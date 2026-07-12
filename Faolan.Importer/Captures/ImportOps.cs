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
					NpcTemplateId = e.NpcTemplateId is { } t && knownTemplates.Contains(t) ? t : null,
					CapturedInstance = e.CapturedInstance,
					ConditionScript = e.ConditionScript,
					GrantQuestId = e.GrantQuestId,
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

		// NPC patrol routes, one per NpcSpawn (#316). The route is correlated to its NPC by the shared spawn position
		// (the spawns were imported + saved just before), then FK'd; the columnarized task tree is replaced wholesale
		// on re-import.
		public static async Task ImportPatrolPaths(DatabaseContext db, List<PatrolPathEntry> paths)
		{
			foreach (var e in paths)
			{
				var spawn = await db.NpcSpawns.FirstOrDefaultAsync(
					n => n.MapId == e.MapId && n.PositionX == e.Position.X && n.PositionY == e.Position.Y && n.PositionZ == e.Position.Z);
				if (spawn == null)
					continue; // a route with no matching spawn (should not happen for burst-truth patrols) - skip

				var existing = await db
					.PatrolPaths.Include(p => p.Nodes).ThenInclude(n => n.Waypoints)
					.FirstOrDefaultAsync(p => p.NpcSpawnId == spawn.Id);

				if (existing == null)
				{
					existing = new PatrolPath { NpcSpawnId = spawn.Id };
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

		// Flattens the task tree into path.Nodes (every node gets PatrolPathId; the Parent/Children navs stay so EF
		// sets ParentId).
		private static void AddNodeTree(PatrolPath path, PatrolNode node)
		{
			path.Nodes.Add(node);
			foreach (var child in node.Children)
				AddNodeTree(path, child);
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
				clone.Waypoints.Add(new PatrolNodeWaypoint
				{
					Ordinal = w.Ordinal,
					PositionX = w.PositionX,
					PositionY = w.PositionY,
					PositionZ = w.PositionZ
				});

			foreach (var child in n.Children)
			{
				var childClone = CloneNode(child);
				childClone.Parent = clone;
				clone.Children.Add(childClone);
			}

			return clone;
		}

		// Quest journal-content definitions, refreshed per harvested quest; un-harvested quests untouched.
		public static async Task ImportQuestDefinitions(DatabaseContext db, List<QuestDefinition> definitions)
		{
			// The world is pruned to Tortage, so some harvested quests point their map marker at a playfield that isn't
			// in the (pruned) Maps catalog (e.g. quest 19522 -> map 30200). QuestDefinition.MapId is a required FK to
			// Maps, so importing those would fail the FK. Skip them with a clear line rather than silently — they'll
			// re-import once/if their map is added back.
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var skipped = 0;

			var added = 0;
			var updated = 0;
			foreach (var def in definitions)
			{
				if (!knownMaps.Contains(def.MapId))
				{
					Console.WriteLine($"Quest {def.QuestId}: skipped - map {def.MapId} not in the (pruned) Maps catalog.");
					skipped++;
					continue;
				}

				var existing = await db.QuestDefinitions.Include(q => q.TextRefs).FirstOrDefaultAsync(q => q.QuestId == def.QuestId);
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
			Console.WriteLine($"Quest definitions (journal content): {definitions.Count} harvested, {added} new, {updated} updated, {skipped} skipped (map not in catalog).");

			await SeedBeachQuestGoals(db);
			await SeedCasildaChain(db);
			await SeedCasildaQuestSnapshot(db);
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

			foreach (var e in objects)
			{
				if (e.DisplayName == "Bag of loot")
					continue;

				var candidates = await db.WorldObjects.Where(o => o.MapId == e.MapId && o.PositionX == e.X && o.PositionY == e.Y && o.PositionZ == e.Z).ToListAsync();
				var existing = candidates.FirstOrDefault(o => WorldObjectBuilder.ReadIdentity(o) == e.DisplayName);
				if (existing == null)
				{
					db.WorldObjects.Add(PopulateObject(new WorldObject
					{
						MapId = e.MapId,
						PositionX = e.X,
						PositionY = e.Y,
						PositionZ = e.Z
					}, e));
				}
				else
					PopulateObject(existing, e);
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
				for (var i = 0; i < cols.Screens.Count; i++)
					pkt.Screens.Add(ConversationScreenColumns.ToRow(cols.Screens[i], i)); // throws if a screen isn't byte-exact round-trippable

			return pkt;
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

			quest.SnapshotN = 4;
			quest.SnapshotFieldEc = 1;
			quest.SnapshotFieldF0 = 0x96;
			quest.SnapshotFieldF8 = 15;
			quest.SnapshotState = 1;
			quest.SnapshotField28 = 0;
			quest.IndexMagic = 0x57365154;
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
				GoalId = 1448,
				Sequence = 1,
				RequiredCount = 1,
				Separator = 9,
				SubId = 180,
				MapId = 4010,
				MarkerX = 416,
				MarkerY = 701,
				Extra5 = 0,
				Extra6 = 0,
				ObjectiveRefId = 1448,
				ObjectiveCategory = 50304,
				ObjectiveKey = "pARATX1!DX]&#92;So+0*A9B",
				TrailingRefId = 180,
				TrailingCategory = 51200,
				TrailingKey = "b2Y2],LfVfAVps*9JX*G",
				TrailingLocalized = false,
				TrailingFlags = ""
			});
			quest.Goals.Add(new QuestGoalDefinition
			{
				GoalId = 3490,
				Sequence = 2,
				RequiredCount = 1,
				Separator = 12,
				SubId = 0,
				MapId = 4010,
				MarkerX = 521,
				MarkerY = 757,
				Extra5 = 70099,
				Extra6 = 0x36314C56,
				ObjectiveRefId = 3490,
				ObjectiveCategory = 50304,
				ObjectiveKey = "cA!(EbPpfzUK&#92;WPN@!H!",
				TrailingRefId = 9500,
				TrailingCategory = 51000,
				TrailingKey = "]+/sUK&#39;G3mO)clF`)9Jh",
				TrailingLocalized = true,
				TrailingFlags = "m"
			});

			await db.SaveChangesAsync();
			Console.WriteLine("Seeded beach quest 10238 goal-detail (2 goals) for the @609 objective region.");
		}

		// The Tortage intro quest CHAIN (#261): 13479 is a four-stage supersession chain. Idempotent.
		private static async Task SeedCasildaChain(DatabaseContext db)
		{
			await SeedChainStage(db, 13479, 13563, QuestGoalType.Kill, "Scavenger Tuut", 5744);
			// The loot stage's key item: Scavenger Tuut's bag drops 319224 "Scavenger's Key" (#261), so the player sees
			// and takes the actual key, not just an empty source-matched bag.
			await SeedChainStage(db, 13563, 13480, QuestGoalType.LootFromNpc, "Scavenger Tuut", 5867, questItemId: 319224);
			await SeedChainStage(db, 13480, 12407, QuestGoalType.TalkToNpc, "Casilda", 5745);
			await SeedChainStage(db, 12407, 0, QuestGoalType.Escort, "Casilda", 3544);
		}

		// Casilda's quest 13479 snapshot Quest_c scalars (#255): journal-record fields that live ONLY in the
		// FullChar snapshot. Idempotent - set every run.
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

		private static async Task SeedChainStage(DatabaseContext db, uint questId, uint nextQuestId, QuestGoalType goalType, string target, uint goalId, uint required = 1, uint questItemId = 0)
		{
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null)
				return;

			quest.NextQuestId = nextQuestId;

			var goal = quest.Goals.FirstOrDefault(g => g.GoalId == goalId) ?? quest.Goals.FirstOrDefault();
			if (goal == null)
			{
				goal = new QuestGoalDefinition
				{
					GoalId = goalId,
					Sequence = 1
				};
				quest.Goals.Add(goal);
			}

			goal.GoalType = goalType;
			goal.TargetName = target;
			goal.RequiredCount = required;
			goal.QuestItemId = questItemId;
			if (goal.Sequence == 0)
				goal.Sequence = 1;

			// NB the key item's Type (Quest 113) is NOT forced here - the `items` module types it from data: the chain
			// captures deliver the key via a live DispatchAddItem (0x2020) to the 0x71 quest container, which
			// ItemHashHarvester.ExtractFromAddItem reads as ItemType.Quest. Run the `items` module to (re)type it.

			await db.SaveChangesAsync();
			Console.WriteLine($"Seeded chain stage: quest {questId} = {goalType} '{target}' (goal {goalId}, required {required}, keyItem {questItemId}) -> {nextQuestId}.");
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
