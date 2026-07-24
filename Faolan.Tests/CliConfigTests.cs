using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Importer.Cli;
using Xunit;

namespace Faolan.Tests
{
	// The importer's DB config comes from env > command line > faolan.json. The command-line CONFIG PROVIDER pairs
	// SEQUENTIAL tokens, so a valueless flag swallows whatever follows it: `--dry-run --connectionstring "X"` parsed
	// as dry-run="--connectionstring", the connection string never reached the config, and the command silently ran
	// against the CONFIGURED database instead of the one named on the command line. A --dry-run that reports on the
	// wrong database is worse than one that fails outright.
	//
	// These pin the BEHAVIOUR, not the mechanism: the resolved connection must not depend on where the flags sit,
	// and it must beat the config file. (The fix was to stop handing the whole argv to that provider - this layer
	// needs exactly one option, so it reads exactly one option.)
	public class CliConfigTests : IDisposable
	{
		private const string Connection = "Data Source=/tmp/faolan-cliconfig-test.db";
		private const string Decoy = "Data Source=the-json-should-not-win.db";

		// Hermetic: a config file of its own rather than the developer's, and its connection string is a DECOY that
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
			// the flag before the option - the ordering that used to lose the value
			yield return [new[] { "--report", "apply-seed", "--dry-run", "--connectionstring", Connection }];
			// and after it, which happened to work
			yield return [new[] { "--report", "apply-seed", "--connectionstring", Connection, "--dry-run" }];
			// several valueless flags either side
			yield return [new[] { "--dry-run", "--force", "--connectionstring", Connection, "--list" }];
			// a value-taking option elsewhere in the line
			yield return [new[] { "--only", "npcs", "--connectionstring", Connection, "--mode", "overwrite" }];
			// no other flags at all
			yield return [new[] { "--connectionstring", Connection }];
		}

		[Theory, MemberData(nameof(Orderings))]
		public void ResolveDatabase_TakesTheConnectionString_WhereverTheFlagsSit(string[] args)
		{
			Assert.Equal(Connection, CliConfig.ResolveDatabase(args).Connection);
		}

		// With no --connectionstring the config file is what should be used - the override must not fabricate one.
		[Fact]
		public void ResolveDatabase_FallsBackToTheConfigFile()
		{
			Assert.Equal(Decoy, CliConfig.ResolveDatabase(["--dry-run"]).Connection);
		}
	}
}
