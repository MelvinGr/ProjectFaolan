using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.GameServer.Spawn;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Importer.Reports;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// Read-only diagnostics (the old --dump-*/--burst-coverage flags), routed through `--report <name>`.
	// These inspect captures or the DB and print; they never write, so they bypass the import runner entirely.
	public static class ReportCli
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		public static async Task<int> RunAsync(string[] args, string report)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = GetOption(args, "--captures") ?? Path.Combine(RepoRoot, "Captures");
			var arg = GetOption(args, "--report-arg");

			switch (report)
			{
				case "conversation-states":
					ConversationStates(LoadCaptures(capturesDir));
					return 0;
				case "anim":
					Anim(LoadCaptures(capturesDir), arg ?? "Casilda");
					return 0;
				case "equip":
					Equip(LoadCaptures(capturesDir), arg ?? "Casilda");
					return 0;
				case "burst-coverage":
					await BurstCoverage(dbPath);
					return 0;
				case "worldobj":
					await WorldObj(dbPath, arg ?? "hangingwoman");
					return 0;
				default:
					await Console.Error.WriteLineAsync($"Unknown report '{report}'. Known: conversation-states, anim, burst-coverage, worldobj.");
					return 1;
			}
		}

		// Per-NPC dialogue screens + mid-conversation quest grants per (capture, NPC) (#37).
		private static void ConversationStates(IReadOnlyList<NamedCapture> captures)
		{
			foreach (var c in captures)
				foreach (var g in ConversationExtractor.Extract(c.Packets)
					         .Where(e => !string.IsNullOrEmpty(e.NpcName))
					         .GroupBy(e => e.NpcName)
					         .OrderBy(g => g.Key))
				{
					var grants = g.Where(e => e.GrantQuestId != 0).OrderBy(e => e.AnswerPath).Select(e => $"'{e.AnswerPath}'->{e.GrantQuestId}");
					Console.WriteLine($"{c.Name} | {g.Key} | screens={g.Count()} | grants=[{string.Join(", ", grants)}]");
				}
		}

		// Per capture, the named NPC's persistent idle animation (CharacterBcc.AnimIdleId) from its FA015807.
		private static void Anim(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(e.Data.AsSpan(8, 4)))..]
						: null;
					var anim = body != null && PlayerSpawnReader.TryReadCharacterMessage(body, out var read)
						? read.Message?.Bcc?.AnimIdleId.ToString() ?? "(no bcc)"
						: "(unreadable)";
					Console.WriteLine($"{c.Name[..Math.Min(40, c.Name.Length)]} | handle 0x{e.Handle:X} | AnimIdleId={anim} | pos=({e.X:0.#},{e.Z:0.#})");
				}
		}

		// Per capture, the named NPC's worn equipment (InventoryEntries: slot + template) from its FA015807 (#267).
		private static void Equip(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(e.Data.AsSpan(8, 4)))..]
						: null;
					if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					{
						Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} | (unreadable)");
						continue;
					}

					var slots = read.Message.InventoryEntries
						.Select(entry => NpcSpawnEquipmentBuilder.TryLift(entry, out var row)
							? $"slot{row.Pos}=tpl{row.Template}[k={row.Key1},{row.Key2},{row.Key3},{row.Key4} lvl={row.Level} hash={row.Hash}]"
							: "slot?=unliftable")
						.ToList();
					Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} pos=({e.X:0.#},{e.Z:0.#})");
					foreach (var s in slots) Console.WriteLine($"    {s}");
				}
		}

		// How much of each map's stored world burst is reconstructable from the DB tables (construct-don't-replay).
		private static async Task BurstCoverage(string dbPath)
		{
			await using var db = OpenDb(dbPath);
			await BurstCoverageReport.RunAsync(db);
		}

		// Build a named world object via the burstless world-entry path and print its hex (render diffing).
		private static async Task WorldObj(string dbPath, string target)
		{
			await using var db = OpenDb(dbPath);
			foreach (var row in await db.WorldObjects.Where(o => o.Name.Contains(target)).ToListAsync())
			{
				var baseOk = WorldObjectBuilder.TryBuild(row, row.CapturedHandle, out var baseBody);
				var rewriteOk = baseOk && SpawnPlayfieldRewriter.TryRewrite(baseBody, 0x01EA985Cu);
				Console.WriteLine($"map {row.MapId} {row.Name} handle 0x{row.CapturedHandle:X} baseBuild={baseOk} rewrite={rewriteOk} datalen={row.Data?.Length} kd={row.KdTreeTail?.Length}");
				if (baseOk)
					Console.WriteLine("  BUILT: " + Convert.ToHexString(baseBody).ToLowerInvariant());
			}
		}

		private static DatabaseContext OpenDb(string dbPath)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan")).Options;
			return new DatabaseContext(options);
		}

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
