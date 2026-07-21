using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Shared capture-import orchestration the modules (Faolan.Importer.Modules) drive - the upsert/wipe-rebuild
	// helpers that sit above the per-subsystem CaptureX importers/extractors.
	public static class ImportOps
	{
		// Unique quest-NPC home FACING (#267): the burst-truth dedup can pick a session where she'd already turned, so
		// her facing drifts from the chained-at-the-post pose. Pin Casilda's rotation to her create-dump FA015807
		// transform (quaternion 0, 0.8661140, 0, -0.4998470) - the byte-exact facing she has while chained at x≈362.
		private static readonly Dictionary<string, Quaternion> UniqueNpcFacings = new()
		{
			["Casilda"] = new Quaternion(0f, 0.8661140203475952f, 0f, -0.4998469948768616f)
		};

		// Unique quest-NPC home positions (they move during their quest, so dedup must not strand them). #266
		private static readonly Dictionary<string, Vector3> UniqueNpcHomes = new()
		{
			["Casilda"] = new Vector3(361.64300537109375f, 153.34036254882813f, 717.66802978515625f)
		};

		// Curated unique/no-random-drop item templates (#273). UNIQUE items are typed Normal and carry no wire-derivable
		// "unique" flag (the RDB item-template property table has no decoded unique field, and SearchCategory mixes them
		// with ordinary sellables), so they can't be auto-detected - this is the hand-maintained denylist. Add a template
		// id here (or set it live with `.noloot <id>`) when an item that shouldn't randomly drop is spotted in-client.
		private static readonly uint[] UniqueLootDenylist =
		[
			354477 // Ape King's Treasure
		];

		// Seeds the random-loot exclusion flag (#273): excludes every Quest/Resource item (those typed from a non-Normal
		// container) plus the curated unique denylist. ADD-ONLY - it never CLEARS the flag, so an item flagged live via
		// .noloot (or by hand) survives a re-import. Returns the number of rows newly excluded.
		public static async Task<int> ApplyRandomLootExclusions(DatabaseContext db)
		{
			var denylist = UniqueLootDenylist.ToHashSet();
			var newlyExcluded = 0;
			foreach (var item in await db.Items.ToListAsync())
			{
				if (item.ExcludeFromRandomLoot)
					continue; // already excluded (a previous run, or a live .noloot) - never clear it

				if (item.Type != ItemType.Normal || denylist.Contains(item.Id))
				{
					item.ExcludeFromRandomLoot = true;
					newlyExcluded++;
				}
			}

			if (newlyExcluded > 0)
				await db.SaveChangesAsync();
			return newlyExcluded;
		}

		// The clean burst-truth NPC spawn set (#266): per capture one entry per distinct mob (dedup by handle),
		// each type taken from its single authoritative session.
		public static (List<NpcSpawnEntry> Spawns, List<PatrolPathEntry> Patrols) BuildBurstTruthSpawns(IReadOnlyList<NamedCapture> captures)
		{
			var perCap = captures
				.Select(c => (All: DistinctByHandle(NpcSpawnExtractor.Extract(c.Packets)), Burst: DistinctByHandle(NpcSpawnExtractor.ExtractLoginBurst(c.Packets)),
					Routes: PatrolPathExtractor.ExtractByHandle(c.Packets)))
				.ToList();
			var allMaps = perCap.SelectMany(p => p.All.Select(e => e.MapId)).Distinct().ToList();

			// Keyed by TEMPLATE ID, not name (#244): many NPCs share a placeholder INTERNAL name - e.g. "Halen van
			// Dummy" is the internal name of ~16 unrelated templates (the level-10 castle Child 14543, a dummy 14535,
			// a quest-giver, ...). Keying the richest-set backfill by name leaked a geared level-80 same-named NPC's
			// gear + morphs onto the level-10 child, which its own captured spawn doesn't carry (floating gear). The
			// template id is the stable per-type key: distinct templates stay separate, while genuinely-same NPCs
			// (same template across captures) still merge.
			var morphsByTemplate = new Dictionary<uint, IReadOnlyList<NpcAppearanceMorph>>();
			foreach (var e in perCap.SelectMany(p => p.All))
			{
				if (e.Morphs.Count > 0 && (!morphsByTemplate.TryGetValue(e.TemplateId, out var cur) || e.Morphs.Count > cur.Count))
					morphsByTemplate[e.TemplateId] = e.Morphs;
			}

			// Richest worn-equipment set per TEMPLATE (#267): the burst-truth dedup can pick a session where an NPC wears
			// FEWER pieces than another capture of the same NPC - e.g. Casilda at the post wears 4 (incl. slot-18
			// tpl402981, the CHAINS that make her look chained), but while FOLLOWING the player she wears only 3. Keep
			// the fullest loadout so the chosen spawn isn't stripped of state-specific gear it has in another capture.
			// (Per template, so it never leaks across the shared-placeholder-name templates above.)
			var equipByTemplate = new Dictionary<uint, IReadOnlyList<NpcSpawnEquipment>>();
			foreach (var e in perCap.SelectMany(p => p.All))
			{
				if (e.Equipment.Count > 0 && (!equipByTemplate.TryGetValue(e.TemplateId, out var cur) || e.Equipment.Count > cur.Count))
					equipByTemplate[e.TemplateId] = e.Equipment;
			}

			var spawns = new List<NpcSpawnEntry>();
			var patrols = new List<PatrolPathEntry>();

			var burstNamesByMap = new Dictionary<uint, HashSet<string>>();
			foreach (var map in allMaps)
			{
				var best = perCap.OrderByDescending(p => p.Burst.Count(e => e.MapId == map)).First();
				var burstSpawns = best.Burst.Where(e => e.MapId == map).ToList();
				if (burstSpawns.Count == 0)
					continue;

				Take(burstSpawns, best.Routes);
				burstNamesByMap[map] = burstSpawns.Select(e => e.Name ?? "").ToHashSet();
			}

			var nonBurstTypes = perCap
				.SelectMany(p => p.All)
				.Select(e => (e.MapId, Name: e.Name ?? ""))
				.Where(t => !(burstNamesByMap.TryGetValue(t.MapId, out var ns) && ns.Contains(t.Name)))
				.Distinct();
			foreach (var (map, name) in nonBurstTypes)
			{
				var best = perCap.OrderByDescending(p => p.All.Count(e => e.MapId == map && (e.Name ?? "") == name)).First();
				Take(best.All.Where(e => e.MapId == map && (e.Name ?? "") == name).ToList(), best.Routes);
			}

			// Pets are content of the creature TYPE (lifted onto the type columns by CapturePetImporter, spawned from the type
			// at summon time - #307/#244), NEVER world placements. A captured session's SUMMONED pets otherwise leak in
			// here as permanent NpcSpawns and stand around forever where the player summoned them (the necro pet cluster
			// on Old Tarantia). Drop the PetSummonCatalog creatures from the placement set.
			var petNames = PetSummonCatalog.All.Select(p => p.PetTemplate.ToLowerInvariant()).ToHashSet();
			var placements = spawns.Where(e => !petNames.Contains((e.Name ?? string.Empty).ToLowerInvariant())).ToList();

			return (NpcSpawnExtractor.Merge(placements), patrols);

			void Take(List<NpcSpawnEntry> chosen, Dictionary<uint, PatrolPathEntry> routes)
			{
				foreach (var e0 in chosen)
				{
					var e = UniqueNpcHomes.TryGetValue(e0.Name ?? "", out var home)
						? e0 with
						{
							X = home.X,
							Y = home.Y,
							Z = home.Z
						}
						: e0;
					if (UniqueNpcFacings.TryGetValue(e.Name ?? "", out var facing))
					{
						e = e with
						{
							RotX = facing.X,
							RotY = facing.Y,
							RotZ = facing.Z,
							RotW = facing.W
						};
					}

					if (e.Morphs.Count == 0 && morphsByTemplate.TryGetValue(e.TemplateId, out var backfillMorphs))
					{
						e = e with
						{
							Morphs = backfillMorphs
						};
					}

					if (equipByTemplate.TryGetValue(e.TemplateId, out var fullEquip) && fullEquip.Count > e.Equipment.Count)
					{
						e = e with
						{
							Equipment = fullEquip,
							HasEquipment = true
						};
					}

					spawns.Add(e);
					if (routes.TryGetValue(e.Handle, out var r) && r.Waypoints.Count > 0)
						patrols.Add(new PatrolPathEntry(e.MapId, new Vector3(e.X, e.Y, e.Z), r.Waypoints, 0, r.Root));
				}
			}
		}

		// Quest definitions harvested from the COMPLETED-quest journal (#390). ADDITIVE by design: these rows carry
		// only a title + journal sub-line, so they fill the catalog out for quests no capture session ever touched
		// without disturbing the far richer definitions the QuestFullUpdate path builds.
		//
		// ⚠️ Deliberately NOT routed through ImportQuestDefinitions, which does
		// `RemoveRange(existing.TextRefs); existing.TextRefs = def.TextRefs` - that would strip a complete quest's
		// summary/objective refs down to the two the completed journal happens to carry.
		public static async Task<(int Added, int Filled)> ImportCompletedQuestDefinitions(DatabaseContext db, CompletedQuestExtractor.Harvest harvest)
		{
			var existing = await db.QuestDefinitions.Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId);
			var maps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			var areas = await db.QuestAreas.Select(a => a.AreaId).ToHashSetAsync();
			int added = 0, filled = 0;

			foreach (var q in harvest.Quests)
			{
				// Maps and areas are FK targets - a placeholder beats dropping the quest (Melvin: "its okay to insert
				// maps wihtout name").
				if (q.MapId != 0 && maps.Add(q.MapId))
					db.Maps.Add(new Map
					{
						Id = q.MapId,
						Name = ""
					});

				if (q.AreaId != 0 && areas.Add(q.AreaId))
					db.QuestAreas.Add(new QuestArea
					{
						AreaId = q.AreaId
					});

				if (!existing.TryGetValue(q.QuestId, out var def))
				{
					def = new QuestDefinition
					{
						QuestId = q.QuestId,
						AreaId = q.AreaId,
						MapId = q.MapId,
						JournalSequence = q.Sequence
					};
					db.QuestDefinitions.Add(def);
					existing[q.QuestId] = def;
					added++;
				}
				else
				{
					// Stable per quest across the whole corpus, so refreshing it is safe and fills older rows.
					def.JournalSequence = q.Sequence;
					filled++;
				}

				AddRefIfMissing(def, QuestTextSlot.Title, q.Title);
				AddRefIfMissing(def, QuestTextSlot.ListSubLine, q.SubLine);

				if (harvest.Areas.TryGetValue(q.AreaId, out var header))
				{
					AddRefIfMissing(def, QuestTextSlot.AreaTitle, header.Title);
					AddRefIfMissing(def, QuestTextSlot.RegionName, header.Region);
					AddRefIfMissing(def, QuestTextSlot.MapName, header.Map);
				}
			}

			await db.SaveChangesAsync();
			return (added, filled);
		}

		public static async Task ImportConversations(DatabaseContext db, List<ConversationEntry> conversations)
		{
			var existing = await db.ConversationNodes.Include(n => n.Packets).ToListAsync();
			db.ConversationPackets.RemoveRange(existing.SelectMany(n => n.Packets));
			db.ConversationNodes.RemoveRange(existing);

			// A conversation NPC's template must exist to be the FK target (#244); the world is pruned, so a template
			// resolved from a capture may not be imported - null it out (name-only fallback) rather than fail the FK.
			var knownTemplates = (await db.NpcTemplates.Select(t => t.TemplateId).ToListAsync()).ToHashSet();

			foreach (var e in conversations)
			{
				db.ConversationNodes.Add(new ConversationNode
				{
					TypeId = e.TypeId,
					AnswerPath = e.AnswerPath,
					CapturedNpcHandle = e.CapturedNpcHandle,
					NpcName = e.NpcName,
					NpcTemplateId = e.NpcTemplateId is { } t && knownTemplates.Contains(t)
						? t
						: null,
					CapturedInstance = e.CapturedInstance,
					ConditionScript = e.ConditionScript,
					GrantQuestId = e.GrantQuestId == 0
						? null
						: e.GrantQuestId,
					Packets = e.Packets.Select((p, i) => BuildPacket(p, i)).ToList()
				});
			}

			await db.SaveChangesAsync();
		}

		// Rebuild the per-map loot pool (#273) from the captured bag contents. Clean rebuild (the pool is fully derived
		// from the captures, so a re-run should not accumulate stale rows), keyed for dedup by (MapId, ItemTemplateId).
		// Only items that exist in the Items table are kept - an unknown template the server can't build is useless as a
		// drop (the items module seeds those identities, so it runs first in the default order).
		public static async Task<int> ImportMapLootPool(DatabaseContext db, List<MapLootPoolEntry> entries)
		{
			db.MapLootEntries.RemoveRange(await db.MapLootEntries.ToListAsync());

			var knownItems = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var kept = entries
				.Where(e => knownMaps.Contains(e.MapId) && knownItems.Contains(e.ItemTemplateId)) // FK targets must exist
				.GroupBy(e => (e.MapId, e.ItemTemplateId))
				.Select(g => g.Key)
				.ToList();

			foreach (var (mapId, templateId) in kept)
			{
				db.MapLootEntries.Add(new MapLootEntry
				{
					MapId = mapId,
					ItemTemplateId = templateId
				});
			}

			await db.SaveChangesAsync();
			return kept.Count;
		}

		// Upsert by (map, spawn point); never wipe. Ensures NpcTemplate TYPE rows exist first (#244).
		public static async Task ImportNpcSpawns(DatabaseContext db, List<NpcSpawnEntry> spawns)
		{
			var typeByTemplate = await UpsertNpcTypes(db, spawns);

			foreach (var e in spawns)
			{
				var existing = await db
					.NpcSpawns.Include(n => n.Morphs)
					.Include(n => n.Equipment)
					.FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z);

				// NpcTemplateId is required now (no phantom rows), so a spawn whose type doesn't resolve is dropped - the same
				// rule ImportNpcSpells applies to typeless casters, and consistent with the phantom-scrub invariant.
				if (e.TemplateId == 0 || !typeByTemplate.TryGetValue(e.TemplateId, out var npc))
					continue;

				var row = existing ?? new NpcSpawn
				{
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z
				};
				Populate(row, e);
				row.NpcTemplateId = npc.TemplateId;
				if (existing == null)
					db.NpcSpawns.Add(row);
			}

			await db.SaveChangesAsync();
		}

		// Caster NPCs' spells (#232), upserted by (NpcTemplate type, spell id). The caster NAME is resolved to its NpcTemplate TYPE id
		// (types were upserted by ImportNpcSpawns just before) and only stored as the NpcTemplateId FK - a cast whose caster is
		// a PLAYER (mis-harvested self-buff) or has no captured NpcTemplate type (off-beach named NPCs) is DROPPED, since the
		// loader reads spells via the FK (NpcTemplate.Spells).
		public static async Task<int> ImportNpcSpells(DatabaseContext db, List<NpcSpellEntry> spells)
		{
			var playerNames = (await db.Characters.Select(c => c.Name).ToListAsync()).Where(n => !string.IsNullOrEmpty(n)).Select(n => n.ToLowerInvariant()).ToHashSet();
			var typeByName = (await db.NpcTemplates.Where(n => n.Name != null).ToListAsync()).GroupBy(n => n.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().TemplateId);

			var inserted = 0;
			foreach (var e in spells)
			{
				var name = e.Name.ToLowerInvariant();
				if (playerNames.Contains(name) || !typeByName.TryGetValue(name, out var npcId))
					continue;

				var existing = await db.NpcSpells.FirstOrDefaultAsync(s => s.NpcTemplateId == npcId && s.SpellId == e.SpellId);
				if (existing == null)
				{
					var s = new NpcSpell
					{
						NpcTemplateId = npcId,
						SpellId = e.SpellId
					};
					ApplyEffect(s, e.Payload);
					db.NpcSpells.Add(s);
					inserted++;
				}
				else
					ApplyEffect(existing, e.Payload);
			}

			await db.SaveChangesAsync();
			return inserted;
		}

		// @609 objective goals harvested from captured journals. ADDITIVE, and deliberately NOT routed through
		// ImportQuestDefinitions - that path rebuilds a quest's goal set and would fight the Seed* overlays.
		//
		// ⚠️ QuestGoalDefinition.GoalId is a GLOBAL primary key. A goal id already owned by a different quest is
		// SKIPPED rather than moved: attaching one to a second quest is what made an earlier attempt at this die with
		// a DbUpdateConcurrencyException mid-import.
		public static async Task<int> ImportObjectiveGoals(DatabaseContext db, IReadOnlyList<ObjectiveGoalExtractor.Goal> goals)
		{
			var tracked = await db.QuestGoalDefinitions.ToDictionaryAsync(g => g.GoalId);
			var owner = tracked.ToDictionary(k => k.Key, k => k.Value.QuestDefinitionId);
			var quests = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId, q => q.MapId);
			var maps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			int added = 0, noMap = 0, refreshed = 0;

			foreach (var g in goals)
			{
				if (!quests.TryGetValue(g.QuestId, out var questMapId))
					continue; // no definition to hang it on

				// 0 means the goal has no world marker - stored as NULL, not skipped.
				uint? mapId = g.MapId != 0
					? g.MapId
					: null;
				if (mapId == null)
					noMap++;

				if (owner.TryGetValue(g.GoalId, out var existingQuest))
				{
					if (existingQuest != g.QuestId)
					{
						Console.WriteLine($"  goal {g.GoalId} already belongs to quest {existingQuest}, not {g.QuestId} - skipped.");
						continue;
					}

					// Present already - typically from a Seed* overlay. The CAPTURE is the truth source for every wire
					// field, so refresh them; the server-side-only fields (TargetName/GoalType/QuestItemId) are left
					// alone. Skipping instead kept hand-written seed values that no capture agrees with, which is what
					// left the last ~116 regions unreproducible.
					var row = tracked[g.GoalId];
					row.Sequence = g.Sequence;
					row.RegionOrder = g.RegionOrder;
					row.Separator = g.Separator;
					row.SubId = g.SubId;
					row.MapId = mapId;
					row.MarkerX = mapId == null
						? null
						: g.MarkerX;
					row.MarkerY = mapId == null
						? null
						: g.MarkerY;
					row.RequiredCount = g.RequiredCount;
					row.Unknown1 = g.Unknown1;
					row.Unknown2 = g.Unknown2;
					row.GoalIdEcho = g.GoalIdEcho;
					row.Extra0 = g.Extra0;
					row.Extra1 = g.Extra1;
					row.Extra2 = g.Extra2;
					row.Extra3 = g.Extra3;
					row.Extra4 = g.Extra4;
					row.Extra5 = g.Extra5;
					row.Extra6 = g.Extra6;
					row.ObjectiveRefId = g.Objective.RefId;
					row.ObjectiveCategory = g.Objective.Category;
					row.ObjectiveKey = g.Objective.Key;
					row.ObjectiveToken = g.Objective.Token;
					row.TrailingRefId = g.Trailing?.RefId ?? 0;
					row.TrailingCategory = g.Trailing?.Category ?? 0;
					row.TrailingKey = g.Trailing?.Key;
					row.TrailingFlags = g.Trailing?.Flags;
					row.TrailingToken = g.Trailing?.Token;
					row.TrailingLocalized = g.Trailing?.IsLocalized ?? false;
					refreshed++;
					continue;
				}

				if (mapId is { } m && maps.Add(m))
					db.Maps.Add(new Map
					{
						Id = m,
						Name = ""
					});

				db.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = g.GoalId,
					QuestDefinitionId = g.QuestId,
					Sequence = g.Sequence,
					RegionOrder = g.RegionOrder,
					Separator = g.Separator,
					SubId = g.SubId,
					MapId = mapId,
					MarkerX = mapId == null
						? null
						: g.MarkerX,
					MarkerY = mapId == null
						? null
						: g.MarkerY,
					Unknown1 = g.Unknown1,
					Unknown2 = g.Unknown2,
					GoalIdEcho = g.GoalIdEcho,
					Extra0 = g.Extra0,
					Extra1 = g.Extra1,
					Extra2 = g.Extra2,
					Extra3 = g.Extra3,
					Extra4 = g.Extra4,
					Extra5 = g.Extra5,
					Extra6 = g.Extra6,
					RequiredCount = g.RequiredCount,
					ObjectiveRefId = g.Objective.RefId,
					ObjectiveCategory = g.Objective.Category,
					ObjectiveKey = g.Objective.Key,
					ObjectiveToken = g.Objective.Token,
					TrailingToken = g.Trailing?.Token,
					TrailingRefId = g.Trailing?.RefId ?? 0,
					TrailingCategory = g.Trailing?.Category ?? 0,
					TrailingKey = g.Trailing?.Key,
					TrailingFlags = g.Trailing?.Flags,
					TrailingLocalized = g.Trailing?.IsLocalized ?? false
				});
				owner[g.GoalId] = g.QuestId;
				added++;
			}

			await db.SaveChangesAsync();
			if (noMap > 0)
				Console.WriteLine($"  {noMap} objective goals carry no world marker (MapId/MarkerX/MarkerY null).");

			if (refreshed > 0)
				Console.WriteLine($"  {refreshed} existing objective goals refreshed from the captures.");

			return added;
		}

		// NPC patrol routes, one per NpcSpawn (#316). The route is correlated to its NPC by the shared spawn position
		// (the spawns were imported + saved just before), then FK'd; the columnarized task tree is replaced wholesale
		// on re-import.
		public static async Task ImportPatrolPaths(DatabaseContext db, List<PatrolPathEntry> paths)
		{
			foreach (var e in paths)
			{
				var spawn = await db.NpcSpawns.FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.Position.X && n.PositionY == e.Position.Y && n.PositionZ == e.Position.Z);
				if (spawn == null)
					continue; // a route with no matching spawn (should not happen for burst-truth patrols) - skip

				var existing = await db.PatrolPaths.Include(p => p.Nodes).ThenInclude(n => n.Waypoints).FirstOrDefaultAsync(p => p.NpcSpawnId == spawn.Id);

				if (existing == null)
				{
					existing = new PatrolPath
					{
						NpcSpawnId = spawn.Id
					};
					db.PatrolPaths.Add(existing);
				}
				else
				{
					db.PatrolNodes.RemoveRange(existing.Nodes); // cascades the waypoints
					existing.Nodes.Clear();
				}

				// Attach a fresh copy of the task tree, flattened so EF assigns every node its PatrolPathId (the
				// Parent/Children navs carry ParentId). Clone because a Root can be shared across entries.
				AddNodeTree(existing, CloneNode(e.Root));
			}

			await db.SaveChangesAsync();
		}

		// Quest journal-content definitions, refreshed per harvested quest; un-harvested quests untouched.
		// Quest AREAS - the grouping tier of the journal tree. `harvested` carries the wire words (the 4CC), which only
		// the completed-quest journal exposes (#390); `referenced` is every AreaId a quest definition points at.
		//
		// QuestDefinition.AreaId is a real FK, so a row must exist for EVERY referenced area before definitions are
		// saved - the captures only show areas a character actually completed a quest in, which is far short of the
		// areas quests live in. Anything unharvested gets a placeholder (Magic 0), exactly as Maps do; a missing row
		// would otherwise fail the FK check on rebuild.
		public static async Task<int> ImportQuestAreas(DatabaseContext db, IReadOnlyList<QuestArea> harvested, IReadOnlyCollection<uint> referenced)
		{
			var existing = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId);
			var byArea = harvested.ToDictionary(a => a.AreaId);
			var added = 0;

			foreach (var areaId in byArea.Keys.Concat(referenced).Distinct())
			{
				byArea.TryGetValue(areaId, out var wire);

				if (existing.TryGetValue(areaId, out var row))
				{
					// Only ever fill IN - never clobber a harvested 4CC with a placeholder's 0.
					if (wire == null)
						continue;

					row.Magic = wire.Magic;
					row.TableField1 = wire.TableField1;
					row.AreaTitleRefId = wire.AreaTitleRefId;
					row.AreaTitleCategory = wire.AreaTitleCategory;
					row.AreaTitleKey = wire.AreaTitleKey;
					row.MapRefId = wire.MapRefId;
					row.MapCategory = wire.MapCategory;
					row.MapKey = wire.MapKey;
					row.RegionRefId = wire.RegionRefId;
					row.RegionCategory = wire.RegionCategory;
					row.RegionKey = wire.RegionKey;
					continue;
				}

				db.QuestAreas.Add(new QuestArea
				{
					AreaId = areaId,
					Magic = wire?.Magic ?? 0,
					TableField1 = wire?.TableField1 ?? 0,
					AreaTitleRefId = wire?.AreaTitleRefId ?? 0,
					AreaTitleCategory = wire?.AreaTitleCategory ?? 0,
					AreaTitleKey = wire?.AreaTitleKey,
					MapRefId = wire?.MapRefId ?? 0,
					MapCategory = wire?.MapCategory ?? 0,
					MapKey = wire?.MapKey,
					RegionRefId = wire?.RegionRefId ?? 0,
					RegionCategory = wire?.RegionCategory ?? 0,
					RegionKey = wire?.RegionKey
				});
				added++;
			}

			await db.SaveChangesAsync();
			return added;
		}

		public static async Task ImportQuestDefinitions(DatabaseContext db, List<QuestDefinition> definitions)
		{
			// QuestDefinition.MapId is a required FK to Maps, but a harvested quest can point its map marker at a
			// playfield the Maps catalog doesn't have - the catalog is built from captured MapChange packets, so a map
			// nobody visited in a capture is simply absent (e.g. 1000004, referenced by four Quest_c journal records).
			// These used to be SKIPPED, which threw away real journal content to satisfy an FK. A placeholder Map row
			// carries the id and nothing else; the maps module fills in the name if that playfield is ever captured.
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var placeholderMaps = 0;
			foreach (var mapId in definitions.Select(d => d.MapId).Distinct().Where(id => id != 0 && !knownMaps.Contains(id)))
			{
				db.Maps.Add(new Map
				{
					Id = mapId,
					Name = string.Empty
				});
				knownMaps.Add(mapId);
				placeholderMaps++;
			}

			if (placeholderMaps > 0)
			{
				await db.SaveChangesAsync();
				Console.WriteLine($"Quest definitions: added {placeholderMaps} placeholder Map row(s) for playfields not in the catalog.");
			}

			var skipped = 0;

			var added = 0;
			var updated = 0;
			foreach (var def in definitions)
			{
				if (!knownMaps.Contains(def.MapId))
				{
					Console.WriteLine($"Quest {def.QuestId}: skipped - map {def.MapId} not in the Maps catalog (id 0 = the record carried no map ref).");
					skipped++;
					continue;
				}

				var existing = await db.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == def.QuestId);
				if (existing == null)
				{
					db.QuestDefinitions.Add(def);
					added++;
					continue;
				}

				existing.AreaId = def.AreaId;
				existing.GoalId = def.GoalId;
				existing.Level = def.Level;
				existing.MapId = def.MapId;
				existing.MarkerX = def.MarkerX;
				existing.MarkerY = def.MarkerY;
				db.QuestTextRefs.RemoveRange(existing.TextRefs);
				existing.TextRefs = def.TextRefs;

				// Merge the harvested PRIMARY goal by GoalId into the existing rows WITHOUT clobbering the server-side-only
				// fields (TargetName/GoalType/QuestItemId) a prior Seed* set - only the wire fields refresh.
				foreach (var hg in def.Goals)
				{
					var g = existing.Goals.FirstOrDefault(x => x.GoalId == hg.GoalId);
					if (g == null)
						existing.Goals.Add(g = new QuestGoalDefinition
						{
							GoalId = hg.GoalId,
							Sequence = hg.Sequence,
							RequiredCount = hg.RequiredCount
						});

					g.MapId = hg.MapId;
					g.MarkerX = hg.MarkerX;
					g.MarkerY = hg.MarkerY;
					g.ObjectiveRefId = hg.ObjectiveRefId;
					g.ObjectiveCategory = hg.ObjectiveCategory;
					g.ObjectiveKey = hg.ObjectiveKey;
				}

				updated++;
			}

			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateConcurrencyException ex)
			{
				foreach (var e in ex.Entries)
				{
					Console.WriteLine($"  CONFLICT entity={e.Entity.GetType().Name} state={e.State} keys=" +
					                  string.Join(",", e.Properties.Where(pr => pr.Metadata.IsPrimaryKey()).Select(pr => $"{pr.Metadata.Name}={pr.CurrentValue}")));
				}

				throw;
			}

			Console.WriteLine($"Quest definitions (journal content): {definitions.Count} harvested, {added} new, {updated} updated, {skipped} skipped (map not in catalog).");
		}

		// Overlay the hand-authored quest WIRING onto the harvested definitions - the fields captures don't carry: the
		// server trigger (GoalType/TargetName/QuestItemId via SeedGoal), the chain successor (NextQuestId via Chain), and
		// the byte-exact @609/snapshot detail the beach + Casilda quests need. Run as its OWN importer module
		// (QuestWiringModule) AFTER the `quests` module, so it applies last - the trigger fields are preserved by the
		// objective-goals refresh, and the @609-detail seeds are authoritative (they're calibrated to match the capture).
		// Trigger/chain seeds are BOOTSTRAP-ONLY (see SeedGoal/Chain), so a re-run never reverts a Quest-editor edit.
		public static async Task WireQuests(DatabaseContext db)
		{
			await SeedBeachQuestGoals(db);
			await SeedShipwreckedCityChain(db);
			await SeedCaptureReachQuests(db);
			await SeedCasildaChain(db);
			await SeedCasildaQuestSnapshot(db);
		}

		// Other capture-harvested quests whose single objective is a LOCATION (TrailingCategory 100 + a map marker),
		// each granted by a SPAWNED NPC's dialogue (ConversationNodes.GrantQuestId). Wire the goal as ReachArea off its
		// captured marker so it completes on arrival - the most quest data we can source without per-quest capture
		// correlation (#358). Best-effort ("as much as possible, not 100%"): cat-100 = a location objective (proven for
		// Shipwrecked III "enter city" / VIII Nadini), but the exact retail purpose (reach vs talk-at-location) isn't
		// pinned; reaching the marker is a reasonable stand-in. All 7 granting NPCs are spawned and all target maps
		// exist. Server-trigger field only (not byte-exact); bootstrap-only via SeedGoal.
		private static async Task SeedCaptureReachQuests(DatabaseContext db)
		{
			await SeedGoal(db, 13041, 4833, QuestGoalType.ReachArea);  // giver Alyssa        -> reach @4020 (959,723)
			await SeedGoal(db, 13986, 6459, QuestGoalType.ReachArea);  // giver Tanner Chael  -> reach @1000 (283,659)
			await SeedGoal(db, 14092, 6597, QuestGoalType.ReachArea);  // giver Borcha        -> reach @1010 (88,205)
			await SeedGoal(db, 14617, 7389, QuestGoalType.ReachArea);  // giver Cadarn        -> reach @3000 (992,1134)
			await SeedGoal(db, 14622, 7394, QuestGoalType.ReachArea);  // giver Serving Wench -> reach @2000 (826,533)
			await SeedGoal(db, 14627, 7399, QuestGoalType.ReachArea);  // giver Caor          -> reach @1000 (403,445)
			await SeedGoal(db, 19522, 14328, QuestGoalType.ReachArea); // giver Hadrathus     -> reach @30200 (627,868)
		}

		// The Shipwrecked destiny chain past stage I (#262/#358). Beach dump proves the supersession 10238 -> 12369 ->
		// 12373 (stages I "The Slaver" -> II "The Acheronian Gate" -> III "The City of Tortage"). Stages II/III are wired
		// as ReachArea goals off their captured MARKERS (verified world X,Z): II's goal 3492 at the beach ruins
		// (4010, 524/755), III's goal 3498 in the city itself (4020, 913/718) - "enter the city" completes on arrival.
		// Stage III is chain-final for now: stages IV-VIII (talk Turach 951 / bricks 12371 / guard / Tina 2805 / Nadini
		// 806) have no imported goal targets yet - tracked on #358. NOT byte-exact; markers/types are best-effort ("polish
		// later"). Idempotent.
		private static async Task SeedShipwreckedCityChain(DatabaseContext db)
		{
			// II "The Acheronian Gate" -> III "The City of Tortage". II's key objective is LOOTING the Acheronian Gate
			// Key (item 193186) from Saddur's corpse bag - the same kill->loot pattern as Casilda's Tuut key: Hoxxling
			// gameplay fr11942 Saddur dies -> fr11947 his corpse becomes a loot bag -> fr12023 the key is added to the
			// 0x71 quest container. So goal 3492 = LootFromNpc "Saddur" (its 524/755 marker is Saddur's spot). (Retail's
			// 2nd goal 7134 "unlock the gate" wasn't imported.) III's goal 3498 = enter the city (ReachArea @4020,
			// completes on arrival). III is chain-final for now.
			await Chain(db, 12369, 12373);
			await SeedGoal(db, 12369, 3492, QuestGoalType.LootFromNpc, "Saddur", questItemId: 193186);
			await SeedGoal(db, 12373, 3498, QuestGoalType.ReachArea);

			// V "The Quarry" (12371) - granted by Turach's dialogue (ConversationNodes.GrantQuestId 12371), so NOT chained
			// here. goal 3495 "get 3 stone bricks" = CollectItem of item 193400 (its TrailingRefId), picked up from the 12
			// "Stone Brick" QuestItemEffect dynels at the quarry (4020). goal 3497 "deliver 3 to Turach" = TalkToNpc Turach
			// (his spawn 824/722 == the marker); the captured count 3 is kept (byte-exact @609) and the talk SOLVES it.
			// Decoded from Hoxxling gameplay2 (pickups fr4310/4367/4447 = DispatchAddItem+SimpleDynelPickup; deliver fr5102).
			await SeedGoal(db, 12371, 3495, QuestGoalType.CollectItem);
			await SeedGoal(db, 12371, 3497, QuestGoalType.TalkToNpc, "Turach");

			// The city TALK stages (#358), progressed by each NPC's conversation GrantQuestId (already imported) - so no
			// Chain() is needed, only the goal TARGET. Confirmed in the Hoxxling gameplay2 dump by the talk-then-complete
			// pairs: Laranga (guard, 0x4FDA) convo fr5680 -> 10286 done fr5829; Tina (0x4FF0) convo fr9214 -> 10287 done
			// fr9738. 10320 "talk Turach" (granted by the entry guard Laranga; talking to Turach completes it + his
			// dialogue grants the quarry 12371) is the pre-quarry "The Blacksmith" stage - inferred from the grant graph
			// + wiki, its dump proof is the quarry grant that predates gameplay2.
			await SeedGoal(db, 10320, 436, QuestGoalType.TalkToNpc, "Turach");  // IV The Blacksmith - talk Turach (pre-quarry)
			await SeedGoal(db, 10286, 399, QuestGoalType.TalkToNpc, "Laranga"); // VI The Guard - talk the city guard
			await SeedGoal(db, 10287, 400, QuestGoalType.TalkToNpc, "Tina");    // VII The Barmaid - talk Tina

			// VIII "Nadini" (10915, granted by Tina): the wiki objective is "Talk to Nadini", but Nadini 806 has NO
			// conversation imported, so her greeting can't fire the talk trigger (PickConversationScreen returns null).
			// Her spawn (1026,670 @4022 night city) EXACTLY matches the goal marker, so wire it as ReachArea (reach her)
			// - the robust stand-in until her dialogue is imported. Chain end (Shipwrecked -> "The Awakening I" is a
			// separate destiny quest, out of scope; Nadini can't grant it without dialogue).
			await SeedGoal(db, 10915, 1352, QuestGoalType.ReachArea);
		}

		// Set a quest's chain SUCCESSOR (the QuestFullUpdate the supersession swaps to). nextQuestId null = chain end.
		// BOOTSTRAP-ONLY (#262): only sets an UN-chained quest (NextQuestId still null), so a re-import never clobbers a
		// successor set by hand (the Quest editor) or a prior run. A fresh capture-harvested quest has NextQuestId null,
		// so it still bootstraps; a chain-end seed (null) is a no-op on the already-null quest.
		private static async Task Chain(DatabaseContext db, uint questId, uint? nextQuestId)
		{
			var quest = await db.QuestDefinitions.FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.NextQuestId != null)
				return;

			quest.NextQuestId = nextQuestId;
			await db.SaveChangesAsync();
		}

		// Static world objects (decor/containers/zones), deduped by (map, position). Transient loot bags skipped.
		public static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects)
		{
			// Purge any transient loot-bag rows that leaked in from old runtime persistence: a dropped loot bag is a
			// live-only world entity (built from scratch by LootBagTemplate, never DB-persisted), so it must not sit in
			// WorldObjects. They are the loot template (TemplateItemId 13782) WITHOUT a captured identity "[TAG]" suffix
			// ("Bag of loot", item-named drops). Captured STATIC containers (e.g. "cimmerian_treasure_chest [QO2V ]")
			// keep their tag and are preserved.
			var stale = await db.WorldObjects.Where(o => o.TemplateItemId == 13782 && o.Name != null && !o.Name.Contains("]")).ToListAsync();
			if (stale.Count > 0)
			{
				db.WorldObjects.RemoveRange(stale);
				Console.WriteLine($"World objects: purged {stale.Count} transient loot-bag row(s) (live-only entities).");
			}

			// The DB UNIQUE index is (MapId, PositionX/Y/Z, DisplayName). Two Adds sharing that key collide at
			// SaveChanges - and the existing-row query below does NOT see un-saved Adds, so two duplicate entries in
			// THIS batch both fell through to Add and crashed a full import (#394). Track the rows queued this batch by
			// the exact constraint key and update the pending/existing one instead of adding a second. SQLite treats a
			// NULL DisplayName as distinct (never collides), so those skip the dedup and always add, as before.
			var pending = new Dictionary<(uint MapId, float X, float Y, float Z, string Name), WorldObject>();
			foreach (var e in objects)
			{
				if (e.DisplayName == "Bag of loot")
					continue;

				// Populate FIRST: the DisplayName column is DERIVED by TryLiftColumns (from the object message body) and
				// can differ from e.DisplayName, so the dedup must key on the actual stored value the UNIQUE index sees.
				var built = PopulateObject(new WorldObject
				{
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z
				}, e);

				if (built.DisplayName is { } name)
				{
					var key = (e.MapId, e.X, e.Y, e.Z, name);
					if (pending.TryGetValue(key, out var queued))
					{
						PopulateObject(queued, e); // a duplicate within this batch: fold it into the row already queued
						continue;
					}

					var existing = await db.WorldObjects.FirstOrDefaultAsync(o => o.MapId == e.MapId && o.PositionX == e.X && o.PositionY == e.Y && o.PositionZ == e.Z && o.DisplayName == name);
					if (existing != null)
					{
						PopulateObject(existing, e);
						pending[key] = existing;
						continue;
					}

					pending[key] = built;
				}

				db.WorldObjects.Add(built);
			}

			Console.WriteLine("World objects: identity columns + geometry tail lifted from the dump at import (#187).");

			await db.SaveChangesAsync();
		}

		public static NpcSpawn Populate(NpcSpawn row, NpcSpawnEntry e)
		{
			row.CapturedHandle = e.Handle;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;
			// Race/Sex are type-level now (#287) - set on the NpcTemplate type in UpsertNpcTypes, not on the spawn row.
			row.Level = e.Level;
			row.MonsterScale = e.MonsterScale;
			row.MonsterData = e.MonsterData;
			row.HeadMesh = e.HeadMesh;
			row.HasEquipment = e.HasEquipment;
			row.Health = e.Health;

			// Per-placement stat pools (#244): lift the remaining level-scaled/per-placement values from this spawn's
			// capture (via the same codec the type core uses), overriding the type StatCore when the body is built.
			var placeBody = WorldObjectBuilder.ExtractBody(e.Data);
			if (placeBody != null && PlayerSpawnReader.TryReadCharacterMessage(placeBody, out var placeRead))
			{
				var core = NpcStatCoreCodec.Lift(placeRead.Message);
				row.Life = core.Life ?? 0;
				row.Mana = core.Mana ?? 0;
				row.MaxMana = core.MaxMana ?? 0;
				row.Stamina = core.Stamina ?? 0;
				row.MaxStamina = core.MaxStamina ?? 0;
				row.ApprenticeLevel = core.ApprenticeLevel ?? 0;
				row.VoiceSet = core.VoiceSet ?? 0;
				row.GuildId = core.GuildId;
				row.HearingSkill = core.HearingSkill;
				row.SightSkill = core.SightSkill;
				row.SmellingSkill = core.SmellingSkill;
				row.WireHealth = core.Health != core.Life
					? core.Health
					: null; // only when wire Health diverges from Life
			}

			row.Morphs.Clear();
			for (var i = 0; i < e.Morphs.Count; i++)
			{
				row.Morphs.Add(new NpcSpawnMorph
				{
					Ordinal = i,
					MorphId = e.Morphs[i].MorphId,
					Value = e.Morphs[i].Value,
					Text = e.Morphs[i].Text ?? ""
				});
			}

			row.Equipment.Clear();
			foreach (var eq in e.Equipment)
			{
				row.Equipment.Add(new NpcSpawnEquipment
				{
					Pos = eq.Pos,
					Template = eq.Template, // FK to Items; the quad + hash live there (harvested by the items module)
					Level = eq.Level
				});
			}

			return row;
		}

		public static WorldObject PopulateObject(WorldObject row, WorldObjectEntry e)
		{
			row.SubOpcode = e.SubOpcode;
			row.Name = e.Name;
			row.ObjectId = e.ObjectId;
			row.CapturedHandle = e.Handle;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;

			// Lift the identity columns + geometry tail from the dump body (the captured Data is no longer stored).
			SimpleDynelMessageCodec.TryLiftColumns(row, WorldObjectBuilder.ExtractBody(e.Data));
			return row;
		}

		// Marks every quest whose @609 objective region, rebuilt from the rows just imported, reproduces the captured
		// bytes exactly. Only those may ride the login snapshot - the region still has undecoded fields, and shipping
		// one we cannot reproduce asserts the client (statlist.cpp:134).
		public static async Task<(int Verified, int Total)> VerifyObjectiveRegions(DatabaseContext db, IReadOnlyDictionary<uint, List<List<QuestObjectiveGoal>>> capturedByQuest)
		{
			var defs = await db.QuestDefinitions.Include(q => q.Goals).ToDictionaryAsync(q => q.QuestId);
			int verified = 0, total = 0;

			foreach (var (questId, captures) in capturedByQuest)
			{
				if (!defs.TryGetValue(questId, out var def) || def.Goals.Count == 0)
					continue;

				total++;
				def.ObjectiveRegionVerified = captures.All(goals => QuestObjectiveRegion
					.Build(def, goals[0].Instance)
					.ToArray()
					.SequenceEqual(new QuestObjectiveRegion
					{
						Goals = goals
					}.ToArray()));

				if (def.ObjectiveRegionVerified)
					verified++;
			}

			await db.SaveChangesAsync();
			return (verified, total);
		}

		// Flattens the task tree into path.Nodes (every node gets PatrolPathId; the Parent/Children navs stay so EF
		// sets ParentId).
		private static void AddNodeTree(PatrolPath path, PatrolNode node)
		{
			path.Nodes.Add(node);
			foreach (var child in node.Children)
				AddNodeTree(path, child);
		}

		// Only ever ADDS a slot that is absent - an existing ref is authoritative, having come from a full-update.
		private static void AddRefIfMissing(QuestDefinition def, QuestTextSlot slot, CompletedQuestExtractor.TextRef text)
		{
			if (def.TextRefs.Any(r => r.Slot == slot))
				return;

			def.TextRefs.Add(new QuestTextRef
			{
				QuestId = def.QuestId,
				Slot = slot,
				RefId = text.RefId,
				Category = text.Category,
				KeyText = text.Key,
				Flags = text.Flags,
				IsLocalized = text.IsLocalized
			});
		}

		// Decodes the captured 54-byte ApplySpell effect payload into the NpcSpell columns (NpcSpellEffectCodec, #304).
		private static void ApplyEffect(NpcSpell s, byte[] payload)
		{
			if (payload is not { Length: NpcSpellEffect.Length })
				return;

			var fx = NpcSpellEffect.Parse(payload);
			s.Duration = fx.Duration;
			s.Magnitude = fx.Magnitude;
			s.Rank = fx.Rank;
			s.FlagA = fx.FlagA;
			s.FlagB = fx.FlagB;
			s.FlagC = fx.FlagC;
		}

		// Upsert (never wipe): refresh from a clean slate so stale handle-keyed (un-named) nodes don't linger. One
		// ConversationPacket is stored FULLY STRUCTURED (#188): decoded into typed columns + ConversationScreen rows,
		// no raw Data blob. A packet that doesn't decode to a recognised sub-opcode is a REJECT (throws) rather than a
		// silent raw-replay - the runtime has no Data fallback, so an un-decodable packet must surface here.
		private static ConversationPacket BuildPacket(byte[] data, int sequence)
		{
			if (ConversationScreenDecoder.Decode(data) is not { } cols)
				throw new InvalidOperationException($"Conversation packet #{sequence} did not decode to a recognised sub-opcode (no Data fallback exists).");

			var pkt = new ConversationPacket
			{
				Sequence = sequence,
				SubOpcode = cols.SubOpcode,
				Env = cols.Env,
				Field = cols.Field,
				Flag = cols.Flag,
				SenderInstance = cols.SenderInstance,
				CameraParams = cols.CameraParams
			};
			if (cols.Screens != null)
			{
				for (var i = 0; i < cols.Screens.Count; i++)
					pkt.Screens.Add(ConversationScreenColumns.ToRow(cols.Screens[i], i)); // throws if a screen isn't byte-exact round-trippable
			}

			return pkt;
		}

		private static PatrolNode CloneNode(PatrolNode n)
		{
			var clone = new PatrolNode
			{
				Ordinal = n.Ordinal,
				Mode = n.Mode,
				HeaderFlags = n.HeaderFlags,
				Duration = n.Duration,
				Remaining = n.Remaining,
				ListMin = n.ListMin,
				PathFlags = n.PathFlags,
				PathFloat10 = n.PathFloat10,
				PathFloat20 = n.PathFloat20,
				PathPacked1 = n.PathPacked1,
				PathPacked2 = n.PathPacked2,
				TrailerByte = n.TrailerByte,
				Speed = n.Speed,
				AnchorX = n.AnchorX,
				AnchorY = n.AnchorY,
				AnchorZ = n.AnchorZ,
				Flag = n.Flag,
				RotateTail = n.RotateTail,
				ApproachType = n.ApproachType,
				ApproachTarget = n.ApproachTarget,
				WordsData = n.WordsData?.ToArray()
			};

			foreach (var w in n.Waypoints)
			{
				clone.Waypoints.Add(new PatrolNodeWaypoint
				{
					Ordinal = w.Ordinal,
					PositionX = w.PositionX,
					PositionY = w.PositionY,
					PositionZ = w.PositionZ
				});
			}

			foreach (var child in n.Children)
			{
				var childClone = CloneNode(child);
				childClone.Parent = clone;
				clone.Children.Add(childClone);
			}

			return clone;
		}

		private static List<NpcSpawnEntry> DistinctByHandle(IEnumerable<NpcSpawnEntry> entries)
		{
			var seen = new HashSet<uint>();
			var result = new List<NpcSpawnEntry>();
			foreach (var e in entries)
			{
				if (seen.Add(e.Handle))
					result.Add(e);
			}

			return result;
		}

		// The beach starter (quest 10238) per-goal objective detail (the snapshot's @609 region). Idempotent.
		private static async Task SeedBeachQuestGoals(DatabaseContext db)
		{
			const uint beachQuestId = 10238;
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == beachQuestId);
			if (quest == null)
				return;

			quest.SnapshotUnknown118 = 4;
			quest.SnapshotUnknown14 = QuestcRegion.BeachN; // the beach record's `n` (@+0x14)
			quest.SnapshotUnknownEc = 1;
			quest.SnapshotUnknownF0 = 0x96;
			quest.SnapshotUnknownF8 = 15;
			quest.Level = 1;
			quest.HasSnapshotRecord = true;
			quest.SnapshotUnknown28 = 0;
			quest.IndexMagic = 0x57365154;
			quest.IndexMarker = 0x7E2;
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 7;
			quest.IndexTableField3 = 0;
			quest.IndexTrailerWord = 0x000156E7;
			quest.IndexGoalSteps = "10238,12369,12373,10320,12371,10286,10287,10915";

			// Overlay the two goals' full @609 detail (find-or-create by GoalId): the extractor may have already emitted
			// goal 1448 as the harvested PRIMARY goal (wire fields only) - this completes it and adds goal 3490 - so the
			// beach byte-exact @609 region is authoritative here regardless of what the capture harvested.
			var goal1 = quest.Goals.FirstOrDefault(g => g.GoalId == 1448) ?? Add(quest, 1448);
			goal1.Sequence = 1;
			goal1.RequiredCount = 1;
			goal1.Separator = 9;
			goal1.SubId = 180;
			goal1.MapId = 4010;
			goal1.MarkerX = 416;
			goal1.MarkerY = 701;
			goal1.Extra5 = 0;
			goal1.Extra6 = 0;
			goal1.ObjectiveRefId = 1448;
			goal1.ObjectiveCategory = 50304;
			goal1.ObjectiveKey = "pARATX1!DX]&#92;So+0*A9B";
			goal1.TrailingRefId = 180;
			goal1.TrailingCategory = 51200;
			goal1.TrailingKey = "b2Y2],LfVfAVps*9JX*G";
			goal1.TrailingLocalized = false;
			goal1.TrailingFlags = "";
			// #262: goal 1448 = "Enter the jungle" - a REACH objective (region ref 180/cat 51200, NOT a kill). Solved
			// when the player walks within range of its map marker (416,701 on 4010, the jungle mouth on the way to
			// Saddur), via the move-handler reach check. No TargetName (it's a place, not an NPC). BOOTSTRAP-ONLY.
			if (!IsWired(goal1))
				goal1.GoalType = QuestGoalType.ReachArea;

			var goal2 = quest.Goals.FirstOrDefault(g => g.GoalId == 3490) ?? Add(quest, 3490);
			goal2.Sequence = 2;
			goal2.RequiredCount = 1;
			goal2.Separator = 12;
			goal2.SubId = 0;
			goal2.MapId = 4010;
			goal2.MarkerX = 521;
			goal2.MarkerY = 757;
			goal2.Extra5 = 70099;
			goal2.Extra6 = 0x36314C56;
			goal2.ObjectiveRefId = 3490;
			goal2.ObjectiveCategory = 50304;
			goal2.ObjectiveKey = "cA!(EbPpfzUK&#92;WPN@!H!";
			goal2.TrailingRefId = 9500;
			goal2.TrailingCategory = 51000;
			goal2.TrailingKey = "]+/sUK&#39;G3mO)clF`)9Jh";
			goal2.TrailingLocalized = true;
			goal2.TrailingFlags = "m";

			// #262: the SERVER-trigger fields (the wire/@609 detail above only drives the journal DISPLAY). 10238 is
			// "Shipwrecked / I. The Slaver" (start Kalanthes, aoc.fandom.com/wiki/Quest:Shipwrecked): its completing goal
			// is 3490 = "Kill slavemaster Saddur" (TrailingRefId 9500 = Saddur's template, at his 521/757 spawn), which
			// supersedes to stage II "The Acheronian Gate" (12369) - proven by the Hoxxling gameplay capture
			// (Saddur dies fr11942 -> 10238 completes -> 12369 -> 12373). BOOTSTRAP-ONLY (see IsWired / Chain): a
			// re-import won't revert these once the Quest editor (or a prior run) has wired them.
			quest.NextQuestId ??= 12369;
			if (!IsWired(goal2))
			{
				goal2.GoalType = QuestGoalType.Kill;
				goal2.TargetName = "Saddur";
			}

			await db.SaveChangesAsync();
			Console.WriteLine("Seeded beach quest 10238 goal-detail (2 goals) + Saddur kill trigger -> 12369 (#262).");

			static QuestGoalDefinition Add(QuestDefinition q, uint goalId)
			{
				var g = new QuestGoalDefinition
				{
					GoalId = goalId
				};
				q.Goals.Add(g);
				return g;
			}
		}

		// The Tortage intro quest CHAIN (#261): 13479 is a four-stage supersession chain. Idempotent.
		private static async Task SeedCasildaChain(DatabaseContext db)
		{
			await Chain(db, 13479, 13563);
			await SeedGoal(db, 13479, 5744, QuestGoalType.Kill, "Scavenger Tuut", required: 1);
			// The loot stage's key item: Scavenger Tuut's bag drops 319224 "Scavenger's Key" (#261), so the player sees
			// and takes the actual key, not just an empty source-matched bag.
			await Chain(db, 13563, 13480);
			await SeedGoal(db, 13563, 5867, QuestGoalType.LootFromNpc, "Scavenger Tuut", questItemId: 319224, required: 1);
			await Chain(db, 13480, 12407);
			await SeedGoal(db, 13480, 5745, QuestGoalType.TalkToNpc, "Casilda", required: 1);
			await Chain(db, 12407, null); // escort is the chain end
			await SeedGoal(db, 12407, 3544, QuestGoalType.Escort, "Casilda", required: 1);
		}

		// Casilda's quest 13479 snapshot Quest_c scalars (#255): journal-record fields that live ONLY in the
		// FullChar snapshot. Idempotent - set every run.
		private static async Task SeedCasildaQuestSnapshot(DatabaseContext db)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs).FirstOrDefaultAsync(q => q.QuestId == 13479);
			if (quest == null)
				return;

			quest.Level = 3;
			quest.HasSnapshotRecord = true;
			quest.SnapshotUnknown118 = 3;
			quest.SnapshotUnknown14 = QuestcRegion.BeachN;
			quest.SnapshotUnknownEc = 1;
			quest.SnapshotUnknownF0 = 80;
			quest.SnapshotUnknownF8 = 7;
			quest.SnapshotUnknown28 = 80;
			quest.IndexMagic = 0x52524C33;
			quest.IndexTableField1 = 1;
			quest.IndexTableField2 = 3;
			quest.IndexTableField3 = 0;
			quest.IndexGoalSteps = "13479,13563,13480,12407";

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
				goal.Extra6 = 0x54375554;
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

		// Configure one goal's SERVER-trigger fields (find-or-create by GoalId): the type + how it's matched at runtime
		// (Kill/LootFromNpc/TalkToNpc by NAME, Kill also by TrailingRefId template id, CollectItem by TrailingRefId item,
		// ReachArea by marker). All the WIRE/@609 display fields (markers, keys, counts) are left as harvested. `required`
		// is set ONLY when given - pass it for a hand-authored chain goal, omit it to PRESERVE the captured RequiredCount
		// (which is in the byte-exact objective region). Chain the quest separately with Chain().
		// BOOTSTRAP-ONLY (#262): only configures an UN-WIRED goal (still the fresh-harvest default: GoalType Kill, no
		// TargetName/QuestItemId), so a re-import never clobbers trigger fields set by hand (the Quest editor) or a prior
		// run. A newly-created goal is un-wired, so a fresh import still bootstraps.
		private static async Task SeedGoal(DatabaseContext db, uint questId, uint goalId, QuestGoalType type, string target = null, uint? questItemId = null, uint? required = null)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null)
				return;

			var goal = quest.Goals.FirstOrDefault(g => g.GoalId == goalId);
			if (goal != null && IsWired(goal))
				return; // already configured (by hand or a prior seed) - don't overwrite

			if (goal == null)
			{
				goal = new QuestGoalDefinition
				{
					GoalId = goalId,
					Sequence = 1
				};
				quest.Goals.Add(goal);
			}

			goal.GoalType = type;
			goal.TargetName = target;
			goal.QuestItemId = questItemId;
			if (required is { } r)
				goal.RequiredCount = r;
			if (goal.Sequence == 0)
				goal.Sequence = 1;

			// NB the key item's Type (Quest 113) is NOT forced here - the `items` module types it from data: the chain
			// captures deliver the key via a live DispatchAddItem (0x2020) to the 0x71 quest container, which
			// ItemHashHarvester.ExtractFromAddItem reads as ItemType.Quest. Run the `items` module to (re)type it.

			await db.SaveChangesAsync();
			Console.WriteLine($"Seeded goal: quest {questId} goal {goalId} = {type}{(target != null ? $" '{target}'" : "")}.");
		}

		// A goal is "wired" once its server-trigger fields diverge from the fresh-harvest default (GoalType Kill, no
		// TargetName/QuestItemId) - i.e. a Seed* or the Quest editor has configured it. Bootstrap-only seeds skip a wired
		// goal so a re-import never reverts a hand edit.
		private static bool IsWired(QuestGoalDefinition goal)
		{
			return goal.GoalType != QuestGoalType.Kill || goal.TargetName != null || goal.QuestItemId != null;
		}

		// Upserts the NpcTemplate TYPE rows (#244). Keyed by TemplateId; Id stays stable across re-imports.
		private static async Task<Dictionary<uint, NpcTemplate>> UpsertNpcTypes(DatabaseContext db, List<NpcSpawnEntry> entries)
		{
			var byTemplate = await db.NpcTemplates.Include(n => n.StatCore).ToDictionaryAsync(n => n.TemplateId);

			// #243: the fresh minimal post-IIR recordCount per type (the type-stable 1 or 2, not an animal/humanoid split),
			// read from a NO-MORPH CAPTURE (the dump-file body, e.Data) - NOT the stored NpcSpawn.Data column, which is
			// being retired. The recordCount word is stable per type across minimal AND runtime-bearing captures, so this
			// reads it off either shape (covering the runtime-bearing types that have no minimal-form capture). Lets a
			// no-morph spawn build its post-IIR from scratch (BuildMinimalPostIir(rc)) instead of the Data blob.
			var minimalRcByTemplate = new Dictionary<uint, uint>();
			foreach (var e in entries)
			{
				if (e.TemplateId == 0 || minimalRcByTemplate.ContainsKey(e.TemplateId))
					continue;

				var b = WorldObjectBuilder.ExtractBody(e.Data);
				if (b != null && NpcSpawnScaffold.TryReadNoMorphRecordCount(b, out var rc))
					minimalRcByTemplate[e.TemplateId] = rc;
			}

			foreach (var e in entries)
			{
				if (e.TemplateId == 0)
					continue;

				if (byTemplate.TryGetValue(e.TemplateId, out var npc))
				{
					if (!string.IsNullOrEmpty(e.Name))
						npc.Name = e.Name;
					// Community/Race/Sex now live on StatCore (set below via Lift), not the retired NpcTemplate columns.
				}
				else
				{
					npc = new NpcTemplate
					{
						TemplateId = e.TemplateId,
						Name = e.Name
					};
					db.NpcTemplates.Add(npc);
					byTemplate[e.TemplateId] = npc;
				}

				// #244: lift the type-level CharacterIIR stat core from a representative capture (the first spawn of the
				// type that decodes), so a spawn body can be constructed from typed columns instead of the Data blob.
				if (npc.StatCore == null || npc.StatCore.IirHeader == null)
				{
					var body = WorldObjectBuilder.ExtractBody(e.Data);
					if (body != null && PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					{
						npc.StatCore ??= NpcStatCoreCodec.Lift(read.Message);
						npc.StatCore.IirHeader ??= NpcStatCoreCodec.ReadIirHeaderWord(body);
					}
				}

				// #243: stamp the type's fresh minimal post-IIR recordCount from the dump-derived scan above.
				if (npc.StatCore != null && minimalRcByTemplate.TryGetValue(e.TemplateId, out var minRc))
					npc.StatCore.MinimalPostIirRecordCount = minRc;
			}

			await db.SaveChangesAsync();
			return byTemplate;
		}
	}
}
