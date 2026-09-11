using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Importer.Cli;
using Xunit;

namespace Faolan.Tests
{
	// The importer's DB config comes from env > command line > faolan.json, and the command-line key is the config
	// path - one name per setting whatever the input method.
	//
	// The bug these pin: the command-line config provider pairs sequential tokens, so a valueless flag swallowed
	// whatever followed it. `--dry-run --Database:ConnectionString "X"` parsed as dry-run="--Database:...", the
	// connection string never reached the config, and the command silently ran against the configured database
	// instead of the one named. A --dry-run reporting on the wrong database is worse than one that fails outright.
	//
	// They pin the behaviour, not the mechanism: the resolved connection must not depend on where the flags sit,
	// and it must beat the config file.
	public class CliConfigTests : IDisposable
	{
		private const string Connection = "Data Source=/tmp/faolan-cliconfig-test.db";
		private const string Decoy = "Data Source=the-json-should-not-win.db";

		// Hermetic: a config file of its own rather than the developer's, and its connection string is a decoy that
		// only shows up if the command line was dropped - which is exactly the bug.
		private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"faolan-cliconfig-{Guid.NewGuid():N}.json");

		public CliConfigTests()
		{
			File.WriteAllText(_configPath, $$"""{ "Database": { "Provider": "Sqlite", "ConnectionString": "{{Decoy}}" } }""");
			Environment.SetEnvironmentVariable("FAOLAN_CONFIG", _configPath);
		}

		public void Dispose()
		{
			Environment.SetEnvironmentVariable("FAOLAN_CONFIG", null);
			File.Delete(_configPath);
		}

		public static IEnumerable<object[]> Orderings()
		{
			// the flag before the option
			yield return [new[] { "--report", "apply-seed", "--dry-run", "--Database:ConnectionString", Connection }];
			// and after it
			yield return [new[] { "--report", "apply-seed", "--Database:ConnectionString", Connection, "--dry-run" }];
			// several valueless flags either side
			yield return [new[] { "--dry-run", "--force", "--Database:ConnectionString", Connection, "--list" }];
			// a value-taking option elsewhere in the line
			yield return [new[] { "--only", "npcs", "--Database:ConnectionString", Connection, "--mode", "overwrite" }];
			// no other flags at all
			yield return [new[] { "--Database:ConnectionString", Connection }];
		}

		[Theory, MemberData(nameof(Orderings))]
		public void ResolveDatabase_TakesTheConnectionString_WhereverTheFlagsSit(string[] args)
		{
			var options = ImporterCommand.Parse(args, out var exitCode);
			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.Equal(Connection, CliConfig.ResolveDatabase(options.Configuration).Connection);
		}

		// With no override on the command line the config file is what should be used - and the override must not
		// fabricate one out of nothing.
		[Fact]
		public void ResolveDatabase_FallsBackToTheConfigFile()
		{
			var options = ImporterCommand.Parse(["--dry-run"], out _);
			Assert.NotNull(options);
			Assert.Equal(Decoy, CliConfig.ResolveDatabase(options.Configuration).Connection);
		}

		// A misspelling is an error, not a silent default: `--dryrun` must not parse as "not a dry run" and let the
		// import write for real.
		[Theory]
		[InlineData("--dryrun")]
		[InlineData("--Database:Connection")]
		[InlineData("--captures")] // an option with its value missing
		public void Parse_RejectsWhatItDoesNotUnderstand(string bad)
		{
			Assert.Null(ImporterCommand.Parse([bad], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}

		// ...and the flags it does understand come back typed, wherever they sit.
		[Fact]
		public void Parse_ReadsTheCommandFlags()
		{
			var options = ImporterCommand.Parse(["--dry-run", "--only", "npcs", "--mode", "overwrite", "--force"], out _);
			Assert.NotNull(options);
			Assert.True(options.DryRun);
			Assert.True(options.Force);
			Assert.Equal("npcs", options.Only);
			Assert.Equal("overwrite", options.Mode);
			Assert.False(options.List);
		}
	}
}
