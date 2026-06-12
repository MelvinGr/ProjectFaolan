using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.PacketAnalyzer;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Rebuilds the database from the raw capture sources. The DB is the single source of truth (committed
	// faolan.db); this tool is the one way to (re)populate it from `Captures/` - no embedded data blobs in the
	// server. Imports (all upsert, no wipes): item identities + cast spell ids (inventory/ApplySpell), maps
	// (MapChange), NPC conversations; and with --archive, the ability/spell definitions. Prints a before/after
	// delta. NPCs are next. See docs/archive/faolan-importer-plan.md.
	internal static class Program
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		// The beach (4010) is a committed DB seed: its full world build (MapChange -> spawn burst -> 0x2076 manifest)
		// was hand-assembled from the Strie login capture and is the ONE in-client-proven world blob. The extractor now
		// derives the same full window for every other map (pre-poll spawn burst + the post-poll 0x2076 manifest), but
		// the beach is still skipped on import so the proven seed is never regenerated/perturbed by a capture re-run.
		private const uint BeachMapId = 0x00000FAA;

		// Friendly names for the RDB resource types we care about (from rdbreader's TYPES enum). Used by the
		// type inventory so we can spot the item-template / NPC-stat / dialogue resources.
		private static readonly (uint Type, string Name)[] RdbTypeNames =
		[
			(0, "ScriptGroup"), (1000010, "Filenames"), (1000077, "ContentSpawnDataExport"), (1000078, "AcgSpawnDataExport"), (1000079, "NpcSpawnDataExport"), (1000083, "TreasureDataExport"),
			(1000090, "ResurrectionPointsExport"), (1010001, "3DModel"), (1010004, "Texture/FCTX"), (1010035, "TextScript"), (1010201, "MonsterData"), (1010223, "LuaArchive"), (1010700, "CharacterCreator"), (1020003, "Lip"),
			(1030002, "TextStrings"), (1070003, "PlayfieldDescription")
		];

		private static uint GetNestedValue(JsonElement packet, string property)
		{
			return packet.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("Value", out var v) && v.TryGetUInt32(out var value)
				? value
				: 0;
		}

		private static string? GetOption(string[] args, string name)
		{
			var i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length
				? args[i + 1]
				: null;
		}

		// Upsert (never wipe): update each extracted node's handles + replace its own packets, insert new nodes, and
		// leave any node not in this run's captures untouched.
		private static async Task ImportConversations(DatabaseContext db, List<ConversationEntry> conversations)
		{
			// Conversations are fully derived from the captures, so refresh from a clean slate: this also clears any
			// stale rows from before the NpcName re-key (handle-keyed nodes with no name would otherwise linger).
			var existing = await db
				.ConversationNodes
				.Include(n => n.Packets)
				.ToListAsync();
			db.ConversationPackets.RemoveRange(existing.SelectMany(n => n.Packets));
			db.ConversationNodes.RemoveRange(existing);

			foreach (var e in conversations)
			{
				db.ConversationNodes.Add(new ConversationNode
				{
					TypeId = e.TypeId,
					Mode = e.Mode,
					Field3 = e.Field3,
					CapturedNpcHandle = e.CapturedNpcHandle,
					NpcName = e.NpcName,
					CapturedInstance = e.CapturedInstance,
					Packets = e
						.Packets
						.Select((p, i) => new ConversationPacket
						{
							Sequence = i,
							Data = p
						})
						.ToList()
				});
			}

			await db.SaveChangesAsync();
		}

		// Upsert per map: replace that map's burst (instance + structured IIR records); leave other maps untouched.
		private static async Task ImportMapWorldBursts(DatabaseContext db, List<MapWorldBurstEntry> bursts)
		{
			// Purge existing rows the current extraction no longer produces, so the DB reflects the captures: any row
			// with an unknown captured instance (0, can't be re-addressed) OR a map the extractor now rejects (e.g. a
			// mid-session window dropped by the hygiene gate - see MapWorldBurstExtractor). The beach is exempt (its
			// burst is a committed seed the extractor deliberately skips, not a captured map).
			var keptMaps = bursts
				.Select(e => e.MapId)
				.ToHashSet();
			var stale = await db
				.MapWorldBursts
				.Include(b => b.Entities)
				.Where(b => b.CapturedInstance == 0 || (b.MapId != BeachMapId && !keptMaps.Contains(b.MapId)))
				.ToListAsync();
			if (stale.Count > 0)
			{
				db.MapWorldBurstEntities.RemoveRange(stale.SelectMany(b => b.Entities));
				db.MapWorldBursts.RemoveRange(stale);
			}

			foreach (var e in bursts)
			{
				if (e.MapId == BeachMapId)
					continue; // proven committed seed - never regenerate it from captures (see BeachMapId remark).

				var burst = await db
					.MapWorldBursts
					.Include(b => b.Entities)
					.FirstOrDefaultAsync(b => b.MapId == e.MapId);
				if (burst == null)
				{
					burst = new MapWorldBurst { MapId = e.MapId };
					db.MapWorldBursts.Add(burst);
				}
				else if (burst.Entities != null)
					db.MapWorldBurstEntities.RemoveRange(burst.Entities);

				burst.CapturedInstance = e.CapturedInstance;
				// Decompose each raw download into a structured IIR record (round-trip lossless, see IirRecordCodec),
				// dropping records the replay never sends: the server-generated ones (globals + player-state, emitted
				// live on world entry) and the transient gameplay events (movement/combat/regen from mid-session
				// captures, which stall the client during entry). Storing either is pure dead weight - the replay
				// filters them anyway - so the burst holds only the structural world build.
				burst.Entities = e
					.Packets
					.Select((p, i) => IirRecordCodec.Parse(p, i))
					.Where(r => !IirMessageTypes.IsServerGenerated(r.IirType, r.Handle, e.CapturedInstance) && !IirMessageTypes.IsTransientGameplay(r.IirType))
					.ToList();
			}

			await db.SaveChangesAsync();
		}

		// Upsert by (map, spawn point); never wipe.
		private static async Task ImportNpcSpawns(DatabaseContext db, List<NpcSpawnEntry> spawns)
		{
			foreach (var e in spawns)
			{
				var existing = await db.NpcSpawns.FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z);

				if (existing == null)
					db.NpcSpawns.Add(Populate(new NpcSpawn
					{
						MapId = e.MapId,
						PositionX = e.X,
						PositionY = e.Y,
						PositionZ = e.Z
					}, e));
				else
					Populate(existing, e);
			}

			await db.SaveChangesAsync();
		}

		// --rdb: read the client RDB index and inventory the resource types. The server references text by id (the
		// client renders it from its own RDB), so we DON'T bulk-store strings; this inventory is the foundation for
		// locating + importing the structured data the server actually needs (item-template identity, NPC stats).
		private static void ImportRdb(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			Console.WriteLine($"RDB: {rdb.Count} index entries in {rdbDir}");

			var names = RdbTypeNames.ToDictionary(t => t.Type, t => t.Name);
			Console.WriteLine("  type        count   name");
			Console.WriteLine("  ----------------------------------------");
			foreach (var (type, count) in rdb.TypeCounts())
				Console.WriteLine($"  {type,-10}  {count,6}  {(names.TryGetValue(type, out var n) ? n : "")}");
		}

		// --rdb-items: decrypt the client RDB item-template for every item we have a key for (Item.Data1..4 =
		// the item's ProtoDecryptionKey). Validates the decrypt end-to-end (RDB-Id header must match) and, with
		// --dump-templates <dir>, writes each decrypted template for the field-layout reversing step. See
		// docs/archive/rdb-extraction-plan.md.
		private static async Task ImportRdbItems(DatabaseContext db, string rdbDir, string dumpDir)
		{
			var r = await RdbItemImporter.ImportAsync(db, rdbDir, dumpDir);
			Console.WriteLine($"RDB items: {r.Decrypted}/{r.Keyed} keyed items decrypted+validated; {r.NamesAdded} names filled" + $"  (noEntry={r.NoEntry}, headerMismatch={r.HeaderMismatch}, decryptFailed={r.DecryptFailed})" +
			                  (dumpDir != null
				                  ? $"  -> templates dumped to {dumpDir}"
				                  : ""));
		}

		// Static world objects (decor/containers/zones), deduped by (map, position) like the NPC spawns.
		private static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects)
		{
			foreach (var e in objects)
			{
				var existing = await db.WorldObjects.FirstOrDefaultAsync(o => o.MapId == e.MapId && o.PositionX == e.X && o.PositionY == e.Y && o.PositionZ == e.Z);
				if (existing == null)
					db.WorldObjects.Add(PopulateObject(new WorldObject
					{
						MapId = e.MapId,
						PositionX = e.X,
						PositionY = e.Y,
						PositionZ = e.Z
					}, e));
				else
					PopulateObject(existing, e);
			}

			await db.SaveChangesAsync();
		}

		private static async Task<int> Main(string[] args)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = GetOption(args, "--captures") ?? Path.Combine(RepoRoot, "Captures");
			var archiveDir = GetOption(args, "--archive"); // dir with aocdatabase.sql (one-time ability/spell defs) // TODO: old sql import support can be removed
			var rdbDir = GetOption(args, "--rdb"); // client RDB dir (le.idx + NN.rdbdata) - one-time text/data import
			var rdbItems = args.Contains("--rdb-items"); // decrypt each keyed item's RDB template (needs --rdb)
			var dumpTemplatesDir = GetOption(args, "--dump-templates"); // optional: write decrypted templates here
			var dryRun = args.Contains("--dry-run");

			Console.WriteLine($"Faolan.Importer: db={dbPath}  captures={capturesDir}  archive={archiveDir ?? "(none)"}  rdb={rdbDir ?? "(none)"}  dryRun={dryRun}");

			var doCaptures = Directory.Exists(capturesDir);
			if (!doCaptures && rdbDir == null)
			{
				await Console.Error.WriteLineAsync($"Captures directory not found: {capturesDir}");
				return 1;
			}

			if (!File.Exists(dbPath))
			{
				await Console.Error.WriteLineAsync($"Database not found: {dbPath} (the DB is the source of truth; point --db at it).");
				return 1;
			}

			// One ordered GameServer packet list per capture (conversations need per-capture sequence + metadata).
			var captures = doCaptures
				? ReadCaptures(capturesDir)
				: [];
			var itemPackets = captures
				.SelectMany(c => c.Select(p => p.Bytes))
				.ToList();
			if (doCaptures)
			{
				foreach (var bin in Directory.EnumerateFiles(capturesDir, "*.bin"))
					itemPackets.Add(await File.ReadAllBytesAsync(bin));
			}

			var conversations = ConversationExtractor.Merge(captures.SelectMany(ConversationExtractor.Extract));

			if (doCaptures)
				Console.WriteLine($"Read {captures.Count} captures, {itemPackets.Count} item-candidate packets; extracted {conversations.Count} conversation nodes.");

			if (dryRun)
			{
				Console.WriteLine("[dry-run] no changes written.");
				return 0;
			}

			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan"))
				.Options;
			await using var db = new DatabaseContext(options);
			await db.Database.MigrateAsync();

			// --burst-coverage: read-only report of how much of each map's stored world burst is already reconstructable
			// from the DB tables (the construct-don't-replay gate). Imports nothing; run it before/after a change to see
			// the residual burst-only records shrink.
			if (args.Contains("--burst-coverage"))
			{
				await BurstCoverageReport.RunAsync(db);
				return 0;
			}

			// --rdb-maps: import map names + ids from the client RDB PLAYFIELDDESCRIPTIONDATA (type 1070003). Isolated
			// (no capture import) so it only touches the Maps table.
			if (args.Contains("--rdb-maps"))
			{
				if (rdbDir == null)
				{
					await Console.Error.WriteLineAsync("--rdb-maps needs --rdb <RDB dir>.");
					return 1;
				}

				var rm = await RdbMapImporter.ImportAsync(db, rdbDir);
				Console.WriteLine($"RDB maps: {rm.Found} playfields read ({rm.Gated} v6-gated skipped); {rm.Created} created, {rm.Renamed} renamed.");
				return 0;
			}

			// --rdb-lua: import the LuaArchive (type 1010223) - the conversation/quest script logic - into LuaScripts.
			if (args.Contains("--rdb-lua"))
			{
				if (rdbDir == null)
				{
					await Console.Error.WriteLineAsync("--rdb-lua needs --rdb <RDB dir>.");
					return 1;
				}

				var n = await RdbLuaImporter.ImportAsync(db, rdbDir);
				Console.WriteLine($"RDB lua: imported {n} scripts into LuaScripts.");
				return 0;
			}

			// --rdb-respawns: import resurrection points (type 1000090) for our maps. On-demand records are fetched
			// from the CDN by content hash (--cdn to override the host). Isolated; touches only RespawnPoints.
			if (args.Contains("--rdb-respawns"))
			{
				if (rdbDir == null)
				{
					await Console.Error.WriteLineAsync("--rdb-respawns needs --rdb <RDB dir>.");
					return 1;
				}

				var cdnBase = GetOption(args, "--cdn") ?? RdbRespawnImporter.DefaultCdnBase;
				var store = GetOption(args, "--store"); // archive fetched CDN blobs here (mirror layout); read-first
				var rr = await RdbRespawnImporter.ImportAsync(db, rdbDir, cdnBase, store);
				Console.WriteLine($"RDB respawns: {rr.Points} points across {rr.Maps} maps ({rr.Empty} maps with no points, {rr.FetchFailed} fetch failures)." + (store != null
					? $" Archived to {store}."
					: ""));
				return 0;
			}

			var before = await Snapshot(db);

			if (archiveDir != null)
			{
				var (abilities, classAbilities) = await ArchiveSqlImporter.ImportAsync(db, archiveDir);
				Console.WriteLine($"Archive: {abilities} abilities, {classAbilities} class-ability rows.");
			}

			if (doCaptures)
			{
				await CaptureItemImporter.ImportAsync(db, itemPackets);
				await CaptureMapImporter.ImportAsync(db, itemPackets);
				await CaptureSpellImporter.ImportAsync(db, itemPackets);
				// The starting ability kit = what every new character gets during creation, so detect it from the
				// CREATE-flow captures only (filenames contain "creat"); the rest are higher-level/class abilities.
				var createPackets = Directory
					.EnumerateFiles(capturesDir, "*.json")
					.Where(f => Path
						.GetFileName(f)
						.Contains("creat", StringComparison.OrdinalIgnoreCase))
					.SelectMany(f => ReadJsonCapture(f)
						.Select(p => p.Bytes))
					.ToList();
				var (abSeen, abNew, abUpd) = await CaptureAbilityImporter.ImportAsync(db, itemPackets, createPackets);
				Console.WriteLine($"Abilities (0x2052 grants): {abSeen} seen, {abNew} new, {abUpd} updated.");
				// After items (the equipment references them). Existing names are skipped, never overwritten.
				if (!args.Contains("--skip-characters")) // TODO: CaptureCharacterImporter is not idempotent (UNIQUE CharacterBodies.CharacterId) on a populated DB
				{
					var (chSeen, chAdded, chSkipped) = await CaptureCharacterImporter.ImportAsync(db, captures);
					Console.WriteLine($"Characters: {chSeen} captured, {chAdded} added, {chSkipped} already in DB.");
				}

				await ImportConversations(db, conversations);
				await ImportNpcSpawns(db, NpcSpawnExtractor.Merge(captures.SelectMany(NpcSpawnExtractor.Extract)));
				await ImportWorldObjects(db, WorldObjectExtractor.Merge(captures.SelectMany(WorldObjectExtractor.Extract)));
				await ImportMapWorldBursts(db, MapWorldBurstExtractor.Merge(captures.SelectMany(MapWorldBurstExtractor.Extract)));
			}

			if (rdbDir != null)
			{
				ImportRdb(rdbDir);
				await RdbMorphTargetImporter.ImportAsync(db, rdbDir);
			}

			if (rdbItems)
			{
				if (rdbDir == null)
					await Console.Error.WriteLineAsync("--rdb-items needs --rdb <RDB dir>.");
				else
					await ImportRdbItems(db, rdbDir, dumpTemplatesDir);
			}

			var after = await Snapshot(db);
			PrintDelta(before, after);

			return 0;
		}

		// Writes the transform + lifted identity onto a row (shared by insert/update); MapId/Position are the
		// dedup key, set on insert only.
		private static NpcSpawn Populate(NpcSpawn row, NpcSpawnEntry e)
		{
			row.Name = e.Name;
			row.CapturedHandle = e.Handle;
			row.Data = e.Data;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;
			row.TemplateId = e.TemplateId;
			row.Race = e.Race;
			row.Sex = e.Sex;
			row.Level = e.Level;
			row.MonsterScale = e.MonsterScale;
			row.MonsterData = e.MonsterData;
			row.HeadMesh = e.HeadMesh;
			row.HasEquipment = e.HasEquipment;
			row.Health = e.Health;
			row.Community = e.Community;
			return row;
		}

		private static WorldObject PopulateObject(WorldObject row, WorldObjectEntry e)
		{
			row.SubOpcode = e.SubOpcode;
			row.Name = e.Name;
			row.ObjectId = e.ObjectId;
			row.CapturedHandle = e.Handle;
			row.Data = e.Data;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;
			return row;
		}

		private static void PrintDelta(Dictionary<string, int> before, Dictionary<string, int> after)
		{
			Console.WriteLine("\n  table                 before    after    delta");
			Console.WriteLine("  ------------------------------------------------");
			foreach (var (key, a) in after)
			{
				var b = before[key];
				var delta = a - b;
				var deltaText = (delta >= 0
					? "+"
					: "") + delta;
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

					var ticks = p.TryGetProperty("TimestampTicks", out var t) && t.TryGetInt64(out var tv)
						? tv
						: 0L;
					var isDownload = p.TryGetProperty("IsDownload", out var d) && d.ValueKind == JsonValueKind.True;
					var opcode = GetNestedValue(p, "Opcode");
					var subOpcode = GetNestedValue(p, "SubOpcode");

					list.Add(new CapturedPacket(ticks, isDownload, opcode, subOpcode, bytes));
				}
			}

			list.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
			return list;
		}

		private static List<CapturedPacket> ReadPcapCapture(PacketAnalysisService analysis, string pcap)
		{
			var list = new List<CapturedPacket>();
			var tick = 0L;
			foreach (var p in analysis.AnalyzeCapture(pcap)
				         .Packets)
			{
				if (p.ProtocolName != "GameServer")
					continue;

				list.Add(new CapturedPacket(tick++, p.IsDownload, p.OpcodeValue, p.SubOpcodeValue ?? 0, p.Bytes));
			}

			return list; // analyzer order is chronological
		}

		private static async Task<Dictionary<string, int>> Snapshot(DatabaseContext db)
		{
			return new Dictionary<string, int>
			{
				["Characters"] = await db.Characters.CountAsync(),
				["Items"] = await db.Items.CountAsync(),
				["Items(buildable)"] = await db.Items.CountAsync(i => i.Archetype != 0),
				["Spells"] = await db.Spells.CountAsync(),
				["Abilities"] = await db.Abilities.CountAsync(),
				["ClassAbilities"] = await db.ClassAbilities.CountAsync(),
				["Maps"] = await db.Maps.CountAsync(),
				["MapWorldBursts"] = await db.MapWorldBursts.CountAsync(),
				["NpcSpawns"] = await db.NpcSpawns.CountAsync(),
				["WorldObjects"] = await db.WorldObjects.CountAsync(),
				["ConversationNodes"] = await db.ConversationNodes.CountAsync(),
				["ConversationPackets"] = await db.ConversationPackets.CountAsync()
			};
		}
	}
}
