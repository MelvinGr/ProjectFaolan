using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
		// each type taken from its single authoritative session. SUMMONED creatures are excluded two ways - by the
		// wire's own owner-binding (`SummonedHandles`, the general rule) and by `petNames`, the seeded summon-template
		// names (the fallback for summons whose binding was never captured). See both call sites below.
		// How near a spawn must stand to a candidate patrol route's nearest waypoint to be counted as walking it (#425).
		// Measured, not chosen: over the 139 spawns that carry their own route by handle, spawn-to-nearest-waypoint is
		// 0.0 at the median, 4.4 at p90 and 7.8 at p95.
		private const float RouteMatchRadius = 10f;

		// `classSpecificMaps` = the maps whose population is per class (Map.ClassSpecific - the city armories). A spawn
		// harvested on one of them is evidence about ONE class's copy of the playfield, so it is stamped with the class
		// the capture was played on and elected only against captures of that same class.
		public static (List<NpcSpawnEntry> Spawns, List<PatrolPathEntry> Patrols, List<NpcSpawnEntry> ObservedTypes) BuildBurstTruthSpawns(IReadOnlyList<NamedCapture> captures, IReadOnlySet<string> petNames,
			IReadOnlySet<uint> classSpecificMaps)
		{
			// SUMMONS ARE DROPPED PER CAPTURE, BEFORE ANYTHING ELSE SEES THEM. A pet handle is session-scoped, so the
			// exclusion has to be, too - and doing it here rather than at the placement step keeps a summon out of the
			// morph/equipment/route backfills and out of the richest-capture election as well, not just out of the
			// final placement list.
			var perCap = captures
				.Select(c =>
				{
					var summoned = SummonedHandles(c.Packets);
					var playedClass = classSpecificMaps.Count > 0
						? CaptureCharacterImporter.DetectPlayedClass(c.Packets)
						: null;
					return (All: DistinctByHandle(StampClass(NpcSpawnExtractor.Extract(c.Packets).Where(e => !summoned.Contains(e.Handle)), playedClass, c.Name)),
						Burst: DistinctByHandle(StampClass(NpcSpawnExtractor.ExtractLoginBurst(c.Packets).Where(e => !summoned.Contains(e.Handle)), playedClass, c.Name)),
						Routes: PatrolPathExtractor.ExtractByHandle(c.Packets));
				})
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

			// Richest PATROL ROUTE per (map, template) - the third backfill of the same shape as the two above, and the
			// one that was missing (#425).
			//
			// A route rides the burst-truth capture's own observation (`best.Routes`, keyed by that session's handle),
			// so a capture that wins on spawn COUNT while happening to observe fewer NPCs mid-patrol takes the map's
			// patrols down with it: importing the two beach->city captures moved PatrolNodes 632 -> 596 for zero new
			// information, six spawns on map 4020 losing their route entirely. But a route is per-NPC CONTENT - the
			// path is a stable asset of that spawn - so it can be backfilled from ANY capture that saw the NPC, exactly
			// as morphs and equipment already are.
			//
			// Keyed by (map, template) for the reason spelled out above: NAME is not a key, because ~16 unrelated
			// templates share the internal name "Halen van Dummy" and keying by it would hand one NPC another's path.
			//
			// ⚠️ Template alone is NOT enough to pick the route, and measuring said so. Handing every same-template
			// spawn the one route observed for that template recovered 207 PatrolNodes where the regression lost 36 -
			// the extra 171 being ten guards of one template, one of them ever seen walking, all ten then walking the
			// same path and converging on one spot. That is a worse artefact than the missing route, and in the row
			// counts it would have looked like a big win.
			//
			// So the route is tied to the spawn POSITIONALLY: a spawn inherits a candidate route only if it is standing
			// ON it. The threshold is measured, not invented - across the 139 spawns that DO carry their own route by
			// handle, the distance from the spawn to the nearest waypoint of its own path is 0.0 at the median, 4.4 at
			// p90 and 7.8 at p95. RouteMatchRadius is 10, which accepts ~95% of genuine matches while being far tighter
			// than the spacing between distinct patrol routes on a map. An AMBIGUOUS spawn - two candidate routes in
			// range - is left alone rather than guessed at.
			var routeCandidates = new Dictionary<(uint Map, uint Template), List<PatrolPathEntry>>();
			foreach (var cap in perCap)
			{
				var byHandle = cap.All.ToDictionary(e => e.Handle, e => e);
				foreach (var (handle, route) in cap.Routes)
				{
					if (route.Waypoints.Count == 0 || !byHandle.TryGetValue(handle, out var spawn))
						continue;

					if (!routeCandidates.TryGetValue((spawn.MapId, spawn.TemplateId), out var list))
						routeCandidates[(spawn.MapId, spawn.TemplateId)] = list = [];
					list.Add(route);
				}
			}

			var routesForTemplate = routeCandidates.ToDictionary(kv => kv.Key, kv => kv.Value
				.GroupBy(WaypointKey)
				.Select(g => g.First())
				.ToList());

			var spawns = new List<NpcSpawnEntry>();
			var patrols = new List<PatrolPathEntry>();

			// PER (map, TYPE), not per map (#486). The login burst is an INSTANT - one snapshot of what is standing
			// there - so it gives true density without counting a respawn twice, and it stays the preferred source.
			// But electing ONE capture for a whole map and then locking every type it saw to that capture does not
			// survive a large, interest-streamed zone.
			//
			// Poitain is the case: `..._combat` entered it by a mid-session MAP CHANGE and the client polled almost
			// immediately, so its burst window holds 5 spawns while 875 more arrive after the poll; `..._combat2` is a
			// fresh LOGIN into the same zone and its burst holds 59. The 59 legitimately won the map, which then
			// locked all 59 type names - and that excluded the first capture's 875 later observations from the
			// non-burst pass entirely. Poitain Guard 13 -> 2, Zingaran Alchemist 12 -> 3.
			//
			// So each type takes whichever single capture saw the most of it, preferring a burst when the burst is at
			// least as complete. One capture per type is still the rule (#266) - this only stops a type being pinned
			// to a capture that barely saw it. `Merge` dedups the result by position, so a respawn on the same anchor
			// inside the chosen capture still collapses.
			//
			// On a class-specific map the key also carries the CLASS, so each class's copy of the armory elects its own
			// truth capture independently and the two sets never compete (a necromancer capture and a herald capture see
			// disjoint quartermasters on map 5007 - without the class in the key they would simply be unioned onto one
			// map and every class served all of them).
			foreach (var (map, cls, name) in perCap.SelectMany(p => p.All).Select(e => (e.MapId, e.ClassId, Name: e.Name ?? "")).Distinct())
			{
				var bestBurst = perCap.OrderByDescending(p => p.Burst.Count(Matches)).First();
				var burstCount = bestBurst.Burst.Count(Matches);

				var bestAll = perCap.OrderByDescending(p => p.All.Count(Matches)).First();
				var allCount = bestAll.All.Count(Matches);

				if (burstCount >= allCount && burstCount > 0)
					Take(bestBurst.Burst.Where(Matches).ToList(), bestBurst.Routes);
				else if (allCount > 0)
					Take(bestAll.All.Where(Matches).ToList(), bestAll.Routes);

				continue;

				bool Matches(NpcSpawnEntry e)
				{
					return e.MapId == map && e.ClassId == cls && (e.Name ?? "") == name;
				}
			}

			// Pets are content of the creature TYPE (lifted onto the type columns by CapturePetImporter, spawned from the type
			// at summon time - #307/#244), NEVER world placements. A captured session's SUMMONED pets otherwise leak in
			// here as permanent NpcSpawns and stand around forever where the player summoned them (the necro pet cluster
			// on Old Tarantia).
			//
			// This is the SECOND of two nets, and it is the weaker one. `SummonedHandles` above is the general rule -
			// it asks the wire who owns the creature - and this one only knows the names we happened to seed. Both are
			// kept because each catches what the other misses, MEASURED on the corpus:
			//   * owner-binding only:  Parasitic Soul x2, Demon Avenger, Tamed Puma  (none of them in the name roster)
			//   * name roster only:    Mutilator x10                                 (no PetIIR ever captured for it)
			// So dropping either one regresses. If a capture ever observes the Mutilator's owner-binding, this net
			// becomes redundant and can go.
			var placements = spawns.Where(e => !petNames.Contains((e.Name ?? string.Empty).ToLowerInvariant())).ToList();

			// The placements are position-deduped; the TYPES are not. A creature that loses the position dedup is
			// still a creature we observed, and its type must survive even though its placement does not - the
			// encounter boss `Eerika of the Maidens` spawns on the exact spot her lure vacated, so `Merge` (which keys
			// on position and keeps the first) dropped her, and with her every trace: no NpcTemplate, no NpcStatCore,
			// level 80 and 8519 HP thrown away (#501). Harvesting the type off the UNMERGED list keeps her, while she
			// correctly gets no placement - a trigger spawn is not a world placement, and a type with no NpcSpawn row
			// is an established shape (pets and mounts, 18 templates today).
			return (NpcSpawnExtractor.Merge(placements), patrols, placements);

			// The one unambiguous candidate route this spawn is standing on, or null.
			PatrolPathEntry? BackfilledRoute(NpcSpawnEntry e)
			{
				if (!routesForTemplate.TryGetValue((e.MapId, e.TemplateId), out var candidates))
					return null;

				var here = new Vector3(e.X, e.Y, e.Z);
				var inRange = candidates.Where(c => c.Waypoints.Min(w => Vector3.Distance(w, here)) <= RouteMatchRadius).ToList();
				return inRange.Count == 1
					? inRange[0]
					: null;
			}

			// Tags the spawns this capture saw on a CLASS-SPECIFIC map with the class it was played on, and leaves every
			// other spawn class-agnostic (null).
			//
			// A spawn on a class-specific map from a capture whose class could not be determined is DROPPED, not kept as
			// null: null means "every class sees this", so keeping it would put one class's quartermasters in front of
			// all twelve - precisely the bug the column exists to fix. Losing a few rows from an unattributable trace is
			// the cheap side of that trade, and it is reported rather than silent.
			List<NpcSpawnEntry> StampClass(IEnumerable<NpcSpawnEntry> entries, ConanClass? playedClass, string captureName)
			{
				var result = new List<NpcSpawnEntry>();
				var dropped = 0;
				foreach (var e in entries)
				{
					if (!classSpecificMaps.Contains(e.MapId))
					{
						result.Add(e);
						continue;
					}

					if (playedClass is not { } cls)
					{
						dropped++;
						continue;
					}

					result.Add(e with
					{
						ClassId = cls
					});
				}

				if (dropped > 0)
					Console.WriteLine($"NPC spawns: dropped {dropped} spawn(s) on class-specific map(s) from '{captureName}' - the played class could not be determined.");

				return result;
			}

			// A route's identity for the agreement test: its waypoint polyline, rounded so float noise across captures
			// does not read as a different path.
			static string WaypointKey(PatrolPathEntry r)
			{
				return string.Join("|", r.Waypoints.Select(w => $"{w.X:F1},{w.Y:F1},{w.Z:F1}"));
			}

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

					// This capture's own observation first; otherwise the unambiguous cross-capture route for the
					// template, so a session that simply did not catch this NPC walking no longer costs it its path.
					if (routes.TryGetValue(e.Handle, out var r) && r.Waypoints.Count > 0)
					{
						patrols.Add(new PatrolPathEntry(e.MapId, new Vector3(e.X, e.Y, e.Z), r.Waypoints, 0, r.Task)
						{
							ClassId = e.ClassId
						});
					}
					else if (BackfilledRoute(e) is { } backfilled)
					{
						patrols.Add(new PatrolPathEntry(e.MapId, new Vector3(e.X, e.Y, e.Z), backfilled.Waypoints, 0, backfilled.Task)
						{
							ClassId = e.ClassId
						});
					}
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
				// Maps and areas are FK targets - a placeholder beats dropping the quest (it's okay to insert
				// maps without a name).
				if (q.MapId != 0 && maps.Add(q.MapId))
				{
					db.Maps.Add(new Map
					{
						Id = q.MapId,
						Name = ""
					});
				}

				if (q.AreaId != 0 && areas.Add(q.AreaId))
				{
					db.QuestAreas.Add(new QuestArea
					{
						AreaId = q.AreaId
					});
				}

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

		// OVERWRITE rebuilds the tree from the whole corpus; UPSERT merges a dump into what is already stored (#419),
		// which is what makes dropping a single new capture in useful. Both run the same comparison - overwrite just
		// starts from an empty table, so "nothing stored" falls out rather than being a second code path.
		//
		// The merge rule is the extractor's, applied against the database: the MOST COMPLETE capture of a screen wins
		// (most response packets), so a truncated dump can never displace a screen already captured in full. Two
		// fields are merged rather than overwritten - GrantQuestId and NpcTemplateId are sparsely observed (only the
		// capture that walked the accept branch sees the grant), so a dump that did not see one must not clear it.
		public static async Task<ConversationImportResult> ImportConversations(DatabaseContext db, List<ConversationEntry> conversations, bool overwrite)
		{
			if (overwrite)
			{
				// The drop MUST land before the inserts are staged. The node ids are derived from the node's identity,
				// so a re-import of the same corpus reuses the very same keys - queueing the deletes and the inserts
				// into one SaveChanges races them on the same primary key ("UNIQUE constraint failed:
				// ConversationScreens..."). ExecuteDelete runs the DELETE immediately (inside the importer's
				// transaction) and cascades to packets/screens/parameters in the database.
				await db.ConversationNodes.ExecuteDeleteAsync();

				// ...but it goes straight to the database and does NOT tell the change tracker, so anything this
				// context already holds would collide on the identity map when the same (derived) ids are re-added.
				foreach (var entry in db.ChangeTracker.Entries<ConversationNode>().ToList())
					entry.State = EntityState.Detached;
			}

			// A conversation NPC's template must exist to be the FK target (#244); the world is pruned, so a template
			// resolved from a capture may not be imported - null it out (name-only fallback) rather than fail the FK.
			var knownTemplates = (await db.NpcTemplates.Select(t => t.TemplateId).ToListAsync()).ToHashSet();

			// The node id is DERIVED from the node's identity (ConversationNode.ComputeId), so a re-harvest of the same
			// corpus reproduces the same ids and faolan.sql doesn't churn. Two entries hashing to one id is either the
			// same screen twice (the extractor's Merge should already have deduped it) or a genuine hash collision -
			// both are import bugs, so name the pair and fail rather than let one screen silently overwrite the other.
			var byId = new Dictionary<uint, string>();
			var desired = new List<ConversationNode>();

			foreach (var e in conversations)
			{
				// The NAME is the identity the runtime routes on, so it must be present. An entry whose NPC couldn't be
				// named from its capture's spawns keeps the "#<handle>" fallback the extractor's Merge already dedups
				// on - a distinct, addressable identity instead of a NULL (which no key constrains).
				var npcName = string.IsNullOrEmpty(e.NpcName)
					? $"#{e.CapturedNpcHandle}"
					: e.NpcName;
				var id = ConversationNode.ComputeId(npcName, e.TypeId, e.AnswerPath, e.ConditionQuestId, e.ConditionQuestState, e.ServerInitiated, e.TriggerWorldObjectId);
				var identity = $"{npcName}/{e.TypeId}/'{e.AnswerPath}'/{e.ConditionQuestId}/{e.ConditionQuestState}";
				if (!byId.TryAdd(id, identity))
					throw new InvalidOperationException($"Conversation node id 0x{id:X8} is not unique: '{identity}' collides with '{byId[id]}'.");

				desired.Add(new ConversationNode
				{
					Id = id,
					TypeId = e.TypeId,
					AnswerPath = e.AnswerPath,
					NpcName = npcName,
					NpcTemplateId = e.NpcTemplateId is { } t && knownTemplates.Contains(t)
						? t
						: null,
					ConditionQuestId = e.ConditionQuestId,
					ConditionQuestState = e.ConditionQuestState,
					GrantQuestId = e.GrantQuestId == 0
						? null
						: e.GrantQuestId,
					ServerInitiated = e.ServerInitiated,
					TriggerWorldObjectId = e.TriggerWorldObjectId,
					Packets = e.Packets.Select((p, i) => BuildPacket(p, i)).ToList()
				});
			}

			// What is already there, by id - only the fields the merge decides on.
			var wanted = desired.Select(d => d.Id).ToList();
			var stored = await db
				.ConversationNodes.Where(n => wanted.Contains(n.Id))
				.Select(n => new
				{
					n.Id,
					PacketCount = n.Packets.Count,
					n.GrantQuestId,
					n.NpcTemplateId
				})
				.ToDictionaryAsync(x => x.Id);

			var insert = new List<ConversationNode>();
			var replace = new List<uint>();
			var kept = 0;

			foreach (var node in desired)
			{
				if (!stored.TryGetValue(node.Id, out var existing))
				{
					insert.Add(node);
					continue;
				}

				if (node.Packets.Count > existing.PacketCount)
				{
					// A richer capture of a screen we already hold: carry the sparse fields across so replacing the
					// screen cannot lose a grant (or a template gate) that only the older capture observed.
					node.GrantQuestId ??= existing.GrantQuestId;
					node.NpcTemplateId ??= existing.NpcTemplateId;
					replace.Add(node.Id);
					insert.Add(node);
					continue;
				}

				// The stored screen is at least as complete, so it stays - but it can still LEARN the sparse fields
				// from this capture.
				kept++;
				if (node.GrantQuestId is { } grant || node.NpcTemplateId is { } template)
				{
					var row = await db.ConversationNodes.FirstAsync(n => n.Id == node.Id);
					row.GrantQuestId ??= node.GrantQuestId;
					row.NpcTemplateId ??= node.NpcTemplateId;
				}
			}

			// Same-key delete-then-insert has to be two round-trips, for the reason spelled out above.
			if (replace.Count > 0)
			{
				await db.ConversationNodes.Where(n => replace.Contains(n.Id)).ExecuteDeleteAsync();

				// ExecuteDelete goes straight to the database and does NOT tell the change tracker, so a node this
				// context still holds from an earlier import would collide on the identity map when the replacement
				// is added under the same (derived) id. Detach them.
				foreach (var entry in db.ChangeTracker.Entries<ConversationNode>().Where(x => replace.Contains(x.Entity.Id)).ToList())
					entry.State = EntityState.Detached;
			}

			db.ConversationNodes.AddRange(insert);
			await db.SaveChangesAsync();

			return new ConversationImportResult(insert.Count - replace.Count, replace.Count, kept);
		}

		// What an incremental conversation import did: rows newly added, rows replaced by a richer capture, and rows
		// left alone because what is stored is already at least as complete.
		public sealed record ConversationImportResult(int Inserted, int Replaced, int Kept);

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
		// `observedTypes` is the UNMERGED observation list - every creature seen, including ones whose placement lost
		// the position dedup. Types come from it, placements from `spawns` (#501). Passing null falls back to the
		// placements, which is what a caller with no wider view should do.
		public static async Task ImportNpcSpawns(DatabaseContext db, List<NpcSpawnEntry> spawns, List<NpcSpawnEntry>? observedTypes = null)
		{
			var typeByTemplate = await UpsertNpcTypes(db, observedTypes ?? spawns);

			foreach (var e in spawns)
			{
				// The lookup key is the placement's full identity, class included: on a class-specific map two classes'
				// NPCs share a spot, and matching on position alone would have each class's import overwrite the other's row.
				var existing = await db
					.NpcSpawns.Include(n => n.Morphs)
					.Include(n => n.Equipment)
					.FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z && n.ClassId == e.ClassId);

				// NpcTemplateId is required now (no phantom rows), so a spawn whose type doesn't resolve is dropped - the same
				// rule ImportNpcSpells applies to typeless casters, and consistent with the phantom-scrub invariant.
				if (e.TemplateId == 0 || !typeByTemplate.TryGetValue(e.TemplateId, out var npc))
					continue;

				// The id is DERIVED from the placement (see NpcSpawn/DerivedKey), so a re-harvest reproduces it rather
				// than renumbering the spawn set and every equipment/morph/patrol row hanging off it. An `existing`
				// row already carries it.
				var row = existing ?? new NpcSpawn
				{
					Id = NpcSpawn.ComputeId(e.MapId, e.X, e.Y, e.Z, e.ClassId),
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z,
					ClassId = e.ClassId
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
			var playerNames = (await db.Characters.Select(c => c.Name).ToListAsync()).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!.ToLowerInvariant()).ToHashSet();
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
					row.ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new();
					row.ObjectiveToken = g.Objective.Token;
					row.TrailingRefId = g.Trailing?.RefId ?? 0;
					row.TrailingCategory = g.Trailing?.Category ?? 0;
					row.TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new();
					row.TrailingFlags = g.Trailing?.Flags;
					row.TrailingToken = g.Trailing?.Token;
					row.TrailingLocalized = g.Trailing?.IsLocalized ?? false;
					refreshed++;
					continue;
				}

				if (mapId is { } m && maps.Add(m))
				{
					db.Maps.Add(new Map
					{
						Id = m,
						Name = ""
					});
				}

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
					ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new(),
					ObjectiveToken = g.Objective.Token,
					TrailingToken = g.Trailing?.Token,
					TrailingRefId = g.Trailing?.RefId ?? 0,
					TrailingCategory = g.Trailing?.Category ?? 0,
					TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new(),
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
			// Resolve every route to its spawn FIRST, keyed by spawn. Two captured routes can correlate to the same
			// spawn (they share a transform), and a per-entry loop cannot see what an earlier iteration already
			// queued - so the second route's nodes would collide with the first's on the primary key.
			//
			// The RICHEST route wins, not the last one. Last-wins made the shipped path depend on which capture the
			// corpus happened to iterate last: adding one gameplay capture of a map already covered replaced 36 nodes
			// worth of patrol with what that run happened to observe (-36 PatrolNodes / -29 waypoints for zero new
			// information). Two captures of one spawn are two observations of the same asset, so the one carrying more
			// of the route is the better record of it - the same rule ImportConversations applies when a later capture
			// is shorter ("replaced by a richer capture").
			var bySpawn = new Dictionary<uint, (NpcSpawn Spawn, MoveTaskNode Task)>();
			foreach (var e in paths)
			{
				// Class included: on a class-specific map (map, position) matches two rows, and a route would otherwise
				// be handed to whichever class the query happened to return first.
				var spawn = await db.NpcSpawns.FirstOrDefaultAsync(n =>
					n.MapId == e.MapId && n.PositionX == e.Position.X && n.PositionY == e.Position.Y && n.PositionZ == e.Position.Z && n.ClassId == e.ClassId);
				if (spawn == null)
				{
					// Say so rather than dropping it quietly: a route is only extracted for a spawn the same capture
					// saw, so a miss means the placement it belongs to did not survive into the burst-truth set, and
					// that NPC ships without its patrol.
					Console.WriteLine($"[npc-patrols] WARNING: route at map {e.MapId} ({e.Position.X}, {e.Position.Y}, {e.Position.Z}) matches no spawn - dropped.");
					continue;
				}

				if (bySpawn.TryGetValue(spawn.Id, out var kept) && RouteWeight(kept.Task).CompareTo(RouteWeight(e.Task)) >= 0)
					continue;

				bySpawn[spawn.Id] = (spawn, e.Task);
			}

			// Drop the persisted routes for those spawns BEFORE staging the replacements. The node key is derived
			// from the tree position now, so a re-import reuses the very same keys - queueing the deletes and the
			// inserts into one SaveChanges races them (`UNIQUE constraint failed: PatrolNodeWaypoints...`), the same
			// way it does for the conversation tree. ExecuteDelete runs immediately, inside the import transaction,
			// and cascades to the waypoints and words.
			var spawnIds = bySpawn.Keys.ToList();
			if (spawnIds.Count > 0)
				await db.PatrolNodes.Where(n => spawnIds.Contains(n.NpcSpawnId)).ExecuteDeleteAsync();

			// Build each spawn's tree from the WIRE AST here rather than reusing the extractor's instance: one entry's
			// task can be shared across spawns, and EF cannot track the same node object twice. Converting per spawn via
			// PatrolNodeMapper - the single wire->row authority - is what keeps this complete. The hand-written deep copy
			// this replaces silently dropped every column #410 added (PathTrailerWord, PathDirection1/2 and the seven
			// Target*/TargetRotation* slots), because a field-by-field clone has to be updated for each new column and
			// nothing forced it: the round-trip gate converts in memory and never reads a row back.
			foreach (var (spawn, task) in bySpawn.Values)
				AddNodeTree(spawn, PatrolNodeMapper.ToNode(task));

			await db.SaveChangesAsync();
		}

		// How much of a route an observation carries: total nodes, then total waypoints as the tiebreak. Used only to
		// pick between two captures of the SAME spawn - see ImportPatrolPaths.
		private static (int Nodes, int Waypoints) RouteWeight(MoveTaskNode task)
		{
			var nodes = 1;
			var waypoints = task.Waypoints.Count;
			foreach (var child in task.Children)
			{
				var (n, w) = RouteWeight(child);
				nodes += n;
				waypoints += w;
			}

			return (nodes, waypoints);
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
					AreaTitleKey = wire?.AreaTitleKey ?? new(),
					MapRefId = wire?.MapRefId ?? 0,
					MapCategory = wire?.MapCategory ?? 0,
					MapKey = wire?.MapKey ?? new(),
					RegionRefId = wire?.RegionRefId ?? 0,
					RegionCategory = wire?.RegionCategory ?? 0,
					RegionKey = wire?.RegionKey ?? new()
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

				// The area's ACGHash, off the captured journal (#544). Only overwrite when the harvest HAS one: the
				// quest index seeds it by hand for two quests, and an area no journal was captured for must not lose it.
				if (def.IndexMagic != 0)
					existing.IndexMagic = def.IndexMagic;
				db.QuestTextRefs.RemoveRange(existing.TextRefs);
				existing.TextRefs = def.TextRefs;

				// Merge the harvested PRIMARY goal by GoalId into the existing rows WITHOUT clobbering the server-side-only
				// fields (TargetName/GoalType/QuestItemId) a prior Seed* set - only the wire fields refresh.
				foreach (var hg in def.Goals)
				{
					var g = existing.Goals.FirstOrDefault(x => x.GoalId == hg.GoalId);
					if (g == null)
					{
						existing.Goals.Add(g = new QuestGoalDefinition
						{
							GoalId = hg.GoalId,
							Sequence = hg.Sequence,
							RequiredCount = hg.RequiredCount,
							Partial = hg.Partial
						});
					}
					else if (!hg.Partial)
					{
						// A goal the snapshot supplied in full stays full: Partial only ever CLEARS on an existing row,
						// so re-harvesting a quest whose secondary goal is now fully known upgrades it, and never the
						// other way round.
						g.Partial = false;
					}

					g.ObjectiveRefId = hg.ObjectiveRefId;
					g.ObjectiveCategory = hg.ObjectiveCategory;
					g.ObjectiveKey = hg.ObjectiveKey;

					// The reserved (marker, 4CC-tag) pairs, lifted off the full-update body by the codec (#537). The same
					// words the snapshot path harvests, so a goal seen by both gets the same value from either - and a
					// goal seen ONLY in a full-update stops carrying zeros. Refreshed like the other wire fields.
					g.Extra0 = hg.Extra0;
					g.Extra1 = hg.Extra1;
					g.Extra2 = hg.Extra2;
					g.Extra3 = hg.Extra3;
					g.Extra4 = hg.Extra4;
					g.Extra5 = hg.Extra5;
					g.Extra6 = hg.Extra6;

					// EVERY goal carries its own map pin (#540). "The marker block describes the primary goal only" was
					// an artefact of the old byte scan, which could see just the one block sitting before the first
					// objective record - so every secondary goal was persisted with no pin at all. A goal the wire pins
					// nowhere (quest 19314's twelve read 0/0/0) keeps whatever a snapshot already gave it.
					if (hg.MapId is > 0 || hg.MarkerX is > 0 || hg.MarkerY is > 0)
					{
						g.MapId = hg.MapId;
						g.MarkerX = hg.MarkerX;
						g.MarkerY = hg.MarkerY;
					}
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

		// Static world objects (decor/containers/zones), deduped by (map, position). Transient loot bags skipped.
		//
		// `overwrite` makes the table exactly what the captures produce: any row the harvest did NOT yield is deleted.
		// Upsert alone cannot do that, and a row-count delta of zero is what it reports either way - it re-writes the
		// live rows and silently leaves the dead ones - which is why the divergence went unseen (#481).
		//
		// ⚠️ IT IS NOT SAFE TO RUN ON THE SHIPPED faolan.db, and the reason is not about this code. Overwrite is only
		// a cleanup if the CURRENT corpus is a SUPERSET of the one that built the table. Ours is not: the corpus has
		// been curated down (111 captures today; the tickets and the extract manifest were written against 213/227),
		// so a row can be absent from the harvest simply because the capture that showed it is gone. Measured on the
		// shipped table, the 62 "orphans" are mostly real props - zone labels, climbing points, tagged static chests -
		// including `decor_chain_hangingwoman`, which carries EffectQuestId 12407 and is looked up BY NAME below to
		// seed the Casilda after-quest effect, and the Thirsty Dog Inn, the canonical same-playfield zone-line door.
		// Deleting those loses content nothing can rebuild. See #481 and the ORPHAN listing the mode prints.
		//
		// So: use it on a table built from the SAME corpus you are importing (a fresh DB, or after a full re-harvest),
		// and read the listing first anywhere else. The transient-loot-bag purge below stays, because it keys on what
		// a row IS rather than on whether a capture still exists.
		public static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects, bool overwrite = false)
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

			// Two Adds sharing an IDENTITY collide at SaveChanges - and the existing-row query below does NOT see
			// un-saved Adds, so two duplicate entries in THIS batch both fell through to Add and crashed a full import
			// (#394). Track the rows queued this batch and update the pending/existing one instead of adding a second.
			// The key is simply the row's DERIVED id, which IS the identity (map + position + display reference) - it
			// used to be a hand-built tuple mirroring the old unique index. Every row goes through this, INCLUDING one
			// whose display element did not lift: it was exempted on the reasoning that it has "no identity to dedup
			// on", which stopped being true when the derived id became the key - map + position identify it fine. The
			// exemption blind-Added it over its own stored row, so a re-import of the one such capture row (the map
			// 11040 "Siege Camp") failed with UNIQUE constraint failed: WorldObjects.Id.
			var pending = new Dictionary<uint, WorldObject>();
			foreach (var e in objects)
			{
				if (e.DisplayName == "Bag of loot")
					continue;

				// Populate FIRST: the display columns are DERIVED by TryLiftColumns (from the object message body) and
				// can differ from e.DisplayName, so the identity must be computed from the actual stored values.
				var built = PopulateObject(new WorldObject
				{
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z
				}, e);

				built.Id = built.ComputeId();
				var id = built.Id;

				if (pending.TryGetValue(id, out var queued))
				{
					PopulateObject(queued, e); // a duplicate within this batch: fold it into the row already queued
					continue;
				}

				{
					var existing = await db.WorldObjects.FirstOrDefaultAsync(o => o.Id == id);
					if (existing != null)
					{
						// Drop the stored word rows BEFORE re-populating. The lift rebuilds them from the captured body
						// (WorldObjectGeometryCodec.TryParse does row.Words.Clear() then re-adds), but `existing` is loaded
						// without its Words - lazy loading is off - so that Clear() empties an already-empty collection and
						// the re-added rows collide with the DB rows nobody loaded:
						//   UNIQUE constraint failed: WorldObjectWords.WorldObjectId, Kind, Ordinal
						// which is what a SECOND capture of the same object (an incremental import, #419) hits. Including
						// the navigation instead would only trade this for the same-key delete+insert race, since the
						// replacements reuse (WorldObjectId, Kind, Ordinal) - so delete immediately, as the conversation
						// and patrol trees do.
						await db.WorldObjectWords.Where(w => w.WorldObjectId == id).ExecuteDeleteAsync();
						PopulateObject(existing, e);
						pending[id] = existing;
						continue;
					}
				}

				pending[id] = built;
				db.WorldObjects.Add(built);
			}

			Console.WriteLine("World objects: identity columns + geometry tail lifted from the dump at import (#187).");

			if (overwrite)
			{
				// Delete by DERIVED id, not by a positional match: the id IS the identity (map + position + display
				// reference), so "the harvest did not yield this row" is exactly "no built row computed this id".
				// WorldObjectWords cascades, and the enrichment columns other modules own (DestinationMapId,
				// InteractAction, the quest gates) live on rows the harvest DOES yield, so they are untouched.
				var live = pending.Keys.ToHashSet();
				var orphans = await db.WorldObjects.Where(o => !live.Contains(o.Id)).ToListAsync();
				if (orphans.Count > 0)
				{
					// Print every row, with the columns that say whether it is residue or content. ⚠️ Read this list
					// before trusting the mode: an "orphan" is only junk if the CURRENT corpus is a superset of the one
					// that built the table. Ours is not - see the warning on the method - so most of these are real
					// props from captures that no longer exist, not stale rows.
					foreach (var o in orphans.OrderBy(o => o.MapId).ThenBy(o => o.Name))
						Console.WriteLine($"  ORPHAN map {o.MapId} subop 0x{o.SubOpcode:X8} act={o.InteractAction} dest={o.DestinationMapId?.ToString() ?? "-"} eq={o.EffectQuestId?.ToString() ?? "-"} gq={o.GateQuestId?.ToString() ?? "-"} tmpl={o.TemplateItemId?.ToString() ?? "-"} :: {o.Name ?? o.GeometryName ?? "?"}");

					var enriched = orphans.Count(o => o.InteractAction != InteractAction.None || o.DestinationMapId != null || o.EffectQuestId != null || o.GateQuestId != null);
					if (enriched > 0)
						Console.WriteLine($"World objects: ⚠️  {enriched} of these carry ENRICHMENT another module wrote (quest gate / teleport destination). Deleting them drops data no capture can rebuild.");

					db.WorldObjects.RemoveRange(orphans);
					Console.WriteLine($"World objects: removed {orphans.Count} orphaned row(s) (overwrite).");
				}
				else
				{
					Console.WriteLine("World objects: no orphaned rows - the table already matches the harvest.");
				}
			}

			await db.SaveChangesAsync();
		}

		public static NpcSpawn Populate(NpcSpawn row, NpcSpawnEntry e)
		{
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
			var placeBody = WorldObjectColumns.ExtractBody(e.Data);
			if (placeBody != null && SpawnBodyReader.TryReadCharacterMessage(placeBody, out var placeRead))
			{
				var core = NpcStatCoreColumns.Lift(placeRead.Message);
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
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;

			// Lift the identity columns + geometry tail from the dump body (the captured Data is no longer stored).
			WorldObjectColumns.TryLiftColumns(row, WorldObjectColumns.RecordOf(WorldObjectColumns.ExtractBody(e.Data)!));
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
				def.ObjectiveRegionVerified = captures.All(goals => QuestObjectiveRegionCodec
					.Build(QuestObjectiveRegions.ForQuest(def, goals[0].Instance))
					.SequenceEqual(QuestObjectiveRegionCodec.Build(new QuestObjectiveRegion
					{
						Goals = goals
					})));

				if (def.ObjectiveRegionVerified)
					verified++;
			}

			await db.SaveChangesAsync();
			return (verified, total);
		}

		// Overlay the hand-authored quest WIRING onto the harvested definitions - the fields captures don't carry: the
		// server trigger (GoalType/TargetName/QuestItemId via SeedGoal), the chain successor (NextQuestId via Chain), and
		// the byte-exact @609/snapshot detail the beach + Casilda quests need. Run as its OWN importer module
		// (QuestWiringModule) AFTER the `quests` module, so it applies last - the trigger fields are preserved by the
		// objective-goals refresh, and the @609-detail seeds are authoritative (they're calibrated to match the capture).
		// Trigger/chain seeds are BOOTSTRAP-ONLY (see SeedGoal/Chain), so a re-run never reverts a Quest-editor edit.
		public static async Task WireQuests(DatabaseContext db)
		{
			await TypeItemGoalsFromTrailingCategory(db);
			await SeedBeachQuestGoals(db);
			await SeedShipwreckedCityChain(db);
			await SeedCaptureReachQuests(db);
			await SeedCasildaChain(db);
			await SeedCasildaQuestSnapshot(db);
			await SeedQuestWorldEffects(db);
		}

		// A goal whose TRAILING REFERENCE is an ITEM is a gather objective, so type it `CollectItem` (#521). The goal
		// row already says so without any RDB or LDB lookup: `TrailingCategory` is a typed reference and 50200 is the
		// client's own `item.name` table (docs/rdb/ldb-string-index.md), so the ref IS an item id.
		//
		// This is a RULE, not a per-quest seed - which is the point. 72 of the 74 such goals sat at the fresh-harvest
		// `Kill` default with no target, i.e. inert: a fetch quest that can never complete, because the kill matcher
		// was looking for an NPC whose template id happens to equal an item id (it never does - NPC ids run 38..22,982
		// and these refs 110,891..4,675,657, so they were harmless as well as useless).
		//
		// ⚠️ It does NOT try to distinguish HOW the item is obtained. The client's data does not say, and the set mixes
		// mob drops (Mangy Yeti Pelt, Hyena Testicles), world pickups (Quarry Stonebrick) and bought/gathered
		// consumables (Pink Salt, Visionwine). It does not need to: a gather objective is satisfied by HOLDING the
		// item, so the runtime ticks these on the item reaching the inventory from any source
		// (QuestProgressionService.AdvanceCollectGoals off the single grant choke point). Guessing
		// LootFromNpc-vs-CollectItem per quest would buy nothing and a wrong guess makes the quest silently
		// un-completable.
		//
		// BOOTSTRAP-ONLY like every other wiring step: an already-wired goal (IsWired - anything but the bare Kill
		// default) is left exactly as it is, so a Quest-editor edit or a hand seed survives a re-import.
		private static async Task TypeItemGoalsFromTrailingCategory(DatabaseContext db)
		{
			const uint itemNameCategory = 50200; // LDB `item.name`

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == itemNameCategory && g.TrailingRefId > 0)
				.ToListAsync();

			var typed = 0;
			foreach (var goal in goals)
			{
				if (IsWired(goal))
					continue;

				// ⚠️ NOT every item goal is a collect - the goal's own `Separator` is the VERB (see QuestGoalKind), and
				// `Deliver` (20) hands the item OVER rather than acquiring it. Typing a hand-in as a collect would
				// complete the quest the moment the player obtains the item, skipping the delivery entirely - a silent
				// chain-progression bug, where leaving it unwired is at least visibly stuck. Those need the target NPC,
				// which the goal does not carry, so they stay for a capture or #520's objective text.
				if (goal.Separator == (uint)QuestGoalKind.Deliver)
					continue;

				goal.GoalType = QuestGoalType.CollectItem;
				typed++;
			}

			if (typed > 0)
				await db.SaveChangesAsync();
		}

		// Flattens the task tree into path.Nodes (every node gets PatrolPathId; the Parent/Children navs stay so EF
		// sets ParentId).
		// Flattens the task tree onto the spawn, stamping each node's POSITION as it goes: OrdinalPath is the chain
		// of sibling ordinals from the root ("" at the root, then "1", "1>0", ...) and ParentPath is its parent's.
		// That pair IS the node's key now (PatrolNode) - EF used to assign a surrogate and derive ParentId from the
		// Children nav, so nothing had to be computed here.
		private static void AddNodeTree(NpcSpawn spawn, PatrolNode node, string? parentPath = null)
		{
			node.ParentPath = parentPath;
			node.OrdinalPath = parentPath switch
			{
				null => "",
				"" => node.Ordinal.ToString(CultureInfo.InvariantCulture),
				_ => $"{parentPath}>{node.Ordinal.ToString(CultureInfo.InvariantCulture)}"
			};

			spawn.PatrolNodes.Add(node);
			foreach (var child in node.Children)
				AddNodeTree(spawn, child, node.OrdinalPath);
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
				KeyText = LdbKey.Decode(text.Key) ?? new(),
				Flags = text.Flags,
				IsLocalized = text.IsLocalized
			});
		}

		// Decodes the captured 54-byte MagicBuffIIR data region into the NpcSpell columns (#304).
		private static void ApplyEffect(NpcSpell s, byte[] payload)
		{
			if (payload is not { Length: MagicBuffDataCodec.Length })
				return;

			var fx = MagicBuffDataCodec.Parse(payload);
			s.Duration = NpcSpellEffectColumns.Duration(fx);
			s.DescriptorSlot3 = NpcSpellEffectColumns.DescriptorSlot3(fx);
			s.CastingLevel = NpcSpellEffectColumns.CastingLevel(fx);
			s.FlagA = NpcSpellEffectColumns.FlagA(fx);
			s.DescriptorSlot0 = NpcSpellEffectColumns.DescriptorSlot0(fx);
			s.DescriptorSlot8 = NpcSpellEffectColumns.DescriptorSlot8(fx);
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
				ShotSize = cols.Camera?.ShotSize,
				ShotType = cols.Camera?.ShotType,
				ShotEmphasis = cols.Camera?.ShotEmphasis,
				ShotFov = cols.Camera?.ShotFov,
				ShotTilt = cols.Camera?.ShotTilt,
				Autoface = cols.Camera?.Autoface,
				HeadPaddingY = cols.Camera?.HeadPaddingY
			};
			if (cols.Screens != null)
			{
				for (var i = 0; i < cols.Screens.Count; i++)
					// Throws if a screen isn't byte-exact round-trippable; stores it with the session slots (the capturing
					// player's name/guild, the retail server label) blanked - the runtime fills them from the live player.
					pkt.Screens.Add(ConversationScreenColumns.ToStoredRow(cols.Screens[i], i));
			}

			return pkt;
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

		// Every entity handle this session's server bound to an OWNER, i.e. every summoned creature - read off the
		// wire's own owner-binding (`PetIIR` mode 0 assign / mode 1 dismiss, #307) instead of a roster of pet names.
		//
		// Why not names: the name roster is the seeded `SpellEffect` SummonPet templates, so it only ever excludes
		// summons we already knew about, and it fails SILENTLY for any we don't. It did: the necromancer's
		// `Parasitic Soul` (Parasitic Soul Swarm) is not in the seed, so two captured sessions that had one out
		// planted it as permanent scenery on maps 2010 and 2070. Owner-binding is the general fact - a creature is a
		// pet exactly when the server said it belongs to somebody - and it covers summons of any class, named or not.
		//
		// Handles are per-session, so this set is only valid for the capture it came from.
		private static HashSet<uint> SummonedHandles(IReadOnlyList<CapturedPacket> packets)
		{
			var summoned = new HashSet<uint>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, SubOpcode: PetCodec.Opcode })
					continue;

				// Body slices the packet on every access, so take it once. The sweep runs over a mixed stream, so a
				// record whose grammar does not hold (a mode the client's own reader rejects) is skipped, not fatal.
				var body = p.Body;
				try
				{
					summoned.Add(PetCodec.Parse(body.AsSpan(4)).Pet);
				}
				catch (CodecParseException)
				{
					// not a well-formed owner binding
				}
			}

			return summoned;
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

		// A goal is "wired" once its server-trigger fields diverge from the fresh-harvest default (GoalType Kill, no
		// TargetName/QuestItemId) - i.e. a Seed* or the Quest editor has configured it. Bootstrap-only seeds skip a wired
		// goal so a re-import never reverts a hand edit.
		private static bool IsWired(QuestGoalDefinition goal)
		{
			return goal.GoalType != QuestGoalType.Kill || goal.TargetName != null || goal.QuestItemId != null;
		}

		// The beach starter (quest 10238) per-goal objective detail (the snapshot's @609 region). Idempotent.
		private static async Task SeedBeachQuestGoals(DatabaseContext db)
		{
			const uint beachQuestId = 10238;
			var quest = await db.QuestDefinitions.Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == beachQuestId);
			if (quest == null)
				return;

			quest.SnapshotUnknown118 = 4;
			quest.SnapshotUnknown14 = QuestFullUpdates.LiveUnknown14; // the beach record's `n` (@+0x14)
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
			goal1.ObjectiveKey = LdbKey.Decode("pARATX1!DX]&#92;So+0*A9B") ?? new();
			goal1.TrailingRefId = 180;
			goal1.TrailingCategory = 51200;
			goal1.TrailingKey = LdbKey.Decode("b2Y2],LfVfAVps*9JX*G") ?? new();
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
			goal2.ObjectiveKey = LdbKey.Decode("cA!(EbPpfzUK&#92;WPN@!H!") ?? new();
			goal2.TrailingRefId = 9500;
			goal2.TrailingCategory = 51000;
			goal2.TrailingKey = LdbKey.Decode("]+/sUK&#39;G3mO)clF`)9Jh") ?? new();
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

		// Other capture-harvested quests whose single objective is a LOCATION (TrailingCategory 100 + a map marker),
		// each granted by a SPAWNED NPC's dialogue (ConversationNodes.GrantQuestId). Wire the goal as ReachArea off its
		// captured marker so it completes on arrival - the most quest data we can source without per-quest capture
		// correlation (#358). Best-effort ("as much as possible, not 100%"): cat-100 = a location objective (proven for
		// Shipwrecked III "enter city" / VIII Nadini), but the exact retail purpose (reach vs talk-at-location) isn't
		// pinned; reaching the marker is a reasonable stand-in. All 7 granting NPCs are spawned and all target maps
		// exist. Server-trigger field only (not byte-exact); bootstrap-only via SeedGoal.
		private static async Task SeedCaptureReachQuests(DatabaseContext db)
		{
			await SeedGoal(db, 13041, 4833, QuestGoalType.ReachArea); // giver Alyssa        -> reach @4020 (959,723)
			await SeedGoal(db, 13986, 6459, QuestGoalType.ReachArea); // giver Tanner Chael  -> reach @1000 (283,659)
			await SeedGoal(db, 14092, 6597, QuestGoalType.ReachArea); // giver Borcha        -> reach @1010 (88,205)
			await SeedGoal(db, 14617, 7389, QuestGoalType.ReachArea); // giver Cadarn        -> reach @3000 (992,1134)
			await SeedGoal(db, 14622, 7394, QuestGoalType.ReachArea); // giver Serving Wench -> reach @2000 (826,533)
			await SeedGoal(db, 14627, 7399, QuestGoalType.ReachArea); // giver Caor          -> reach @1000 (403,445)
			await SeedGoal(db, 19522, 14328, QuestGoalType.ReachArea); // giver Hadrathus     -> reach @30200 (627,868)
		}

		// The Tortage intro quest CHAIN (#261): 13479 is a four-stage supersession chain. Idempotent.
		private static async Task SeedCasildaChain(DatabaseContext db)
		{
			await Chain(db, 13479, 13563);
			await SeedGoal(db, 13479, 5744, QuestGoalType.Kill, "Scavenger Tuut", required: 1);
			// The loot stage's key item: Scavenger Tuut's bag drops 319224 "Scavenger's Key" (#261), so the player sees
			// and takes the actual key, not just an empty source-matched bag.
			await Chain(db, 13563, 13480);
			await SeedGoal(db, 13563, 5867, QuestGoalType.LootFromNpc, "Scavenger Tuut", 319224, 1);
			await Chain(db, 13480, 12407);
			await SeedGoal(db, 13480, 5745, QuestGoalType.TalkToNpc, "Casilda", required: 1);
			await Chain(db, 12407, null); // escort is the chain end
			await SeedGoal(db, 12407, 3544, QuestGoalType.Escort, "Casilda", required: 1);

			// The escortee's TEMPLATE id (Casilda 9411) as the Escort goal's TrailingRefId, so the runtime follow trigger
			// resolves her by ID not name (#262). Set AUTHORITATIVELY every run (not bootstrap-only): the capture's escort
			// @609 carries no trailing ref and this module runs after the objective-goals refresh, so it must win. Byte-
			// safe - 12407's objective region is not in the tested set.
			var escortGoal = await db.QuestGoalDefinitions.FirstOrDefaultAsync(g => g.QuestDefinitionId == 12407 && g.GoalId == 3544);
			if (escortGoal != null)
			{
				escortGoal.TrailingRefId = 9411;
				await db.SaveChangesAsync();
			}
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
			quest.SnapshotUnknown14 = QuestFullUpdates.LiveUnknown14;
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
				goal.ObjectiveKey = quest.TextRefs.FirstOrDefault(t => t.Slot == QuestTextSlot.Objective)?.KeyText ?? new();
				goal.TrailingRefId = 11561;
				goal.TrailingCategory = 51000;
				goal.TrailingKey = quest.TextRefs.FirstOrDefault(t => t.Slot == QuestTextSlot.ObjectiveLocalized)?.KeyText ?? new();
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
		private static async Task SeedGoal(DatabaseContext db, uint questId, uint goalId, QuestGoalType type, string? target = null, uint? questItemId = null, uint? required = null)
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

		// After-quest world-object EFFECTS (#262): tag decor with the quest whose state changes its dynel look
		// (WorldObject.EffectQuestId/EffectDynelState, applied by WorldBroadcaster.ApplyQuestWorldEffects). The Tortage
		// beach "invisible wall" = the Casilda chain decor's collision, dropped (SimpleDynel visual-change state 5) once
		// the escort quest (12407) is active - data now, replacing the hard-coded DropBeachWall lookup. Idempotent.
		private static async Task SeedQuestWorldEffects(DatabaseContext db)
		{
			var chain = await db.WorldObjects.FirstOrDefaultAsync(o => o.Name != null && o.Name.Contains("decor_chain_hangingwoman"));
			if (chain == null)
				return;

			chain.EffectQuestId = 12407;
			chain.EffectDynelState = 5;
			await db.SaveChangesAsync();
			Console.WriteLine("Seeded after-quest world effect: beach chain-wall (decor_chain_hangingwoman) -> drop on quest 12407 (state 5).");
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
			await SeedGoal(db, 12369, 3492, QuestGoalType.LootFromNpc, "Saddur", 193186);
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
			await SeedGoal(db, 10320, 436, QuestGoalType.TalkToNpc, "Turach"); // IV The Blacksmith - talk Turach (pre-quarry)
			await SeedGoal(db, 10286, 399, QuestGoalType.TalkToNpc, "Laranga"); // VI The Guard - talk the city guard
			await SeedGoal(db, 10287, 400, QuestGoalType.TalkToNpc, "Tina"); // VII The Barmaid - talk Tina

			// VIII "Nadini" (10915, granted by Tina): the wiki objective is "Talk to Nadini", but Nadini 806 has NO
			// conversation imported, so her greeting can't fire the talk trigger (PickConversationScreen returns null).
			// Her spawn (1026,670 @4022 night city) EXACTLY matches the goal marker, so wire it as ReachArea (reach her)
			// - the robust stand-in until her dialogue is imported. Chain end (Shipwrecked -> "The Awakening I" is a
			// separate destiny quest, out of scope; Nadini can't grant it without dialogue).
			await SeedGoal(db, 10915, 1352, QuestGoalType.ReachArea);
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

				var b = WorldObjectColumns.ExtractBody(e.Data);
				if (b != null && NpcSpawnAppearance.TryReadNoMorphWord2(b, out var rc))
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
					var body = WorldObjectColumns.ExtractBody(e.Data);
					if (body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					{
						npc.StatCore ??= NpcStatCoreColumns.Lift(read.Message);
						npc.StatCore.IirHeader ??= NpcStatCoreColumns.ReadIirHeaderWord(body);
					}
				}

				// #243: stamp the type's fresh minimal post-IIR recordCount from the dump-derived scan above.
				if (npc.StatCore != null && minimalRcByTemplate.TryGetValue(e.TemplateId, out var minRc))
					npc.StatCore.MinimalPostIirWord2 = minRc;
			}

			await db.SaveChangesAsync();
			return byTemplate;
		}
	}
}
