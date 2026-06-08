using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Helpers;
using Faolan.PacketAnalyzer;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Rebuilds the database from the raw capture sources. The DB is the single source of truth (committed
	// faolan.db); this tool is the one way to (re)populate it from `Captures/` - no embedded data blobs in the
	// server. Imports (all upsert, no wipes): item identities + cast spell ids (inventory/ApplySpell), maps
	// (MapChange), NPC conversations; and with --archive, the ability/spell definitions. Prints a before/after
	// delta. NPCs are next. See docs/faolan-importer-plan.md.
	internal static class Program
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		private static async Task<int> Main(string[] args)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = GetOption(args, "--captures") ?? Path.Combine(RepoRoot, "Captures");
			var archiveDir = GetOption(args, "--archive"); // dir with aocdatabase.sql (one-time ability/spell defs)
			var dryRun = args.Contains("--dry-run");

			Console.WriteLine($"Faolan.Importer: db={dbPath}  captures={capturesDir}  archive={archiveDir ?? "(none)"}  dryRun={dryRun}");

			if (!Directory.Exists(capturesDir))
			{
				Console.Error.WriteLine($"Captures directory not found: {capturesDir}");
				return 1;
			}

			if (!File.Exists(dbPath))
			{
				Console.Error.WriteLine($"Database not found: {dbPath} (the DB is the source of truth; point --db at it).");
				return 1;
			}

			// One ordered GameServer packet list per capture (conversations need per-capture sequence + metadata).
			var captures = ReadCaptures(capturesDir);
			var itemPackets = captures.SelectMany(c => c.Select(p => p.Bytes)).ToList();
			foreach (var bin in Directory.EnumerateFiles(capturesDir, "*.bin"))
				itemPackets.Add(File.ReadAllBytes(bin));

			var conversations = ConversationExtractor.Merge(captures.SelectMany(ConversationExtractor.Extract));

			Console.WriteLine($"Read {captures.Count} captures, {itemPackets.Count} item-candidate packets; extracted {conversations.Count} conversation nodes.");

			if (dryRun)
			{
				Console.WriteLine("[dry-run] no changes written.");
				return 0;
			}

			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan")).Options;
			await using var db = new DatabaseContext(options);
			await db.Database.MigrateAsync();

			var before = await Snapshot(db);

			if (archiveDir != null)
			{
				var (abilities, classAbilities) = await ArchiveSqlImporter.ImportAsync(db, archiveDir);
				Console.WriteLine($"Archive: {abilities} abilities, {classAbilities} class-ability rows.");
			}

			await CaptureItemImporter.ImportAsync(db, itemPackets);
			await CaptureMapImporter.ImportAsync(db, itemPackets);
			await CaptureSpellImporter.ImportAsync(db, itemPackets);
			await ImportConversations(db, conversations);
			await ImportNpcSpawns(db, NpcSpawnExtractor.Merge(captures.SelectMany(NpcSpawnExtractor.Extract)));
			await ImportMapWorldBursts(db, MapWorldBurstExtractor.Merge(captures.SelectMany(MapWorldBurstExtractor.Extract)));
			await SeedBeachBurstFromBlob(db); // beach (4010) uses the proven 71-packet blob, not the captured window

			var after = await Snapshot(db);
			PrintDelta(before, after);

			return 0;
		}

		// Upsert (never wipe): update each extracted node's handles + replace its own packets, insert new nodes, and
		// leave any node not in this run's captures untouched.
		private static async Task ImportConversations(DatabaseContext db, List<ConversationEntry> conversations)
		{
			foreach (var e in conversations)
			{
				var node = await db.ConversationNodes.Include(n => n.Packets)
					.FirstOrDefaultAsync(n => n.TypeId == e.TypeId && n.Mode == e.Mode && n.Field3 == e.Field3);

				if (node == null)
				{
					node = new ConversationNode { TypeId = e.TypeId, Mode = e.Mode, Field3 = e.Field3 };
					db.ConversationNodes.Add(node);
				}
				else if (node.Packets != null)
				{
					db.ConversationPackets.RemoveRange(node.Packets); // refresh this node's packets
				}

				node.CapturedNpcHandle = e.CapturedNpcHandle;
				node.CapturedInstance = e.CapturedInstance;
				node.Packets = e.Packets.Select((p, i) => new ConversationPacket { Sequence = i, Data = p }).ToList();
			}

			await db.SaveChangesAsync();
		}

		// Beach map 4010: seed its world burst from the proven embedded blob (strie-login-worldburst.bin, 71 packets,
		// instance 0x02165EFF) rather than the captured window, so the long-working beach login is byte-identical.
		// The blob file stays in the repo only as this import source (it is no longer embedded in the server).
		private static async Task SeedBeachBurstFromBlob(DatabaseContext db)
		{
			const uint beachMap = 0x00000FAA;
			const uint strieInstance = 0x02165EFF;
			var blobPath = Path.Combine(RepoRoot, "Faolan", "GameServer", "Data", "strie-login-worldburst.bin");
			if (!File.Exists(blobPath))
				return;

			var data = await File.ReadAllBytesAsync(blobPath);
			var packets = new List<byte[]>();
			var offset = 0;
			uint ReadU32()
			{
				var v = (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
				offset += 4;
				return v;
			}

			var count = ReadU32();
			for (var i = 0; i < count; i++)
			{
				var len = (int)ReadU32();
				packets.Add(data[offset..(offset + len)]);
				offset += len;
			}

			var burst = await db.MapWorldBursts.Include(b => b.Packets).FirstOrDefaultAsync(b => b.MapId == beachMap);
			if (burst == null)
			{
				burst = new MapWorldBurst { MapId = beachMap };
				db.MapWorldBursts.Add(burst);
			}
			else if (burst.Packets != null)
			{
				db.MapWorldBurstPackets.RemoveRange(burst.Packets);
			}

			burst.CapturedInstance = strieInstance;
			burst.Packets = packets.Select((p, i) => new MapWorldBurstPacket { Sequence = i, Data = p }).ToList();
			await db.SaveChangesAsync();
		}

		// Upsert per map: replace that map's burst (instance + packets); leave other maps untouched.
		private static async Task ImportMapWorldBursts(DatabaseContext db, List<MapWorldBurstEntry> bursts)
		{
			// Purge any existing rows whose captured instance is outside the 4-byte-varint range - the
			// length-preserving rewrite can't re-address them, so a stale row (e.g. maps 1000/11040 captured
			// 0x110099BD) would break the world build. Merge() already filters new bursts; this clears old ones.
			var stale = await db.MapWorldBursts.Include(b => b.Packets)
				.Where(b => b.CapturedInstance < 0x02000000 || b.CapturedInstance > 0x0FFFFFFF)
				.ToListAsync();
			if (stale.Count > 0)
			{
				db.MapWorldBurstPackets.RemoveRange(stale.SelectMany(b => b.Packets));
				db.MapWorldBursts.RemoveRange(stale);
			}

			foreach (var e in bursts)
			{
				var burst = await db.MapWorldBursts.Include(b => b.Packets).FirstOrDefaultAsync(b => b.MapId == e.MapId);
				if (burst == null)
				{
					burst = new MapWorldBurst { MapId = e.MapId };
					db.MapWorldBursts.Add(burst);
				}
				else if (burst.Packets != null)
				{
					db.MapWorldBurstPackets.RemoveRange(burst.Packets);
				}

				burst.CapturedInstance = e.CapturedInstance;
				burst.Packets = e.Packets.Select((p, i) => new MapWorldBurstPacket { Sequence = i, Data = p }).ToList();
			}

			await db.SaveChangesAsync();
		}

		// Upsert by (map, spawn point); never wipe.
		private static async Task ImportNpcSpawns(DatabaseContext db, List<NpcSpawnEntry> spawns)
		{
			foreach (var e in spawns)
			{
				var existing = await db.NpcSpawns.FirstOrDefaultAsync(n =>
					n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z);

				if (existing == null)
				{
					db.NpcSpawns.Add(new NpcSpawn
					{
						MapId = e.MapId, Name = e.Name, CapturedHandle = e.Handle,
						PositionX = e.X, PositionY = e.Y, PositionZ = e.Z, Data = e.Data
					});
				}
				else
				{
					existing.Name = e.Name;
					existing.CapturedHandle = e.Handle;
					existing.Data = e.Data;
				}
			}

			await db.SaveChangesAsync();
		}

		private static async Task<Dictionary<string, int>> Snapshot(DatabaseContext db) => new()
		{
			["Items"] = await db.Items.CountAsync(),
			["Items(buildable)"] = await db.Items.CountAsync(i => i.Archetype != 0),
			["Spells"] = await db.Spells.CountAsync(),
			["Abilities"] = await db.Abilities.CountAsync(),
			["ClassAbilities"] = await db.ClassAbilities.CountAsync(),
			["Maps"] = await db.Maps.CountAsync(),
			["MapWorldBursts"] = await db.MapWorldBursts.CountAsync(),
			["NpcSpawns"] = await db.NpcSpawns.CountAsync(),
			["ConversationNodes"] = await db.ConversationNodes.CountAsync(),
			["ConversationPackets"] = await db.ConversationPackets.CountAsync()
		};

		private static void PrintDelta(Dictionary<string, int> before, Dictionary<string, int> after)
		{
			Console.WriteLine("\n  table                 before    after    delta");
			Console.WriteLine("  ------------------------------------------------");
			foreach (var (key, a) in after)
			{
				var b = before[key];
				var delta = a - b;
				var deltaText = (delta >= 0 ? "+" : "") + delta;
				Console.WriteLine($"  {key,-20} {b,7} {a,8} {deltaText,7}");
			}
		}

		private static List<List<CapturedPacket>> ReadCaptures(string capturesDir)
		{
			var captures = new List<List<CapturedPacket>>();

			foreach (var json in Directory.EnumerateFiles(capturesDir, "*.json"))
				captures.Add(ReadJsonCapture(json));

			var analysis = new PacketAnalysisService();
			foreach (var pcap in Directory.EnumerateFiles(capturesDir, "*.pcapng"))
			{
				if (File.Exists(Path.ChangeExtension(pcap, ".json")))
					continue; // covered by its dump

				try
				{
					captures.Add(ReadPcapCapture(analysis, pcap));
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"  skip {Path.GetFileName(pcap)}: {ex.Message}");
				}
			}

			return captures;
		}

		private static List<CapturedPacket> ReadPcapCapture(PacketAnalysisService analysis, string pcap)
		{
			var list = new List<CapturedPacket>();
			var tick = 0L;
			foreach (var p in analysis.AnalyzeCapture(pcap).Packets)
			{
				if (p.ProtocolName != "GameServer")
					continue;

				list.Add(new CapturedPacket(tick++, p.IsDownload, p.OpcodeValue, p.SubOpcodeValue ?? 0, p.Bytes));
			}

			return list; // analyzer order is chronological
		}

		private static List<CapturedPacket> ReadJsonCapture(string jsonPath)
		{
			var list = new List<CapturedPacket>();

			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
			}
			catch
			{
				return list;
			}

			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
					return list;

				foreach (var p in packets.EnumerateArray())
				{
					if (p.TryGetProperty("ProtocolName", out var proto) && proto.GetString() != "GameServer")
						continue;
					if (!p.TryGetProperty("RawBytesBase64", out var raw) || raw.ValueKind != JsonValueKind.String)
						continue;

					byte[] bytes;
					try
					{
						bytes = Convert.FromBase64String(raw.GetString()!);
					}
					catch
					{
						continue;
					}

					var ticks = p.TryGetProperty("TimestampTicks", out var t) && t.TryGetInt64(out var tv) ? tv : 0L;
					var isDownload = p.TryGetProperty("IsDownload", out var d) && d.ValueKind == JsonValueKind.True;
					var opcode = GetNestedValue(p, "Opcode");
					var subOpcode = GetNestedValue(p, "SubOpcode");

					list.Add(new CapturedPacket(ticks, isDownload, opcode, subOpcode, bytes));
				}
			}

			list.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
			return list;
		}

		private static uint GetNestedValue(JsonElement packet, string property)
		{
			return packet.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("Value", out var v) && v.TryGetUInt32(out var value)
				? value
				: 0;
		}

		private static string? GetOption(string[] args, string name)
		{
			var i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
		}
	}
}
