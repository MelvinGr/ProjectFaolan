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
				case "spell-fields":
					SpellFields(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg);
					return 0;
				case "rdb-assets":
					RdbAssets(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg, GetOption(args, "--out"), args.Contains("--dump-mesh"));
					return 0;
				default:
					await Console.Error.WriteLineAsync($"Unknown report '{report}'. Known: conversation-states, anim, burst-coverage, worldobj, spell-fields, rdb-assets.");
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

		// TEMP (#274): discover the spell DURATION field in the RDB spell-data records (type 1040005). Dumps either a
		// specific spell's full increasing field table (--report-arg <decimal id>) or, with no arg, a histogram of how
		// often each field id appears across all named spell records (so a duration field stands out as near-universal
		// with ms-range values). Field 0x126=cast/0x11F=range/0x15C=damage are already known.
		private static void SpellFields(string rdbDir, string arg)
		{
			using var rdb = new Faolan.Importer.Rdb.RdbReader(rdbDir);
			var counts = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<uint>>();
			uint? wanted = uint.TryParse(arg, out var w) ? w : null;
			foreach (var e in rdb.EntriesOfType(1040005))
			{
				if (e.Size == 0 || e.File == 0xff) continue;
				byte[] body;
				try { body = rdb.ReadDecompressed(e); } catch { continue; }
				if (body.Length < 12) continue;
				var name = Faolan.Importer.Rdb.RdbItemImporter.ExtractName(body);
				if (string.IsNullOrEmpty(name)) continue;
				var fields = ReadIncreasingFields(body.AsSpan(12));
				if (wanted is uint id && e.Id == id)
				{
					Console.WriteLine($"spell {e.Id} '{name}':");
					foreach (var kv in fields.OrderBy(k => k.Key))
						Console.WriteLine($"  0x{kv.Key:X3} ({kv.Key,4}) = {kv.Value}  (0x{kv.Value:X})");
					return;
				}
				foreach (var kv in fields)
				{
					counts[kv.Key] = counts.GetValueOrDefault(kv.Key) + 1;
					if (!samples.TryGetValue(kv.Key, out var l)) samples[kv.Key] = l = new();
					if (l.Count < 8) l.Add(kv.Value);
				}
			}
			if (wanted is not null) { Console.WriteLine($"spell {wanted} not found / unnamed"); return; }
			foreach (var kv in counts.OrderByDescending(k => k.Value).Take(40))
				Console.WriteLine($"0x{kv.Key:X3} ({kv.Key,4}) count={kv.Value,5}  samples={string.Join(",", samples[kv.Key])}");
		}

		// RDB -> 3D-model asset graph (Gitea #275, Phase 0). With no --report-arg: a corpus summary (how many
		// MESHINDEXFILE manifests resolve to geometry, how their textures are split local vs CDN). With --report-arg
		// <meshIndexId>: print that model's graph (RenderMesh id, materials, texture refs) and, if --out <dir> is given,
		// extract its FCTX textures to DDS (+ --dump-mesh to also write the raw 1010001 geometry blob for the Phase 1 spike).
		private static void RdbAssets(string rdbDir, string arg, string? outDir, bool dumpMesh)
		{
			using var rdb = new Faolan.Importer.Rdb.RdbReader(rdbDir);

			if (uint.TryParse(arg, out var meshIndexId))
			{
				var result = Faolan.Importer.Rdb.Assets.RdbAssetExtractor.Extract(rdb, meshIndexId, outDir, dumpMesh);
				if (!result.Ok || result.Manifest is not { } m)
				{
					Console.WriteLine($"MESHINDEXFILE {meshIndexId} not found in RDB.");
					return;
				}

				Console.WriteLine($"MESHINDEXFILE {meshIndexId}");
				Console.WriteLine($"  source       : {m.Source}");
				Console.WriteLine($"  bounds       : {m.Bounds}");
				Console.WriteLine($"  renderMesh   : {Describe(m.RenderMesh)}{(m.RenderMeshDump != null ? $" -> {m.RenderMeshDump}" : "")}");
				Console.WriteLine($"  collisionMesh: {Describe(m.CollisionMesh)}");
				Console.WriteLine($"  materials    : {m.Materials.Count}");
				foreach (var mat in m.Materials)
					Console.WriteLine($"    - {mat.Name}: [{string.Join(", ", mat.Textures)}]");
				Console.WriteLine($"  textures     : {m.Textures.Count}");
				foreach (var t in m.Textures)
					Console.WriteLine($"    - {t.Ref,-18} {t.Status}{(t.Output != null ? $" -> {t.Output}" : "")}");
				if (outDir != null)
					Console.WriteLine($"  written to   : {outDir}");
				return;
			}

			// Corpus summary.
			int manifests = 0, withGeometry = 0, geomLocal = 0, geomCdn = 0, texLocal = 0, texCdn = 0, texMissing = 0;
			foreach (var e in rdb.EntriesOfType(Faolan.Importer.Rdb.Assets.RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0 || !e.IsLocal)
					continue;

				manifests++;
				Faolan.Importer.Rdb.Assets.MeshIndexFile mesh;
				try { mesh = Faolan.Importer.Rdb.Assets.MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e)); }
				catch { continue; }

				if (mesh.RenderMeshId is { } rmId)
				{
					withGeometry++;
					if (rdb.TryGet(Faolan.Importer.Rdb.Assets.RdbAssetExtractor.RenderMeshType, rmId, out var rm))
						(rm.IsLocal ? ref geomLocal : ref geomCdn)++;
				}

				foreach (var t in mesh.UniqueTextures)
				{
					if (!rdb.TryGet(t.Type!.Value, t.Id, out var te)) texMissing++;
					else if (te.IsLocal) texLocal++;
					else texCdn++;
				}
			}

			Console.WriteLine($"MESHINDEXFILE manifests (local) : {manifests}");
			Console.WriteLine($"  with a RenderMesh ref         : {withGeometry} (geometry local={geomLocal} cdn={geomCdn})");
			Console.WriteLine($"  texture refs: local={texLocal} cdn={texCdn} missing={texMissing}");
			Console.WriteLine("Pass --report-arg <meshIndexId> [--out <dir>] [--dump-mesh] to extract one model.");
		}

		private static string Describe(Faolan.Importer.Rdb.Assets.RdbAssetExtractor.ResourceResult? r) =>
			r is { } v ? $"id={v.Id} type={v.Type} {v.Availability} ({v.Size} B)" : "(none)";

		// Mirror of RdbSpellImporter.ReadIncreasingFields (kept private there); used only by the spell-fields report.
		private static Dictionary<uint, uint> ReadIncreasingFields(ReadOnlySpan<byte> body)
		{
			var words = body.Length / 4;
			Dictionary<uint, uint> best = new();
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0; var haveLast = false; var cur = new Dictionary<uint, uint>();
				for (var j = start; j + 1 < words; j++)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last)) break;
					cur[fid] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice((j + 1) * 4, 4));
					last = fid; haveLast = true; j++;
				}
				if (cur.Count > best.Count) best = cur;
			}
			return best;
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
