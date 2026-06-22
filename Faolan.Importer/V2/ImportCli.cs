using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.V2
{
	// Entry for the v2 importer (invoked from Program.Main when --v2 is present). Parses paths + selection +
	// mode, loads captures once, migrates, and hands a resolved plan to ImportRunner. See
	// docs/importer-v2-plan.md for the CLI spec.
	public static class ImportCli
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		public static async Task<int> RunAsync(string[] args)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = GetOption(args, "--captures") ?? Path.Combine(RepoRoot, "Captures");
			var rdbDir = GetOption(args, "--rdb");

			// --list: catalog the modules and exit (no DB needed).
			if (args.Contains("--list"))
			{
				Console.WriteLine("Importer v2 modules:");
				foreach (var m in ModuleRegistry.All)
					Console.WriteLine($"  {m.Name,-18} [{m.Source}]  {m.Description}"
						+ (m.DependsOn.Count > 0 ? $"  (needs: {string.Join(", ", m.DependsOn)})" : ""));
				return 0;
			}

			var (mode, overrides) = ParseMode(GetOption(args, "--mode"));
			var dryRun = args.Contains("--dry-run");
			var force = args.Contains("--force");

			if (!File.Exists(dbPath))
			{
				await Console.Error.WriteLineAsync($"Database not found: {dbPath} (the DB is the source of truth; point --db at it).");
				return 1;
			}
			if (!Directory.Exists(capturesDir))
			{
				await Console.Error.WriteLineAsync($"Captures directory not found: {capturesDir}");
				return 1;
			}

			// Resolve the requested module set: --only X,Y | --skip X,Y | (default = all non-RDB).
			IReadOnlyList<IImportModule> requested;
			var only = GetOption(args, "--only");
			var skip = GetOption(args, "--skip");
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

			// Hard-skip: a module named in --skip is removed even if a dependency pulled it back in (mirrors v1
			// --skip-characters, where character-state/learned-spells still run against the already-present chars).
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

			Console.WriteLine($"Faolan.Importer v2: db={dbPath}  captures={capturesDir}  rdb={rdbDir ?? "(none)"}");
			var captures = LoadCaptures(capturesDir);

			// Raw packet bytes across every capture PLUS the loose *.bin files (v1's `itemPackets`).
			var rawPackets = captures.SelectMany(c => c.Packets.Select(p => p.Bytes)).ToList();
			foreach (var bin in Directory.EnumerateFiles(capturesDir, "*.bin"))
				rawPackets.Add(await File.ReadAllBytesAsync(bin));

			// Per create-flow capture (filename contains "creat"), its packet bytes - the starting-kit source.
			var createCaptures = captures
				.Where(c => c.Name.Contains("creat", StringComparison.OrdinalIgnoreCase))
				.Select(c => (IReadOnlyList<byte[]>)c.Packets.Select(p => p.Bytes).ToList())
				.ToList();

			Console.WriteLine($"Read {captures.Count} captures ({rawPackets.Count} raw packets, {createCaptures.Count} create-flow).");

			var options = new DbContextOptionsBuilder<DatabaseContext>()
				.UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan"))
				.Options;
			await using var db = new DatabaseContext(options);
			await db.Database.MigrateAsync(); // outside the runner's transaction (migrations can't run inside one)

			var ctx = new ImportContext
			{
				Db = db,
				Captures = captures,
				CapturesDir = capturesDir,
				RdbDir = rdbDir,
				RawPackets = rawPackets,
				CreateCaptures = createCaptures,
				RdbCdnBase = GetOption(args, "--cdn"),
				RdbStoreDir = GetOption(args, "--store"),
				DumpTemplatesDir = GetOption(args, "--dump-templates"),
				Mode = mode,
				ModeOverrides = overrides,
				DryRun = dryRun,
				Force = force,
				Out = Console.Out
			};

			return await ImportRunner.RunAsync(ctx, plan);
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

		private static WriteMode ParseWriteMode(string s) => s.Trim().ToLowerInvariant() switch
		{
			"insert" => WriteMode.Insert,
			"upsert" => WriteMode.Upsert,
			"overwrite" => WriteMode.Overwrite,
			_ => throw new ArgumentException($"Unknown write mode '{s}' (expected insert|upsert|overwrite).")
		};

		private static List<NamedCapture> LoadCaptures(string capturesDir)
		{
			var captures = new List<NamedCapture>();
			foreach (var ts in Directory.EnumerateFiles(capturesDir, "*.tshark.json"))
			{
				var name = Path.GetFileName(ts);
				if (name.EndsWith(".tshark.json", StringComparison.OrdinalIgnoreCase))
					name = name[..^".tshark.json".Length];
				captures.Add(new NamedCapture(name, TsharkCaptureReader.Read(ts)));
			}
			return captures;
		}

		private static string? GetOption(string[] args, string name)
		{
			var i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		}
	}
}
