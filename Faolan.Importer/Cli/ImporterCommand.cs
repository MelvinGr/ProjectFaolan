using System.Collections.Generic;
using System.CommandLine;
using Faolan.Core.Cli;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Importer.Cli
{
	// The importer's command line, declared once (System.CommandLine) instead of scanned ad hoc.
	//
	// What the declaration buys over the old `GetOption(args, "--x")` scans: an unknown or misspelled option is now
	// an ERROR rather than silently ignored (`--dryrun` used to mean "not a dry run"), a missing value is an error
	// rather than a null, `--help` lists everything, and the parse happens ONCE up front instead of each caller
	// re-reading argv with its own idea of the grammar.
	//
	// Two kinds of option live here and they are handled differently:
	//   - CONFIG KEYS (Database:ConnectionString, Database:Provider, RdbDir) name a setting that also lives in
	//     faolan.json and the environment. They are parsed here but resolved through CliConfig, so the precedence
	//     (env > command line > json) is unchanged and the NAME is the config path - one spelling per setting.
	//   - COMMAND options (--only, --dry-run, --report, ...) are per-invocation and belong to the CLI alone.
	public static class ImporterCommand
	{
		// Config keys: the option name IS the configuration path, and its description + default come from the one
		// catalog (FaolanSettings).
		private static readonly Option<string> ConnectionString = FaolanCommandLine.ConfigOption(Keys.DatabaseConnectionString);
		private static readonly Option<string> Provider = FaolanCommandLine.ConfigOption(Keys.DatabaseProvider);
		private static readonly Option<string> RdbDir = FaolanCommandLine.ConfigOption(Keys.ContentRdbDir);

		// Command options.
		private static readonly Option<string> Captures = new("--captures") { Description = "The capture corpus directory (the private ProjectFaolan-Data repo's Captures/)." };
		private static readonly Option<string> Only = new("--only") { Description = "Run ONLY these modules (comma-separated). Default: every module in the default set." };
		private static readonly Option<string> Skip = new("--skip") { Description = "Skip these modules (comma-separated)." };
		private static readonly Option<string> Mode = new("--mode") { Description = "insert | upsert (default) | overwrite, globally or per module (npcs=overwrite,items=insert)." };
		private static readonly Option<bool> DryRun = new("--dry-run") { Description = "Write nothing: run in a rolled-back transaction and print the per-table delta." };
		private static readonly Option<bool> Force = new("--force") { Description = "Run modules that are otherwise suppressed (out-of-DB side effects)." };
		private static readonly Option<bool> List = new("--list") { Description = "List every module (and which are opt-in) and exit." };
		private static readonly Option<string> Report = new("--report") { Description = "Run a read-only diagnostic instead of importing." };
		private static readonly Option<string> ReportArg = new("--report-arg") { Description = "The report's argument (its meaning is per report)." };
		private static readonly Option<string> Out = new("--out") { Description = "Output directory for reports that write files." };
		private static readonly Option<string> Skeleton = new("--skeleton") { Description = "Skeleton id for the rdb-rig report." };
		private static readonly Option<string> TerrainOut = new("--terrain-out") { Description = "Output directory for the terrain-export report (default: ./Terrain)." };
		private static readonly Option<bool> DumpMesh = new("--dump-mesh") { Description = "Dump mesh payloads in the rdb-assets report." };
		private static readonly Option<string> DumpTemplates = new("--dump-templates") { Description = "Dump decrypted item templates to this directory." };
		private static readonly Option<string> RdbProxy = new("--rdb-proxy") { Description = "Faolan.Web.Content mirror address for on-demand RDB fetches, e.g. http://127.0.0.1:7081 (the /gameupdate/upm/rdb route is appended)." };

		// Parses argv. Returns null when the parser handled the invocation itself (--help, or a parse error, which it
		// reports); the exit code is then in `exitCode`.
		public static ImporterOptions? Parse(string[] args, out int exitCode)
		{
			var command = new RootCommand("Faolan importer - rebuilds the game data in the database from packet captures and the client RDB.")
			{
				ConnectionString, Provider, RdbDir,
				Captures, Only, Skip, Mode, DryRun, Force, List,
				Report, ReportArg, Out, Skeleton, TerrainOut, DumpMesh, DumpTemplates, RdbProxy
			};

			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			return new ImporterOptions(
				result.GetValue(Captures), result.GetValue(Only), result.GetValue(Skip), result.GetValue(Mode),
				result.GetValue(DryRun), result.GetValue(Force), result.GetValue(List),
				result.GetValue(Report), result.GetValue(ReportArg), result.GetValue(Out), result.GetValue(Skeleton), result.GetValue(TerrainOut),
				result.GetValue(DumpMesh), result.GetValue(DumpTemplates), result.GetValue(RdbProxy),
				ConfigOverrides(result));
		}

		private static Dictionary<string, string?> ConfigOverrides(ParseResult result)
		{
			return FaolanCommandLine.Overrides(result, ConnectionString, Provider, RdbDir);
		}
	}

	// Everything the importer was scraping out of argv, parsed once.
	public sealed record ImporterOptions(
		string? Captures, string? Only, string? Skip, string? Mode,
		bool DryRun, bool Force, bool List,
		string? Report, string? ReportArg, string? Out, string? Skeleton, string? TerrainOut,
		bool DumpMesh, string? DumpTemplates, string? RdbProxy,
		Dictionary<string, string?> ConfigOverrides);
}
