using System.CommandLine;
using Faolan.Core.Cli;
using Microsoft.Extensions.Configuration;
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
		// Config keys: the option name IS the configuration path, and its description, TYPE and default come from the
		// one catalog (FaolanSettings) - these are that catalog's own options, which is why they are the non-generic
		// Option rather than Option<string> (`--Database:Provider` is an Option<FaolanDbProvider>, so a bad engine name
		// is a parse error listing the ones that exist).
		private static readonly Option ConnectionString = FaolanSettings.Get(Keys.DatabaseConnectionString);
		private static readonly Option Provider = FaolanSettings.Get(Keys.DatabaseProvider);
		private static readonly Option RdbDir = FaolanSettings.Get(Keys.ContentRdbDir);

		// Command options.
		private static readonly Option<string> Captures = new Option<string>("--captures") { Description = "The capture corpus directory (the private ProjectFaolan-Data repo's Captures/)." };
		private static readonly Option<string> Only = new Option<string>("--only") { Description = "Run ONLY these modules (comma-separated). Default: every module in the default set." };
		private static readonly Option<string> Skip = new Option<string>("--skip") { Description = "Skip these modules (comma-separated)." };
		private static readonly Option<string> Mode = new Option<string>("--mode") { Description = "insert | upsert (default) | overwrite, globally or per module (npcs=overwrite,items=insert)." };
		private static readonly Option<bool> DryRun = new Option<bool>("--dry-run") { Description = "Write nothing: run in a rolled-back transaction and print the per-table delta." };
		private static readonly Option<bool> Force = new Option<bool>("--force") { Description = "Run modules that are otherwise suppressed (out-of-DB side effects)." };
		private static readonly Option<bool> List = new Option<bool>("--list") { Description = "List every module (and which are opt-in) and exit." };
		private static readonly Option<string> Report = new Option<string>("--report") { Description = "Run a read-only diagnostic instead of importing." };
		private static readonly Option<string> ReportArg = new Option<string>("--report-arg") { Description = "The report's argument (its meaning is per report)." };
		private static readonly Option<string> Out = new Option<string>("--out") { Description = "Output directory for reports that write files." };
		private static readonly Option<string> Skeleton = new Option<string>("--skeleton") { Description = "Skeleton id for the rdb-rig report." };
		private static readonly Option<string> TerrainOut = new Option<string>("--terrain-out") { Description = "Output directory for the terrain-export report (default: ./Terrain)." };
		private static readonly Option<bool> DumpMesh = new Option<bool>("--dump-mesh") { Description = "Dump mesh payloads in the rdb-assets report." };
		private static readonly Option<string> DumpTemplates = new Option<string>("--dump-templates") { Description = "Dump decrypted item templates to this directory." };
		private static readonly Option<string> RdbProxy = new Option<string>("--rdb-proxy") { Description = "Faolan.Web.Content mirror address for on-demand RDB fetches, e.g. http://127.0.0.1:7081 (the /gameupdate/upm/rdb route is appended)." };

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
				FaolanCommandLine.Build(result, ConnectionString, Provider, RdbDir));
		}
	}

	// Everything the importer was scraping out of argv, parsed once - including the shared CONFIGURATION its three
	// config keys contributed to, resolved once here. It used to carry the override layer instead, and every reader
	// rebuilt the whole stack (both json files and the environment) to look one key up: eight sites did that.
	public sealed record ImporterOptions(
		string? Captures, string? Only, string? Skip, string? Mode,
		bool DryRun, bool Force, bool List,
		string? Report, string? ReportArg, string? Out, string? Skeleton, string? TerrainOut,
		bool DumpMesh, string? DumpTemplates, string? RdbProxy,
		IConfiguration Configuration);
}
