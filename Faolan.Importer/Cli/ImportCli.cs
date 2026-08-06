using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Microsoft.Data.Sqlite;

namespace Faolan.Importer.Cli
{
	// Importer entry point (invoked from Program.Main). Parses paths + selection + mode, loads captures once,
	// migrates, and hands a resolved plan to ImportRunner. See docs/tooling/importer.md for the CLI spec.
	public static class ImportCli
	{
		public static async Task<int> RunAsync(ImporterOptions options)
		{
			var capturesDir = options.Captures;
			var rdbDir = CliConfig.Resolve(options.ConfigOverrides)["RdbDir"];

			// --report <name>: read-only diagnostics (no import, bypasses the runner).
			if (options.Report != null)
				return await ReportCli.RunAsync(options, options.Report);

			// --list: catalog the modules and exit (no DB needed).
			if (options.List)
			{
				Console.WriteLine("Importer modules:");
				foreach (var m in ModuleRegistry.All)
				{
					Console.WriteLine($"  {m.Name,-18} [{m.Source}]{(m.InDefaultSet ? "" : " (opt-in)")}  {m.Description}" + (m.DependsOn.Count > 0
						? $"  (needs: {string.Join(", ", m.DependsOn)})"
						: ""));
				}

				Console.WriteLine("\nReports (read-only): --report coverage | conversation-states | anim | bcc-census | bcc-attack-tail | combo-swings | creature-races | worldobj | rdb-tweaks | rdb-class-vitals | item-stat-scaling  [--report-arg <name>]");
				return 0;
			}

			var (mode, overrides) = ParseMode(options.Mode);
			var dryRun = options.DryRun;
			var force = options.Force;

			// Provider + connection come from the SHARED config (the same DB the server uses); see CliConfig.
			// `--Database:ConnectionString` overrides that key in the command-line layer (env > cmdline > json); the provider follows config.
			var (provider, connection) = CliConfig.ResolveDatabase(options.ConfigOverrides);

			// A SQLite DB is a FILE that must already exist (the DB is the source of truth). Postgres targets a server.
			if (provider == FaolanDbProvider.Sqlite)
			{
				var dbFile = new SqliteConnectionStringBuilder(connection).DataSource;
				if (!File.Exists(dbFile))
				{
					await Console.Error.WriteLineAsync(
						$"Database not found: {dbFile} (the DB is the source of truth; set Database:ConnectionString in faolan.json, or pass --Database:ConnectionString \"Data Source=<path>\").");
					return 1;
				}
			}

			// Resolve the requested module set: --only X,Y | --skip X,Y | (default = all non-RDB).
			IReadOnlyList<IImportModule> requested;
			var only = options.Only;
			var skip = options.Skip;
			if (only != null)
			{
				requested = ResolveNames(only, out var unknownOnly);
				if (unknownOnly.Count > 0)
				{
					await Console.Error.WriteLineAsync($"Unknown module(s): {string.Join(", ", unknownOnly)}. Use --list.");
					return 1;
				}
			}
			else
			{
				var skipNames = (skip ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
				requested = ModuleRegistry.DefaultSet.Where(m => !skipNames.Contains(m.Name)).ToList();
			}

			var plan = ModuleRegistry.Plan(requested);

			// Hard-skip: a module named in --skip is removed even if a dependency pulled it back in (e.g.
			// --skip characters, where character-state/learned-spells still run against the already-present chars).
			if (skip != null)
			{
				var skipNames = skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
				plan = plan.Where(m => !skipNames.Contains(m.Name)).ToList();
			}

			// RDB modules are opt-in and need --rdb; fail fast rather than silently skipping a requested module.
			var missingRdb = plan.Where(m => m.Source == ImportSource.Rdb && rdbDir == null).Select(m => m.Name).ToList();
			if (missingRdb.Count > 0)
			{
				await Console.Error.WriteLineAsync($"Module(s) {string.Join(", ", missingRdb)} need --rdb <RDB dir>.");
				return 1;
			}

			// The corpus is needed by the modules that READ it, and only by those: a plan of nothing but Db-source
			// modules (`--only spell-effects`, whose rows are decoded facts) runs on a plain clone with no data repo.
			var capturesPath = capturesDir != null && Directory.Exists(capturesDir)
				? capturesDir
				: null;
			if (capturesPath == null && plan.Any(m => m.Source == ImportSource.Captures))
			{
				await Console.Error.WriteLineAsync($"Captures directory not found: {capturesDir}");
				await Console.Error.WriteLineAsync("  The corpus is NOT in this repo - it lives in the private ProjectFaolan-Data repo");
				await Console.Error.WriteLineAsync("  (see CLAUDE.md \"Capture data\"). Pass --captures <dir> pointing at its Captures/ folder.");
				return 1;
			}

			Console.WriteLine($"Faolan.Importer: {provider} [{connection}]  captures={capturesPath ?? "(none - no module needs them)"}  rdb={rdbDir ?? "(none)"}");
			var captures = capturesPath != null
				? NamedCapture.LoadAll(capturesPath)
				: [];

			// Raw packet bytes across every capture PLUS the loose *.bin files (the `itemPackets` source).
			var rawPackets = captures.SelectMany(c => c.Packets.Select(p => p.Bytes)).ToList();
			if (capturesPath != null)
			{
				foreach (var bin in Directory.EnumerateFiles(capturesPath, "*.bin"))
					rawPackets.Add(await File.ReadAllBytesAsync(bin));
			}

			// Per create-flow capture (filename contains "creat"), its packet bytes - the starting-kit source.
			var createCaptures = captures.Where(c => c.Name.Contains("creat", StringComparison.OrdinalIgnoreCase)).Select(c => (IReadOnlyList<byte[]>)c.Packets.Select(p => p.Bytes).ToList()).ToList();

			Console.WriteLine($"Read {captures.Count} captures ({rawPackets.Count} raw packets, {createCaptures.Count} create-flow).");

			await using var db = DatabaseExtensions.CreateContext(provider, connection);
			// Migrate + seed the default realm/account before any module runs - the same sequence the server runs
			// (DatabaseInitializer). Migrations run here, outside the runner's transaction (they can't run inside one).
			// seedGameData: false ON PURPOSE - the importer's whole job is to BUILD that content from captures, so
			// loading faolan.sql first would import on top of a copy of its own previous output and blur what this run
			// actually produced (the --dry-run per-table delta especially).
			var seeded = await DatabaseInitializer.InitializeAsync(db, false);
			if (seeded.RealmCreated)
				Console.WriteLine("Importer: no realm present - inserted the default realm.");
			if (seeded.AccountCreated)
				Console.WriteLine("Importer: no account present - inserted the default account.");

			var ctx = new ImportContext
			{
				Db = db,
				Captures = captures,
				CapturesDir = capturesPath ?? "",
				RdbDir = rdbDir,
				RawPackets = rawPackets,
				CreateCaptures = createCaptures,
				RdbProxyBase = options.RdbProxy ?? ImportContext.DefaultProxyBase(CliConfig.Resolve(options.ConfigOverrides)),
				DumpTemplatesDir = options.DumpTemplates,
				Mode = mode,
				ModeOverrides = overrides,
				DryRun = dryRun,
				Force = force,
				Out = Console.Out
			};

			return await ImportRunner.RunAsync(ctx, plan);
		}

		// --mode upsert | insert | overwrite, with optional per-module overrides:
		//   --mode upsert,npcs=overwrite,items=insert   (a bare token sets the global default)
		private static (WriteMode Mode, Dictionary<string, WriteMode> Overrides) ParseMode(string? spec)
		{
			var mode = WriteMode.Upsert;
			var overrides = new Dictionary<string, WriteMode>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(spec))
				return (mode, overrides);

			foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var eq = token.IndexOf('=');
				if (eq < 0)
					mode = ParseWriteMode(token);
				else
					overrides[token[..eq]] = ParseWriteMode(token[(eq + 1)..]);
			}

			return (mode, overrides);
		}

		private static WriteMode ParseWriteMode(string s)
		{
			return s.Trim().ToLowerInvariant() switch
			{
				"insert"    => WriteMode.Insert,
				"upsert"    => WriteMode.Upsert,
				"overwrite" => WriteMode.Overwrite,
				_           => throw new ArgumentException($"Unknown write mode '{s}' (expected insert|upsert|overwrite).")
			};
		}

		private static IReadOnlyList<IImportModule> ResolveNames(string csv, out List<string> unknown)
		{
			unknown = [];
			var modules = new List<IImportModule>();
			foreach (var name in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var m = ModuleRegistry.Find(name);
				if (m == null)
					unknown.Add(name);
				else
					modules.Add(m);
			}

			return modules;
		}
	}
}
