using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Spawn;
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
					AnswerPath = e.AnswerPath,
					CapturedNpcHandle = e.CapturedNpcHandle,
					NpcName = e.NpcName,
					CapturedInstance = e.CapturedInstance,
					ConditionScript = e.ConditionScript,
					GrantQuestId = e.GrantQuestId,
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

		// Quest journal-content definitions (QuestDefinition + QuestTextRef) are fully derived from the captures, so
		// refresh each harvested quest wholesale (replace its text refs); leave un-harvested quests untouched.
		private static async Task ImportQuestDefinitions(DatabaseContext db, List<QuestDefinition> definitions)
		{
			var added = 0;
			var updated = 0;
			foreach (var def in definitions)
			{
				var existing = await db
					.QuestDefinitions
					.Include(q => q.TextRefs)
					.FirstOrDefaultAsync(q => q.QuestId == def.QuestId);

				if (existing == null)
				{
					db.QuestDefinitions.Add(def);
					added++;
					continue;
				}

				existing.AreaId = def.AreaId;
				existing.GoalId = def.GoalId;
				existing.MapId = def.MapId;
				existing.MarkerX = def.MarkerX;
				existing.MarkerY = def.MarkerY;
				db.QuestTextRefs.RemoveRange(existing.TextRefs);
				existing.TextRefs = def.TextRefs;
				updated++;
			}

			await db.SaveChangesAsync();
			Console.WriteLine($"Quest definitions (journal content): {definitions.Count} harvested, {added} new, {updated} updated.");

			await SeedBeachQuestGoals(db);
			await SeedKillQuestTargets(db);
			await SeedCasildaQuestSnapshot(db);
		}

		// Casilda's quest 13479 snapshot Quest_c scalars (#255): the journal-record fields that live ONLY in the
		// FullChar snapshot, not the QuestFullUpdate/Journal packets the extractor reads. Decoded from the
		// compeltefirstquest/Stihetfv/Strie login snapshots (byte-identical across that quest's active + completed
		// states, so per-quest constants). Lets the multi-quest BuildQuestPortion generate 13479's record from the DB.
		// Idempotent - set every run. (13479's header text refs + AreaId are already harvested by the extractor.)
		private static async Task SeedCasildaQuestSnapshot(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs).FirstOrDefaultAsync(q => q.QuestId == 13479);
			if (quest == null)
				return;
			quest.SnapshotState = 3;
			quest.SnapshotN = 3;
			quest.SnapshotFieldEc = 1;
			quest.SnapshotFieldF0 = 80;
			quest.SnapshotFieldF8 = 7;
			quest.SnapshotField28 = 80;
			// 13479's quest-index block (#255): its own "RRL3" goal-table magic, table fields 1/3/0, and its 4 goal
			// steps. In a multi-quest index this block follows the beach quest's; the chain link is by areaId.
			quest.IndexMagic = 0x52524C33;
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 3;
			quest.IndexTableField3 = 0;
			quest.IndexGoalSteps = "13479,13563,13480,12407";

			// 13479's snapshot @609 objective goal (decoded from the Strie/Stihetfv login snapshots, #255): goal 5744,
			// 1 step, separator 12, no subId, map 4010 marker (327,678), the two undecoded reserved words 70099/0x542D5354,
			// and the objective (cat 50304) + trailing LOCALIZED (cat 51000, flags "m") text - keys taken from the
			// already-harvested TextRefs so they stay byte-exact. Lets QuestObjectiveRegion.Build emit 13479's region.
			var goal = quest.Goals.FirstOrDefault(g => g.GoalId == 5744);
			if (goal != null)
			{
				goal.Sequence = 1;
				goal.Separator = 12;
				goal.SubId = 0;
				goal.MapId = 4010;
				goal.MarkerX = 327;
				goal.MarkerY = 678;
				goal.Extra5 = 70099;
				goal.Extra6 = 0x542D5354;
				goal.ObjectiveRefId = 5744;
				goal.ObjectiveCategory = 50304;
				goal.ObjectiveKey = quest.TextRefs.FirstOrDefault(t => t.Slot == QuestTextSlot.Objective)?.KeyText;
				goal.TrailingRefId = 11561;
				goal.TrailingCategory = 51000;
				goal.TrailingKey = quest.TextRefs.FirstOrDefault(t => t.Slot == QuestTextSlot.ObjectiveLocalized)?.KeyText;
				goal.TrailingLocalized = true;
				goal.TrailingFlags = "m";
			}

			await db.SaveChangesAsync();
			Console.WriteLine("Seeded Casilda quest 13479 snapshot Quest_c scalars + @609 objective goal (#255).");
		}

		// Kill-quest objectives recovered by capture correlation + domain knowledge (#47): the goal record carries the
		// RequiredCount but NOT the target NPC (that's in the RDB-resolved objective text), so the target is seeded
		// here. CombatService.AdvanceKillQuests matches a kill on the exact NPC name. Idempotent: only adds a goal if
		// the quest exists and has none yet.
		//   - Casilda's quest 13479 = kill 1 "Scavenger Tuut" (the NPC hit at the QuestGoal-complete frame in
		//     Blablabla_..._questcomplete; "Tuut is the NPC you kill for the Casilda quest").
		private static async Task SeedKillQuestTargets(DatabaseContext db)
		{
			await SeedKillGoal(db, questId: 13479, goalId: 5744, target: "Scavenger Tuut", required: 1);
		}

		private static async Task SeedKillGoal(DatabaseContext db, uint questId, uint goalId, string target, uint required)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.Goals.Count > 0)
				return;

			quest.Goals.Add(new QuestGoalDefinition
			{
				GoalId = goalId, Sequence = 1, RequiredCount = required, TargetName = target
			});
			await db.SaveChangesAsync();
			Console.WriteLine($"Seeded kill-quest target: quest {questId} = kill {required} {target}.");
		}

		// The beach starter (quest 10238) per-goal objective detail (the snapshot's @609 region). This data lives ONLY
		// in the FullChar snapshot (BeachTailsTemplate.bin), not the QuestFullUpdate/Journal packets the extractor reads,
		// so it is seeded here from the decoded values (Captures/scripts/decode_snapshot_609.py / QuestObjectiveRegion). Two
		// goals (1448, 3490), each with its own world marker + objective text. Lets QuestObjectiveRegion.Build generate
		// the objective region from the DB instead of the .bin (#186). Idempotent: skipped if the goals already exist.
		private static async Task SeedBeachQuestGoals(DatabaseContext db)
		{
			const uint beachQuestId = 10238;
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == beachQuestId);
			if (quest == null)
				return;

			// The Quest_c scalars + the index region's W6QT/table/trailer/goal-steps (snapshot-only; decoded from the
			// template @0..1292, Captures/scripts/decode_snapshot_609.py). Idempotent - set every run.
			quest.SnapshotN = 4;
			quest.SnapshotFieldEc = 1;
			quest.SnapshotFieldF0 = 0x96;
			quest.SnapshotFieldF8 = 15;
			quest.SnapshotState = 1;   // Quest_c State word @20 (per-quest constant; beach = 1). #255
			quest.SnapshotField28 = 0; // Quest_c Field28 @28 (beach = 0). #255
			quest.IndexMagic = 0x57365154; // "W6QT" goal-table name word. #255
			quest.IndexMarker = 0x7E2;
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 7;
			quest.IndexTableField3 = 0;
			quest.IndexTrailerWord = 0x000156E7;
			quest.IndexGoalSteps = "10238,12369,12373,10320,12371,10286,10287,10915";

			if (quest.Goals.Count > 0)
			{
				await db.SaveChangesAsync();
				return;
			}

			quest.Goals.Add(new QuestGoalDefinition
			{
				GoalId = 1448, Sequence = 1, RequiredCount = 1, Separator = 9, SubId = 180, MapId = 4010, MarkerX = 416, MarkerY = 701,
				Extra5 = 0, Extra6 = 0,
				ObjectiveRefId = 1448, ObjectiveCategory = 50304, ObjectiveKey = "pARATX1!DX]&#92;So+0*A9B",
				TrailingRefId = 180, TrailingCategory = 51200, TrailingKey = "b2Y2],LfVfAVps*9JX*G",
				TrailingLocalized = false, TrailingFlags = ""
			});
			quest.Goals.Add(new QuestGoalDefinition
			{
				GoalId = 3490, Sequence = 2, RequiredCount = 1, Separator = 12, SubId = 0, MapId = 4010, MarkerX = 521, MarkerY = 757,
				Extra5 = 70099, Extra6 = 0x36314C56,
				ObjectiveRefId = 3490, ObjectiveCategory = 50304, ObjectiveKey = "cA!(EbPpfzUK&#92;WPN@!H!",
				TrailingRefId = 9500, TrailingCategory = 51000, TrailingKey = "]+/sUK&#39;G3mO)clF`)9Jh",
				TrailingLocalized = true, TrailingFlags = "m"
			});

			await db.SaveChangesAsync();
			Console.WriteLine("Seeded beach quest 10238 goal-detail (2 goals) for the @609 objective region.");
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
			// #244: ensure the Npc TYPE rows exist first, so each spawn links to its type (the type identity lives on
			// Npc now, not duplicated on the spawn).
			var typeByTemplate = await UpsertNpcTypes(db, spawns);

			foreach (var e in spawns)
			{
				var existing = await db.NpcSpawns.Include(n => n.Morphs).Include(n => n.Equipment).FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z);

				var row = existing ?? new NpcSpawn
				{
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z
				};
				Populate(row, e);
				row.NpcId = e.TemplateId != 0 && typeByTemplate.TryGetValue(e.TemplateId, out var npc) ? npc.Id : null;
				if (existing == null)
					db.NpcSpawns.Add(row);
			}

			await db.SaveChangesAsync();
		}

		// Upserts the Npc TYPE rows (#244) for the just-imported spawns and links each spawn to its type. Keyed by
		// TemplateId; Name/Community come from the spawn entry (constant per type). Upsert (not delete+recreate) keeps
		// Npc.Id stable across re-imports so existing NpcSpell FKs survive. Spawns with TemplateId 0 (phantom capture
		// artifacts) get no type. Returns a TemplateId -> Npc cache for the caller.
		private static async Task<Dictionary<uint, Npc>> UpsertNpcTypes(DatabaseContext db, List<NpcSpawnEntry> entries)
		{
			var byTemplate = await db.Npcs.ToDictionaryAsync(n => n.TemplateId);
			foreach (var e in entries)
			{
				if (e.TemplateId == 0)
					continue;
				if (byTemplate.TryGetValue(e.TemplateId, out var npc))
				{
					if (!string.IsNullOrEmpty(e.Name)) npc.Name = e.Name;
					npc.Community = e.Community;
				}
				else
				{
					npc = new Npc { TemplateId = e.TemplateId, Name = e.Name, Community = e.Community };
					db.Npcs.Add(npc);
					byTemplate[e.TemplateId] = npc;
				}
			}
			await db.SaveChangesAsync(); // assigns Ids to new types
			return byTemplate;
		}

		// Links each NpcSpell to its NPC type by name (#244) - replaces the fragile NpcName string key with the FK while
		// keeping NpcName as the live fallback. Returns the count linked.
		private static async Task<int> LinkSpellsToTypes(DatabaseContext db)
		{
			var typeByName = new Dictionary<string, Npc>(StringComparer.OrdinalIgnoreCase);
			foreach (var n in await db.Npcs.ToListAsync())
				if (!string.IsNullOrEmpty(n.Name))
					typeByName.TryAdd(n.Name, n);

			var linked = 0;
			foreach (var sp in await db.NpcSpells.ToListAsync())
				if (sp.NpcName != null && typeByName.TryGetValue(sp.NpcName, out var npc))
				{
					sp.NpcId = npc.Id;
					linked++;
				}
			await db.SaveChangesAsync();
			return linked;
		}

		// Caster NPCs' spells (#232), upserted by (NPC name, spell id). The effect payload is refreshed on re-import.
		// PLAYER characters are materialized as NPC spawns too (captured other-players), so their OWN casts would be
		// mis-attributed as NPC spells - skip any name that is a known character.
		private static async Task<int> ImportNpcSpells(DatabaseContext db, List<NpcSpellEntry> spells)
		{
			var playerNames = (await db.Characters.Select(c => c.Name).ToListAsync())
				.Where(n => !string.IsNullOrEmpty(n))
				.Select(n => n.ToLowerInvariant())
				.ToHashSet();

			var inserted = 0;
			foreach (var e in spells)
			{
				if (playerNames.Contains(e.Name.ToLowerInvariant()))
					continue;
				var existing = await db.NpcSpells.FirstOrDefaultAsync(s => s.NpcName == e.Name && s.SpellId == e.SpellId);
				if (existing == null)
				{
					db.NpcSpells.Add(new NpcSpell { NpcName = e.Name, SpellId = e.SpellId, Payload = e.Payload });
					inserted++;
				}
				else
				{
					existing.Payload = e.Payload;
				}
			}

			await db.SaveChangesAsync();
			return inserted;
		}

		// NPC patrol routes (the canonical MoveTask waypoint form), deduped by (map, position) like the NPC spawns.
		// Replaces the route's waypoints wholesale on re-import so an updated capture re-seeds cleanly.
		private static async Task ImportPatrolPaths(DatabaseContext db, List<PatrolPathEntry> paths)
		{
			foreach (var e in paths)
			{
				var existing = await db.PatrolPaths
					.Include(p => p.Waypoints)
					.FirstOrDefaultAsync(p => p.MapId == e.MapId && p.PositionX == e.Position.X && p.PositionY == e.Position.Y && p.PositionZ == e.Position.Z);

				if (existing == null)
				{
					existing = new PatrolPath { MapId = e.MapId, Position = e.Position };
					db.PatrolPaths.Add(existing);
				}
				else
				{
					db.PatrolWaypoints.RemoveRange(existing.Waypoints);
					existing.Waypoints.Clear();
				}

				existing.Speed = e.Speed;
				for (var i = 0; i < e.Waypoints.Count; i++)
					existing.Waypoints.Add(new PatrolWaypoint
					{
						Ordinal = i,
						X = e.Waypoints[i].X,
						Y = e.Waypoints[i].Y,
						Z = e.Waypoints[i].Z
					});
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
			Console.WriteLine($"RDB items: {r.Decrypted}/{r.Keyed} keyed items decrypted+validated; {r.NamesAdded} names filled; {r.CategoriesAdded} search-categories filled; {r.ValuesAdded} values filled" + $"  (noEntry={r.NoEntry}, headerMismatch={r.HeaderMismatch}, decryptFailed={r.DecryptFailed})" +
			                  (dumpDir != null
				                  ? $"  -> templates dumped to {dumpDir}"
				                  : ""));
		}

		// Static world objects (decor/containers/zones), deduped by (map, position) like the NPC spawns.
		private static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects)
		{
			foreach (var e in objects)
			{
				// Match by (position, identity): two props can share an exact position (#222), so disambiguate the
				// existing row by its display_name identity (read from its own stored Data, the same way the entry's
				// DisplayName was read) - position alone would update the wrong row and still drop one of the pair.
				var candidates = await db.WorldObjects.Where(o => o.MapId == e.MapId && o.PositionX == e.X && o.PositionY == e.Y && o.PositionZ == e.Z).ToListAsync();
				var existing = candidates.FirstOrDefault(o => WorldObjectBuilder.ReadIdentity(o) == e.DisplayName);
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

			// Backfill the identity columns (#187) over EVERY world object - including rows imported in a prior run
			// whose objects aren't in this run's capture extraction (so PopulateObject never re-touched them). The
			// lift reads each row's own stored Data and is self-validating, so this only fills gaps, never corrupts.
			var lifted = 0;
			foreach (var row in await db.WorldObjects.ToListAsync())
				if (SimpleDynelMessageCodec.TryLiftColumns(row))
					lifted++;
			Console.WriteLine($"World objects: identity columns lifted on {lifted} rows (#187).");

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
			// Re-seed ONLY the AH listings (TradepostListing) and skip every other capture import - avoids a full
			// re-import churning unrelated tables (Items/NpcSpawns) from newer captures into the committed DB.
			var listingsOnly = args.Contains("--listings-only");

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
				.SelectMany(c => c.Packets.Select(p => p.Bytes))
				.ToList();
			if (doCaptures)
			{
				foreach (var bin in Directory.EnumerateFiles(capturesDir, "*.bin"))
					itemPackets.Add(await File.ReadAllBytesAsync(bin));
			}

			// Extract per capture so quest/tag dialogue conditions (ConversationConditions) can target a named capture
			// (e.g. Casilda's pre- vs post-first-quest greeting); Merge then dedups by (name, type, path, condition).
			var conversations = ConversationExtractor.Merge(
				captures.SelectMany(c => ConversationConditions.Apply(c.Name, ConversationExtractor.Extract(c.Packets))));

			if (doCaptures)
				Console.WriteLine($"Read {captures.Count} captures, {itemPackets.Count} item-candidate packets; extracted {conversations.Count} conversation nodes.");

			// --dump-conversation-states: read-only classification of each capture's per-NPC dialogue (issue #37). Prints
			// the screens + mid-conversation quest GRANTS per (capture, NPC) so the quest STATE a capture represents can
			// be inferred (a grant ⇒ that capture is the OFFER walk for that quest). Used to author per-capture gates.
			if (args.Contains("--dump-conversation-states"))
			{
				foreach (var c in captures)
				{
					foreach (var g in ConversationExtractor.Extract(c.Packets)
						         .Where(e => !string.IsNullOrEmpty(e.NpcName))
						         .GroupBy(e => e.NpcName)
						         .OrderBy(g => g.Key))
					{
						var grants = g.Where(e => e.GrantQuestId != 0)
							.OrderBy(e => e.AnswerPath)
							.Select(e => $"'{e.AnswerPath}'->{e.GrantQuestId}");
						Console.WriteLine($"{c.Name} | {g.Key} | screens={g.Count()} | grants=[{string.Join(", ", grants)}]");
					}
				}

				return 0;
			}

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

			// --reseed-npcs: wipe the NPC spawn tables and re-import ONLY each map's LOGIN-BURST NPCs (the pre-poll
			// world-build window), restricted to the Tortage intro + beach (4000/4010). This drops the cross-session
			// mid-session-spawn duplicates that otherwise accrete (419 beach spawns -> the ~42 retail-shaped set; see
			// #185/#244). Isolated: touches only NpcSpawns + its cascaded morph/equipment rows; no other table, so the
			// Tortage prune stays intact. NpcSpells (type-level, name-keyed) is left as-is.
			if (args.Contains("--reseed-npcs"))
			{
				var allowedMaps = new HashSet<uint> { 4000u, 4010u };

				// ONE capture file per map: the login burst is deterministic per map, but positions carry tiny
				// per-session float jitter, so UNIONING bursts across captures would not dedup (it ballooned to 236).
				// Instead pick, per map, the single capture whose login burst has the most NPCs (the most complete
				// world build) and take only that capture's burst - the clean retail-shaped ~42-NPC beach set.
				var perCapture = captures
					.Select(c => NpcSpawnExtractor.ExtractLoginBurst(c.Packets, allowedMaps))
					.ToList();
				var burst = new List<NpcSpawnEntry>();
				foreach (var map in allowedMaps)
				{
					var best = perCapture
						.Select(b => b.Where(e => e.MapId == map).ToList())
						.OrderByDescending(list => list.Count)
						.FirstOrDefault();
					if (best is { Count: > 0 })
						burst.AddRange(NpcSpawnExtractor.Merge(best)); // dedup any exact-position repeat within the one burst
				}

				var removed = await db.NpcSpawns.CountAsync();
				await db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawnMorphs");
				await db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawnEquipment");
				await db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawns");

				await ImportNpcSpawns(db, burst);

				await LinkSpellsToTypes(db); // #244: link the reseeded NPCs' spells to their types (types upserted in ImportNpcSpawns)

				var byMap = burst.GroupBy(e => e.MapId).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
				Console.WriteLine($"Reseed NPCs: removed {removed} spawns; imported {burst.Count} login-burst spawns ({string.Join(", ", byMap)}); " +
					$"morphs={await db.NpcSpawnMorphs.CountAsync()}, equipment={await db.NpcSpawnEquipment.CountAsync()}.");
				return 0;
			}

			// --reseed-mounts: harvest the summonable mount creatures (#27) from the captures into the dedicated
			// never-loaded Mounts map (CaptureMountImporter), so a client-cast SummonMount renders the mount. Isolated:
			// touches only the Mounts map + its NpcSpawns; leaves the Tortage world set intact.
			if (args.Contains("--reseed-mounts"))
			{
				var (seen, seeded) = await CaptureMountImporter.ImportAsync(db, captures.Select(c => c.Packets));
				Console.WriteLine($"Reseed mounts: {seen} mount creature(s) found, {seeded} seeded under map 0x{CaptureMountImporter.MountMapId:X}.");
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
				var abilities = await ArchiveSqlImporter.ImportAsync(db, archiveDir);
				Console.WriteLine($"Archive: {abilities} abilities.");
			}

			if (doCaptures)
			{
				// AH listings (TradepostListing) - harvested from the captured DispatchTradepostSearchResults
				// (0x2005) rows. Re-seedable on its own via --listings-only.
				var (listSeen, listAdded, listUpdated) = await CaptureListingImporter.ImportAsync(db, captures.SelectMany(c => CaptureListingImporter.Extract(c.Packets)));
				Console.WriteLine($"AH listings (0x2005 search results): {listSeen} listings, {listAdded} new, {listUpdated} updated.");

				if (!listingsOnly)
				{
				await CaptureItemImporter.ImportAsync(db, itemPackets);
				await CaptureMapImporter.ImportAsync(db, itemPackets);

				// Backfill: strip level-tooling suffixes from existing map names (#31) so .maps/teleport show the
				// clean RDB name even without re-running --rdb-maps. Idempotent; only touches names with a marker.
				var renamedMaps = 0;
				foreach (var map in await db.Maps.ToListAsync())
				{
					var clean = RdbMapImporter.SanitizeMapName(map.Name);
					if (clean != map.Name)
					{
						map.Name = clean;
						renamedMaps++;
					}
				}
				if (renamedMaps > 0)
				{
					await db.SaveChangesAsync();
					Console.WriteLine($"Maps: sanitized {renamedMaps} tooling-suffixed names (#31).");
				}
				await CaptureSpellImporter.ImportAsync(db, itemPackets);
				// The starting ability kit = what every new character gets during creation, so detect it from the
				// CREATE-flow captures only (filenames contain "creat"); the rest are higher-level/class abilities.
				// Keep ONE packet list per capture (CaptureAbilityImporter attributes each capture's grants to the
				// class chosen by its ChangeClass command). The tshark dump is the source (it keeps the compressed
				// 0x2052 downloads); the C# analyzer was retired (#82).
				var createCaptures = new List<IReadOnlyList<byte[]>>();
				foreach (var f in Directory.EnumerateFiles(capturesDir, "*.tshark.json")
					         .Where(f => Path.GetFileName(f).Contains("creat", StringComparison.OrdinalIgnoreCase)))
					createCaptures.Add(TsharkCaptureReader.Read(f).Select(p => p.Bytes).ToList());

				var (abSeen, abNew, abUpd) = await CaptureAbilityImporter.ImportAsync(db, itemPackets, createCaptures);
				Console.WriteLine($"Abilities (0x2052 grants): {abSeen} seen, {abNew} new, {abUpd} updated.");
				// Per-class STARTING spells (#74): harvest each create capture's 0x200A grants, class-attributed.
				var (startSpells, startSpellsNew) = await CaptureSpellImporter.ImportClassStartingSpellsAsync(db, createCaptures);
				Console.WriteLine($"Class starting spells (create 0x200A grants): {startSpells} harvested, {startSpellsNew} new.");
				// After items (the equipment references them). Existing names are skipped, never overwritten.
				if (!args.Contains("--skip-characters")) // TODO: CaptureCharacterImporter is not idempotent (UNIQUE CharacterBodies.CharacterId) on a populated DB
				{
					var (chSeen, chAdded, chSkipped) = await CaptureCharacterImporter.ImportAsync(db, captures.Select(c => c.Packets).ToList());
					Console.WriteLine($"Characters: {chSeen} captured, {chAdded} added, {chSkipped} already in DB.");
				}

				await ImportConversations(db, conversations);
				// After characters exist: seed their observed live state (wallet balances, hotbar slots).
				var (moneySeeded, shortcutsSeeded) = await CaptureCharacterStateImporter.ImportAsync(db, captures.Select(c => c.Packets).ToList());
				Console.WriteLine($"Character state: {moneySeeded} wallets, {shortcutsSeeded} hotbar slots seeded from captures.");
				// ...and their learned SPELL sets from the DispatchLearnSpell grants (re-granted on login, #74).
				var (spellChars, spellRows) = await CaptureSpellImporter.ImportLearnedSpellsAsync(db, itemPackets);
				Console.WriteLine($"Learned spells (0x200A grants): {spellChars} characters seeded, {spellRows} new rows.");
				var (shopsSeen, shopsAdded, shopsUpdated) = await CaptureShopImporter.ImportAsync(db, captures.SelectMany(c => CaptureShopImporter.Extract(c.Packets)));
				Console.WriteLine($"Shops (0x2033 vendor windows): {shopsSeen} vendors, {shopsAdded} new, {shopsUpdated} updated.");
				await ImportNpcSpawns(db, NpcSpawnExtractor.Merge(captures.SelectMany(c => NpcSpawnExtractor.Extract(c.Packets))));
				var npcSpells = NpcSpellExtractor.Merge(captures.SelectMany(c => NpcSpellExtractor.Extract(c.Packets)));
				var npcSpellsAdded = await ImportNpcSpells(db, npcSpells);
				Console.WriteLine($"NPC spells (#232): {npcSpells.Count} (name,spell) pairs, {npcSpellsAdded} new.");
				await LinkSpellsToTypes(db); // #244: link spells to their types (types upserted in ImportNpcSpawns)
				await ImportPatrolPaths(db, PatrolPathExtractor.Merge(captures.SelectMany(c => PatrolPathExtractor.Extract(c.Packets))));
				await ImportWorldObjects(db, WorldObjectExtractor.Merge(captures.SelectMany(c => WorldObjectExtractor.Extract(c.Packets))));
				await ImportMapWorldBursts(db, MapWorldBurstExtractor.Merge(captures.SelectMany(c => MapWorldBurstExtractor.Extract(c.Packets))));
				await ImportQuestDefinitions(db, QuestDefinitionExtractor.Extract(captures.SelectMany(c => c.Packets).ToList()));
				}
			}

			if (rdbDir != null)
			{
				ImportRdb(rdbDir);
				await RdbMorphTargetImporter.ImportAsync(db, rdbDir);
				var sp = await RdbSpellImporter.ImportAsync(db, rdbDir);
				Console.WriteLine($"RDB spells: {sp.Seen} named records, {sp.Inserted} new, {sp.NamesSet} names set, {sp.CastTimesSet} cast times set.");
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
			row.CapturedHandle = e.Handle;
			row.Data = e.Data;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;
			row.Race = e.Race;
			row.Sex = e.Sex;
			row.Level = e.Level;
			row.MonsterScale = e.MonsterScale;
			row.MonsterData = e.MonsterData;
			row.HeadMesh = e.HeadMesh;
			row.HasEquipment = e.HasEquipment;
			row.Health = e.Health;

			// Replace the appearance/morph table (#185) - decoded from the post-IIR, ordered. Empty for animals.
			row.Morphs.Clear();
			for (var i = 0; i < e.Morphs.Count; i++)
				row.Morphs.Add(new NpcSpawnMorph
				{
					Ordinal = i,
					MorphId = e.Morphs[i].MorphId,
					Value = e.Morphs[i].Value,
					Text = e.Morphs[i].Text ?? ""
				});

			// Replace the worn-equipment table (#185) - lifted from the CharacterIIR f23. Empty for unarmed NPCs.
			row.Equipment.Clear();
			foreach (var eq in e.Equipment)
				row.Equipment.Add(new NpcSpawnEquipment
				{
					Pos = eq.Pos,
					Key1 = eq.Key1, Key2 = eq.Key2, Key3 = eq.Key3, Key4 = eq.Key4,
					Template = eq.Template,
					Level = eq.Level,
					Hash = eq.Hash
				});
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

			// Lift the object's ProtoSimpleDynelIIR identity into typed columns (#187) from its stored Data so the
			// builder can CONSTRUCT it rather than slice it. Class-agnostic + self-validating (see TryLiftColumns).
			SimpleDynelMessageCodec.TryLiftColumns(row);

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

		// A capture's packets plus its base filename (no extension) - the name lets per-capture rules (e.g.
		// quest-state dialogue conditions, ConversationConditions) target a specific capture.
		public sealed record NamedCapture(string Name, List<CapturedPacket> Packets);

		private static string CaptureBaseName(string path, string suffix) => StripSuffix(Path.GetFileName(path), suffix);

		private static List<NamedCapture> ReadCaptures(string capturesDir)
		{
			var captures = new List<NamedCapture>();

			// tshark dumps (*.tshark.json) TCP-reassemble and keep the compressed downloads (notably the conversation
			// RESPONSE packets) - they are the trustworthy, canonical source. The C# PacketAnalyzer (which dropped
			// ~2/3 of compressed downloads) and its analyzer-JSON / direct-pcap read paths were retired (#82); every
			// capture is now dumped with Captures/tshark_dump.sh.
			foreach (var ts in Directory.EnumerateFiles(capturesDir, "*.tshark.json"))
				captures.Add(new NamedCapture(CaptureBaseName(ts, ".tshark.json"), TsharkCaptureReader.Read(ts)));

			return captures;
		}

		// Strips a (possibly multi-part) extension like ".tshark.json" from a path, returning the capture base name.
		private static string StripSuffix(string path, string suffix)
		{
			return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
				? path[..^suffix.Length]
				: path;
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
				["Maps"] = await db.Maps.CountAsync(),
				["MapWorldBursts"] = await db.MapWorldBursts.CountAsync(),
				["NpcSpawns"] = await db.NpcSpawns.CountAsync(),
				["PatrolPaths"] = await db.PatrolPaths.CountAsync(),
				["WorldObjects"] = await db.WorldObjects.CountAsync(),
				["ConversationNodes"] = await db.ConversationNodes.CountAsync(),
				["ConversationPackets"] = await db.ConversationPackets.CountAsync()
			};
		}
	}
}
