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
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
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
		private static readonly Dictionary<string, Quaternion> UniqueNpcFacings = new Dictionary<string, Quaternion>
		{
			["Casilda"] = new Quaternion(0f, 0.8661140203475952f, 0f, -0.4998469948768616f)
		};

		// Unique quest-NPC home positions (they move during their quest, so dedup must not strand them). #266
		private static readonly Dictionary<string, Vector3> UniqueNpcHomes = new Dictionary<string, Vector3>
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

		// #565: the widest spawn-to-route gap a HANDLE-joined route may have before it is read as a mis-join rather than
		// a route. The handle join is identity evidence and is deliberately far looser than RouteMatchRadius above - an
		// NPC may well have drifted from its recorded spawn point by the time its MoveTask was issued, and that is a
		// route, not an error. What it is guarding against is handle RECYCLING: a handle freed by a despawn is reissued
		// inside the same capture, so a route observed for the first NPC can land on the second one's spawn. That failure
		// is gross, never marginal - the audit found 30 of 900 routes whose NEAREST waypoint is already beyond this, six
		// of them compact 16-109u loops sitting 480-640u from the spawn they were joined to (Ymirish Frostshield,
		// Vanir Champion Elite x2, ...), which no NPC could be walking. Against genuine matches there is a wide margin:
		// spawn-to-nearest-waypoint is 7.8u at p95 and 23.1u at p95 over ALL routes, so 40 sits ~5x the genuine spread.
		// A route rejected here still gets the positional backfill below, which is the correct owner-finding path.
		private const float MaxHandleRouteGap = 40f;

		// `classSpecificMaps` = the maps whose population is per class (Map.ClassSpecific - the city armories). A spawn
		// harvested on one of them is evidence about ONE class's copy of the playfield, so it is stamped with the class
		// the capture was played on and elected only against captures of that same class.
		public static (List<NpcSpawnEntry> Spawns, List<PatrolPathEntry> Patrols, List<NpcSpawnEntry> ObservedTypes) BuildBurstTruthSpawns(IReadOnlyList<NamedCapture> captures, IReadOnlySet<string> petNames,
			IReadOnlySet<uint> classSpecificMaps, IReadOnlySet<uint> triggerSpawnedTypes)
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

			// ⚠️ ...and how many DISTINCT NPCs each template stands for, because the merge above is only identity-
			// preserving when the answer is one (#836). Casilda is a unique creature, so "the richest kit seen on
			// template 9411" IS her own richest kit. A Poacher is not: template 8848 stands for many individuals, and
			// retail dresses them INDIVIDUALLY - the three captured beach Poachers wear three different kits (7, 6 and
			// 6 pieces, differing in chest, weapon, boots and whether they carry a slot-2 piece at all), sharing only
			// their leather legs. Applying the richest kit to all of them handed every Poacher in the world the same
			// loadout and erased that, which is what "they all look the same" turned out to be.
			//
			// A placement is (map, position): a handle is session-volatile, and the same NPC seen in two captures is
			// the one standing in the same spot.
			var placementsPerTemplate = perCap.SelectMany(p => p.All)
				.GroupBy(e => e.TemplateId)
				.ToDictionary(g => g.Key, g => g.Select(e => (e.MapId, X: MathF.Round(e.X), Z: MathF.Round(e.Z))).Distinct().Count());

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
				// (map, handle), matching DistinctByHandle - a handle alone is not an identity across a zone change (#571).
				var byHandle = cap.All.ToDictionary(e => (e.MapId, e.Handle), e => e);
				foreach (var (key, route) in cap.Routes)
				{
					if (route.Waypoints.Count == 0 || !byHandle.TryGetValue(key, out var spawn))
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
			//
			// TRIGGER-SPAWNED types go the same way, for the same reason one level over (#528). `triggerSpawnedTypes`
			// is every creature the world puts in play through a SCRIPT rather than a placement:
			//   * a `NpcAmbush` ambusher  - a quest goal advancing spawns it (the Quarry Bat Demon)
			//   * a `NpcEncounter` BOSS   - a proximity lure swaps into it (Eerika of the Maidens / Boros)
			// A capture that caught one caught the trigger FIRING, so harvesting it as a placement is the summoned-pet
			// failure exactly: it left a Bat Demon standing in the Tortage quarry permanently, for every player,
			// regardless of quest state. By TEMPLATE rather than by name, because unlike the pet roster these are real
			// tables with real ids.
			//
			// ⚠️ The encounter boss half is a RULE now, where it used to be an accident. She got no placement only
			// because she spawns on the exact spot her lure vacated and the position dedup below dropped her (#501) -
			// which would have stopped protecting her the moment a capture caught the swap a few units off.
			var placements = spawns
				.Where(e => !petNames.Contains((e.Name ?? string.Empty).ToLowerInvariant()))
				.Where(e => !triggerSpawnedTypes.Contains(e.TemplateId))
				.ToList();

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

			void Take(List<NpcSpawnEntry> chosen, Dictionary<(uint MapId, uint Handle), PatrolPathEntry> routes)
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

					// The richest-kit merge only where the template IS one NPC (#836); everywhere else a placement keeps
					// what IT was seen wearing, and the template set is a fallback for an observation that carried no
					// equipment at all - the same rule the morph backfill above uses, and for the same reason.
					var oneNpcOfThisType = placementsPerTemplate.GetValueOrDefault(e.TemplateId) <= 1;
					if (equipByTemplate.TryGetValue(e.TemplateId, out var fullEquip) && (oneNpcOfThisType
						    ? fullEquip.Count > e.Equipment.Count
						    : e.Equipment.Count == 0))
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
					// The handle join is sanity-checked positionally (MaxHandleRouteGap) so a RECYCLED handle cannot hand
					// this spawn a route belonging to a different NPC on the far side of the zone; a rejected route falls
					// through to the positional backfill, which finds the spawn actually standing on it.
					if (routes.TryGetValue((e.MapId, e.Handle), out var r) && r.Waypoints.Count > 0
					    && r.Waypoints.Min(w => Vector3.Distance(w, new Vector3(e.X, e.Y, e.Z))) <= MaxHandleRouteGap)
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
			var areas = await db.QuestLines.Select(a => a.QuestLineId).ToHashSetAsync();
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

				if (q.QuestLineId != 0 && areas.Add(q.QuestLineId))
				{
					db.QuestLines.Add(new QuestLine
					{
						QuestLineId = q.QuestLineId
					});
				}

				if (!existing.TryGetValue(q.QuestId, out var def))
				{
					def = new QuestDefinition
					{
						QuestId = q.QuestId,
						QuestLineId = q.QuestLineId,
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

				if (harvest.Areas.TryGetValue(q.QuestLineId, out var header))
				{
					AddRefIfMissing(def, QuestTextSlot.QuestLineTitle, header.Title);
					AddRefIfMissing(def, QuestTextSlot.RegionName, header.Region);
					AddRefIfMissing(def, QuestTextSlot.MapName, header.Map);
				}
			}

			await db.SaveChangesAsync();
			return (added, filled);
		}

		// Rebuilds the dialogue GRAPH from the whole corpus (#642): the harvested walks are columnized here, folded into
		// content-addressed nodes + explicit edges by ConversationGraph, and written whole.
		//
		// ⚠️ WHOLE-CORPUS ONLY, which the answer-path tree was not (#419 supported an upsert that merged one new dump
		// into what was stored). The fold is a global partition over every walk - which screens merge, and which need a
		// Discriminator, is decided by comparing them against each other - so folding one capture against a database
		// that no longer remembers the walks it came from cannot reproduce the same classification. The module
		// therefore advertises Overwrite only, and the merge rule the upsert existed for ("the most complete capture of
		// a screen wins") is applied inside the fold instead, across all captures at once.
		//
		// A row marked Verified is hand-reviewed and survives untouched unless `force`. That is what makes "import the
		// bulk, correct the tail by hand" a workflow rather than a treadmill. Edges and entries always rebuild: they
		// are wiring between screens, not content, and half a rebuilt graph is not a graph.
		public static async Task<ConversationImportResult> ImportConversations(DatabaseContext db, List<ConversationWalk> conversations, bool force = false)
		{
			ArgumentNullException.ThrowIfNull(conversations);

			var verified = force
				? []
				: (await db.ConversationNodes.Where(n => n.Verified).Select(n => n.Id).ToListAsync()).ToHashSet();

			// A conversation NPC's template must exist to be the FK target (#244), and since #646 it is also the
			// screen's IDENTITY - so a walk whose template is not imported is DROPPED rather than downgraded to a
			// name-only row. The world is pruned, so this can legitimately happen; the count is reported.
			var knownTemplates = (await db.NpcTemplates.Select(t => t.TemplateId).ToListAsync()).ToHashSet();
			var knownQuests = (await db.QuestDefinitions.Select(q => q.QuestId).ToListAsync()).ToHashSet();
			var knownObjects = (await db.WorldObjects.Select(o => o.Id).ToListAsync()).ToHashSet();

			// The NAME is now only a LABEL (#646), so it is stored as harvested - empty where the capture never named
			// the NPC - instead of being given the "#<handle>" stand-in identity it needed while it was the key. What
			// must be present is the TEMPLATE: a walk pointing at one we did not import cannot be a row, so it is
			// filtered out here and the fold reports the rest (a walk with NO template at all).
			var unknownTemplate = conversations.Count(e => e.NpcTemplateId is { } t && !knownTemplates.Contains(t));
			var harvested = conversations
				.Where(e => e.NpcTemplateId is not { } t || knownTemplates.Contains(t))
				.Select(e => new ConversationGraph.Harvested(e, [.. e.Packets.Select(BuildPacket)]))
				.ToList();

			var graph = ConversationGraph.Fold(harvested);
			if (unknownTemplate > 0)
				graph.Report.Add($"{unknownTemplate} walk(s) name an NPC template that is not imported - dropped (the world is pruned)");

			// The ids are DERIVED from each row's identity, so a re-harvest of the same corpus reproduces them exactly
			// and faolan.sql does not churn. Two rows hashing to one id is either the same screen twice (the fold
			// should already have merged it) or a genuine hash collision - both are import bugs, so name the pair and
			// fail rather than let one silently overwrite the other.
			var byId = new Dictionary<uint, string>();
			foreach (var n in graph.Nodes)
			{
				if (!byId.TryAdd(n.Id, $"{n.NpcName} ({n.NpcTemplateId}) {n.ContentKey}#{n.Discriminator}"))
					throw new InvalidOperationException($"Conversation node id 0x{n.Id:X8} is not unique: '{n.NpcName} ({n.NpcTemplateId}) {n.ContentKey}' collides with '{byId[n.Id]}'.");
			}

			// ⚠️ TravelDestinations FK a node and DO cascade, so the delete below takes them with it - and the nodes
			// then come straight back under the SAME derived ids, so nothing about the trip has actually stopped being
			// true. Snapshot them here and put them back after the rebuild (#763).
			//
			// This matters because the two halves of that table have different sources. `travel-wiring` re-harvests
			// the CAPTURED trips right after this, so those survived by luck; the `{teleportplayer}` directive trips
			// come from `rdb-dialogue-teleports`, which is OPT-IN and needs an RdbDir - so a default
			// `--skip characters` run destroyed four rows nothing in that run could rebuild, and the loss was silent.
			var travelBefore = await db.TravelDestinations.AsNoTracking().ToListAsync();

			// The rebuild drops the WIRING before the screens: an edge names two nodes, and the model deliberately does
			// not cascade its inbound end (see DatabaseContext), so deleting a node underneath a live edge is a foreign
			// key violation rather than a silent orphan.
			await db.ConversationEntries.ExecuteDeleteAsync();
			await db.ConversationEdges.ExecuteDeleteAsync();
			await db.ConversationNodes.Where(n => force || !n.Verified).ExecuteDeleteAsync();

			// ExecuteDelete goes straight to the database and does NOT tell the change tracker, so anything this
			// context already holds would collide on the identity map when the same (derived) ids are re-added.
			db.ChangeTracker.Clear();

			var inserted = 0;
			foreach (var n in graph.Nodes)
			{
				if (verified.Contains(n.Id))
					continue; // hand-reviewed: the stored screen IS the answer

				db.ConversationNodes.Add(new ConversationNode
				{
					Id = n.Id,
					NpcName = n.NpcName,
					ContentKey = n.ContentKey,
					Discriminator = n.Discriminator,
					NpcTemplateId = n.NpcTemplateId,
					Packets = n.Packets
				});
				inserted++;
			}

			await db.SaveChangesAsync();

			// The screens have to exist before anything can point at them, so the wiring is a second pass.
			var nodeIds = graph.Nodes.Select(n => n.Id).Concat(verified).ToHashSet();
			var edges = 0;
			foreach (var e in graph.Edges)
			{
				// A branch into or out of a screen that did not survive the fold is dangling wiring; dropping it is
				// what keeps the FK honest, and the count is reported rather than swallowed.
				if (!nodeIds.Contains(e.FromNodeId) || !nodeIds.Contains(e.ToNodeId))
					continue;

				db.ConversationEdges.Add(new ConversationEdge
				{
					Id = ConversationEdge.ComputeId(e.FromNodeId, e.OptionId, e.ConditionQuestId, e.ConditionQuestState),
					FromNodeId = e.FromNodeId,
					OptionId = e.OptionId,
					ToNodeId = e.ToNodeId,
					ConditionQuestId = e.ConditionQuestId,
					ConditionQuestState = e.ConditionQuestState,
					GrantQuestId = e.GrantQuestId is { } q && knownQuests.Contains(q)
						? q
						: null
				});
				edges++;
			}

			var entries = 0;
			foreach (var e in graph.Entries)
			{
				if (!nodeIds.Contains(e.NodeId))
					continue;

				db.ConversationEntries.Add(new ConversationEntry
				{
					Id = ConversationEntry.ComputeId(e.NpcTemplateId, e.Trigger, e.TriggerWorldObjectId, e.ConditionQuestId, e.ConditionQuestState, e.RequiresRepeatGreeting),
					NpcName = e.NpcName,
					NpcTemplateId = e.NpcTemplateId,
					Trigger = e.Trigger,
					TriggerWorldObjectId = e.TriggerWorldObjectId is { } w && knownObjects.Contains(w)
						? w
						: null,
					ConditionQuestId = e.ConditionQuestId,
					ConditionQuestState = e.ConditionQuestState,
					RepeatGreeting = e.RequiresRepeatGreeting,
					NodeId = e.NodeId
				});
				entries++;
			}

			await db.SaveChangesAsync();

			// Put the travel rows back on the screens that came back (#763). Keyed by the node id, which is DERIVED
			// from the screen's content - so a screen that still exists is the same screen, and the trip it sold is
			// still the trip it sells. A row whose screen did NOT survive the fold is genuinely dangling and is
			// dropped, counted, not silently swallowed.
			var travelRestored = 0;
			foreach (var t in travelBefore)
			{
				if (!nodeIds.Contains(t.FromNodeId))
					continue;

				db.TravelDestinations.Add(t);
				travelRestored++;
			}

			if (travelRestored > 0)
				await db.SaveChangesAsync();

			var travelLost = travelBefore.Count - travelRestored;
			if (travelLost > 0)
				graph.Report.Add($"{travelLost} travel destination(s) sat on a screen that did not survive the fold - dropped");

			var dropped = graph.Edges.Count - edges + (graph.Entries.Count - entries);
			if (dropped > 0)
				graph.Report.Add($"{dropped} edge(s)/entrie(s) point at a screen that did not survive the fold - dropped");

			return new ConversationImportResult(inserted, edges, entries, verified.Count, graph.Report);
		}

		// What a conversation rebuild produced: the screens, the branches between them, the roots that open them, and
		// the rows skipped as hand-verified. `Report` carries what the fold could not do cleanly - a content group it
		// had to discriminate, a branch whose option list was never captured - because a silent fold is exactly the
		// thing you would otherwise spend an afternoon on.
		public sealed record ConversationImportResult(int Nodes, int Edges, int Entries, int Protected, List<string> Report);

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
		// (types were upserted by ImportNpcSpawns just before) and only stored as the NpcTemplateId FK - a cast whose caster
		// has no captured NpcTemplate type (off-beach named NPCs) is DROPPED, since the loader reads spells via the FK
		// (NpcTemplate.Spells).
		//
		// ⚠️ The `playerNames` check is a BELT, not the braces (#616). It only knows characters in OUR database, so it
		// never sees a stranger who happened to stand near the capturing client on the live retail server. What keeps
		// those out is structural and upstream: their spawns carry player_stats, so NpcSpawnExtractor never gives them
		// a handle -> name entry for NpcSpellExtractor to join on (measured: all 317 player-backed spawns in the
		// corpus are excluded, NpcSpellExtractorPlayerFilterTests). Keep both - this one also catches the capture's
		// OWN player once imported, whose spawn is dropped by handle rather than by message.
		// The harvested pick-one-of-N reward lists (#815). Replaces a quest's set wholesale rather than merging into it:
		// the offer is one list, so a stale position left behind by a shorter re-harvest would be a reward the client
		// can select and the server cannot explain.
		//
		// ⚠️ A row whose quest or item we do not hold is DROPPED with a count, not inserted. Both are hard FKs and a
		// violation would abort the whole import over one un-harvested template, which is the wrong trade for a
		// capture-sourced table - the count is what keeps the drop from being silent.
		public static async Task<(int Written, int SkippedQuest, int SkippedItem)> ImportQuestRewardOptions(DatabaseContext db, List<QuestRewardOffer> offers)
		{
			ArgumentNullException.ThrowIfNull(offers);

			var knownQuests = (await db.QuestDefinitions.Select(q => q.QuestId).ToListAsync()).ToHashSet();
			var knownItems = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();

			int written = 0, skippedQuest = 0, skippedItem = 0;
			foreach (var quest in offers.GroupBy(o => o.QuestId))
			{
				if (!knownQuests.Contains(quest.Key))
				{
					skippedQuest += quest.Count();
					continue;
				}

				var usable = quest.Where(o => knownItems.Contains(o.ItemTemplateId)).OrderBy(o => o.Pos).ToList();
				skippedItem += quest.Count() - usable.Count;
				if (usable.Count == 0)
					continue;

				await db.QuestRewardOptions.Where(o => o.QuestId == quest.Key).ExecuteDeleteAsync();
				foreach (var o in usable)
				{
					db.QuestRewardOptions.Add(new QuestRewardOption
					{
						QuestId = o.QuestId,
						Pos = o.Pos,
						ItemTemplateId = o.ItemTemplateId,
						Level = o.Level
					});
					written++;
				}
			}

			await db.SaveChangesAsync();
			return (written, skippedQuest, skippedItem);
		}

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
					// reserved[0] is NOT harvested: it is the captured character's SolvedTimes, not quest data (#550).
					row.Extra1 = g.Extra1;
					row.Extra2 = g.Extra2;
					row.Extra3 = g.Extra3;
					row.Extra4 = g.Extra4;
					row.Extra5 = g.Extra5;
					row.Extra6 = g.Extra6;
					row.ObjectiveRefId = g.Objective.RefId;
					row.ObjectiveCategory = g.Objective.Category;
					row.ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new ProtoDecryptionKey();
					row.ObjectiveToken = g.Objective.Token;
					row.TrailingRefId = g.Trailing?.RefId ?? 0;
					row.TrailingCategory = g.Trailing?.Category ?? 0;
					row.TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new ProtoDecryptionKey();
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
					// reserved[0] is NOT harvested - see the refresh path above (#550).
					Extra1 = g.Extra1,
					Extra2 = g.Extra2,
					Extra3 = g.Extra3,
					Extra4 = g.Extra4,
					Extra5 = g.Extra5,
					Extra6 = g.Extra6,
					RequiredCount = g.RequiredCount,
					ObjectiveRefId = g.Objective.RefId,
					ObjectiveCategory = g.Objective.Category,
					ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new ProtoDecryptionKey(),
					ObjectiveToken = g.Objective.Token,
					TrailingToken = g.Trailing?.Token,
					TrailingRefId = g.Trailing?.RefId ?? 0,
					TrailingCategory = g.Trailing?.Category ?? 0,
					TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new ProtoDecryptionKey(),
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

			// #713: the beat identifies the PLACEMENT, so stamp it now that every route is attached. Rows that come out
			// with the same key are sightings of ONE patroller in different capture sessions - this extraction keys
			// spawns on MapId + exact XYZ, and a patroller stands somewhere different every time it is seen, so each
			// sighting minted a placement (231 of 857 route-carrying spawns repo-wide are redundant that way).
			//
			// Nothing is deleted: the redundant rows are the placement's VARIANT POOL. What differs between them is
			// exactly what retail re-rolls per spawn - level, morphs and all 7 equipment slots - so the world draws one
			// at spawn rather than standing them all up at once.
			foreach (var (spawn, _) in bySpawn.Values)
				spawn.PlacementId = PatrolRoute.PlacementKey(spawn.PatrolNodes, spawn.NpcTemplateId);

			await db.SaveChangesAsync();

			var pools = bySpawn.Values.Select(v => v.Spawn.PlacementId).Where(p => p != null).Distinct().Count();
			Console.WriteLine($"Patrol placements: {bySpawn.Count} route-carrying spawn(s) -> {pools} placement(s) (#713).");
		}

		// #718/#765: stamps `PlacementId` on the STATIONARY spawn clusters the corpus proves are one placement seen
		// twice - the case #713's route key cannot reach, because these NPCs have no beat.
		//
		// The decision is `StationaryPlacementResolver`'s and the reasoning lives there; the short version is that the
		// test is a HANDLE, not a distance. Retail genuinely places same-name NPCs a metre apart (181 of 191 candidate
		// clusters are two real placements), so a tolerance would collapse the world's guard pairs; but one handle
		// sighted at two of a cluster's positions WALKED between them, and that is one entity.
		//
		// Nothing is deleted, exactly as on the patrol side: the redundant rows are the placement's VARIANT POOL, and
		// the world stands one entity up drawing its level/morphs/equipment from the group.
		//
		// Returns how many rows were pooled (cluster size minus one, summed).
		public static async Task<int> ImportStationaryPlacements(DatabaseContext db, IReadOnlyList<NamedCapture> captures)
		{
			var routed = await db.PatrolNodes.Select(p => p.NpcSpawnId).Distinct().ToHashSetAsync();
			var rows = await db.NpcSpawns.ToListAsync();

			var pooled = 0;
			var clusters = 0;
			foreach (var cluster in StationaryPlacementResolver.Resolve(rows, routed, captures)
				         .Where(c => c.Verdict == StationaryPlacementResolver.Verdict.OneWalked))
			{
				var key = cluster.PlacementKey();
				foreach (var row in cluster.Rows)
					row.PlacementId = key;

				clusters++;
				pooled += cluster.Rows.Count - 1;
			}

			await db.SaveChangesAsync();
			Console.WriteLine($"Stationary placements: {clusters} cluster(s) proved to be one NPC seen twice -> {pooled} row(s) pooled (#718).");
			return pooled;
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
		// the completed-quest journal exposes (#390); `referenced` is every QuestLineId a quest definition points at.
		//
		// QuestDefinition.QuestLineId is a real FK, so a row must exist for EVERY referenced area before definitions are
		// saved - the captures only show areas a character actually completed a quest in, which is far short of the
		// areas quests live in. Anything unharvested gets a placeholder (Magic 0), exactly as Maps do; a missing row
		// would otherwise fail the FK check on rebuild.
		public static async Task<int> ImportQuestLines(DatabaseContext db, IReadOnlyList<QuestLine> harvested, IReadOnlyCollection<uint> referenced)
		{
			var existing = await db.QuestLines.ToDictionaryAsync(a => a.QuestLineId);
			var byArea = harvested.ToDictionary(a => a.QuestLineId);
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
					row.TitleRefId = wire.TitleRefId;
					row.TitleCategory = wire.TitleCategory;
					row.TitleKey = wire.TitleKey;
					row.MapRefId = wire.MapRefId;
					row.MapCategory = wire.MapCategory;
					row.MapKey = wire.MapKey;
					row.RegionRefId = wire.RegionRefId;
					row.RegionCategory = wire.RegionCategory;
					row.RegionKey = wire.RegionKey;
					continue;
				}

				db.QuestLines.Add(new QuestLine
				{
					QuestLineId = areaId,
					Magic = wire?.Magic ?? 0,
					TableField1 = wire?.TableField1 ?? 0,
					TitleRefId = wire?.TitleRefId ?? 0,
					TitleCategory = wire?.TitleCategory ?? 0,
					TitleKey = wire?.TitleKey ?? new ProtoDecryptionKey(),
					MapRefId = wire?.MapRefId ?? 0,
					MapCategory = wire?.MapCategory ?? 0,
					MapKey = wire?.MapKey ?? new ProtoDecryptionKey(),
					RegionRefId = wire?.RegionRefId ?? 0,
					RegionCategory = wire?.RegionCategory ?? 0,
					RegionKey = wire?.RegionKey ?? new ProtoDecryptionKey()
				});
				added++;
			}

			await db.SaveChangesAsync();
			return added;
		}

		public static async Task ImportQuestDefinitions(DatabaseContext db, List<QuestDefinition> definitions, bool force = false)
		{
			// QuestDefinition.MapId is an FK to Maps, but a harvested quest can point its map marker at a playfield the
			// Maps catalog doesn't have - the catalog is built from captured MapChange packets, so a map nobody visited
			// in a capture is simply absent (e.g. 1000004, referenced by four Quest_c journal records). These used to be
			// SKIPPED, which threw away real journal content to satisfy an FK. A placeholder Map row carries the id and
			// nothing else; the maps module fills in the name if that playfield is ever captured.
			//
			// A NULL MapId needs none of this: it means the quest has no map marker at all, which is every quest that
			// comes from the client's catalog rather than a capture.
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var placeholderMaps = 0;
			foreach (var mapId in definitions.Select(d => d.MapId).OfType<uint>().Distinct().Where(id => !knownMaps.Contains(id)))
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
			var protectedRows = 0;

			var added = 0;
			var updated = 0;
			foreach (var def in definitions)
			{
				// Unreachable in practice now that every referenced map gets a placeholder above and a mapless quest is
				// NULL rather than 0 - kept as a guard so a future caller that hands us an unplaceholdered map fails
				// loudly on one row instead of aborting the whole import on the FK.
				if (def.MapId is { } defMapId && !knownMaps.Contains(defMapId))
				{
					Console.WriteLine($"Quest {def.QuestId}: skipped - map {defMapId} not in the Maps catalog.");
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

				// Hand-reviewed: leave every field as stored. See QuestDefinition.Verified.
				if (existing.Verified && !force)
				{
					protectedRows++;
					continue;
				}

				existing.QuestLineId = def.QuestLineId;
				existing.GoalId = def.GoalId;
				existing.Level = def.Level;
				existing.MapId = def.MapId;
				existing.MarkerX = def.MarkerX;
				existing.MarkerY = def.MarkerY;

				// The area's ACGHash, off the captured journal (#544). Only overwrite when the harvest HAS one: the
				// quest index seeds it by hand for two quests, and an area no journal was captured for must not lose it.
				if (def.IndexMagic != 0)
					existing.IndexMagic = def.IndexMagic;

				// The series HEAD off the captured Quest_c record (#566), same rule: only overwrite when the harvest has
				// one. NB the other Snapshot* scalars deliberately do NOT refresh here - they land only when a row is
				// first added - so a column added later reaches an existing row through a line like this one or not at
				// all. That is why this one is here and why adding the column alone left all 108 rows at 0.
				if (def.ChainHeadQuestId != 0)
					existing.ChainHeadQuestId = def.ChainHeadQuestId;

				// The MONEY reward off the same record (@+0x20), same rule. 21 of the 99 captured quests carry one;
				// it was emitted as a constant 0 for years under the name `Accept`, so every value was discarded.
				if (def.MoneyReward != 0)
					existing.MoneyReward = def.MoneyReward;
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

					// The goal's KIND TAG and required amount (#548). A full-update carries both for every goal - the
					// same values the snapshot path harvests where both exist (34 of 34 agree) - so refreshing them
					// here is the same "the capture is the truth source for every wire field" rule the goals module
					// applies, and it is what stops a full-update-only goal reading as "kill one unnamed thing".
					g.Separator = hg.Separator;
					g.RequiredCount = hg.RequiredCount;

					// The reserved (marker, 4CC-tag) pairs [1..6], lifted off the full-update body by the codec (#537). The same
					// words the snapshot path harvests, so a goal seen by both gets the same value from either - and a
					// goal seen ONLY in a full-update stops carrying zeros. Refreshed like the other wire fields.
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

			Console.WriteLine($"Quest definitions (journal content): {definitions.Count} harvested, {added} new, {updated} updated, {skipped} skipped (map not in catalog)"
			                  + (protectedRows > 0 ? $", {protectedRows} left as hand-verified." : "."));
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
		//
		// ⚠️ **This table is the ONLY one that drifts**, which #481 asked and is worth not re-asking. A full default
		// run under a global `--mode overwrite --dry-run` reports a negative delta for exactly three tables, and all
		// three are this one: `WorldObjects` (-87 today, up from the 62 the ticket measured), its cascaded
		// `WorldObjectWords` (-57), and the `TravelDestinations` (-3) a dependent module hangs off the rows being
		// deleted. Every other capture module comes out even. So there is no fourth generation of residue waiting
		// elsewhere, and the shape is specific to this table rather than a general property of upsert modules.
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
				// WorldObjectWords cascades.
				//
				// ⚠️ "The enrichment columns other modules own live on rows the harvest DOES yield" is NOT true, and
				// this is the reason the mode is still not safe to fire blind on the shipped table (#481). Measured
				// 2026-08-03 against the 120-capture corpus: 2400 stored vs 2313 harvested = 87 orphans, of which
				//   * 84 have an EXACT name+position twin on another map - the same object filed under two playfield
				//     ids, which is what map-tracking residue looks like;
				//   * 3 are item-named drop dynels with no captured "[TAG]" identity (Girdle of Erlik x2, one glove) -
				//     the same transient class the TemplateItemId 13782 purge above targets, missed because that
				//     purge keys on the loot-bag template rather than on "names an item and carries no tag".
				// So none of them is unique CONTENT - but the twin is not always the row to keep. `Ymir's Pass` on map
				// 1092 is an orphan carrying InteractAction=ProxyTeleport -> 1090, while its 1090 twin carries neither,
				// and map 1092 is demonstrably a real place: the table holds the RECIPROCAL zone line (1090
				// "Amphitheatre of Karutonia" -> 1092). The table has 22 enriched doors and a world-teleports re-run
				// reproduces only 18, so an overwrite + re-enrich does not restore all of them.
				var live = pending.Keys.ToHashSet();
				var orphans = await db.WorldObjects.Where(o => !live.Contains(o.Id)).ToListAsync();
				if (orphans.Count > 0)
				{
					// Print every row, with the columns that say whether it is residue or content. ⚠️ Read this list
					// before trusting the mode: an "orphan" is only junk if the CURRENT corpus is a superset of the one
					// that built the table, and ours is not. On the shipped table they are cross-map duplicates rather
					// than lost content (see above) - but which member of a pair is the correct one is decided per map
					// pair, not by the harvest alone.
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
					Word2 = e.Morphs[i].Word2, // #672: two captured tables carry it; dropping it re-declines them
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
		// Flags each quest whose @609 objective region we can rebuild EXACTLY as retail sent it - the gate that decides
		// what may ride the login snapshot, because a region the client cannot parse asserts it (statlist.cpp:134).
		//
		// Compared LIKE WITH LIKE: only against the captures whose record carries the same `XpReward` variant as the
		// record we would ship for that quest (see ObjectiveGoalExtractor.CapturedRegions for the evidence that F0
		// selects the region's spelling). Comparing against every capture regardless of variant failed a quest for
		// being a faithful rendering of the variant we DON'T emit: quest 10238 - the beach starter every new character
		// holds - rebuilds byte-exact in all 13 of its F0=150 captures and differs by 21 bytes from the 7 F0=175 ones,
		// which cost it the flag and left the login journal empty (#389).
		//
		// The safety property is unchanged and is the one that matters: the exact bytes we ship are bytes retail sent.
		// A quest with NO capture of our variant stays unflagged - unverifiable is not verified.
		public static async Task<(int Verified, int Total)> VerifyObjectiveRegions(DatabaseContext db,
			IReadOnlyDictionary<uint, List<(uint Variant, List<QuestObjectiveGoal> Goals)>> capturedByQuest)
		{
			var defs = await db.QuestDefinitions.Include(q => q.Goals).ToDictionaryAsync(q => q.QuestId);
			int verified = 0, total = 0;

			foreach (var (questId, captures) in capturedByQuest)
			{
				if (!defs.TryGetValue(questId, out var def) || def.Goals.Count == 0)
					continue;

				total++;

				bool Reproduces((uint Variant, List<QuestObjectiveGoal> Goals) c)
				{
					// The goal's SolvedTimes is the CAPTURED CHARACTER's progress, not quest data (#550) - so it is
					// not on the row and cannot be rebuilt from one. Feeding this capture's own reading in keeps the
					// comparison honest: every OTHER word still has to match exactly, and a region that only
					// reproduces because progress was ignored would still fail.
					return QuestObjectiveRegionCodec
						.Build(QuestObjectiveRegions.ForQuest(def, c.Goals[0].Instance,
							g => c.Goals.FirstOrDefault(cg => cg.GoalId == g.GoalId)?.Reserved is { Length: > 0 } r
								? r[0]
								: 0))
						.SequenceEqual(QuestObjectiveRegionCodec.Build(new QuestObjectiveRegion
						{
							Goals = c.Goals
						}));
				}

				// Two conditions, and both matter:
				//   * at least ONE capture reproduces      - the bytes we ship are bytes retail sent, not a near-miss
				//     that happens to have no same-variant capture to contradict it;
				//   * every capture of OUR variant reproduces - no counter-example among the records we actually emit.
				// A quest whose region does not depend on the variant satisfies these exactly as it did under the old
				// all-captures rule (its same-variant set is a subset of a set that all matches), so nothing that was
				// verified before is weakened; a quest that only matches SOMEONE ELSE's variant still fails the second.
				def.ObjectiveRegionVerified = captures.Any(Reproduces) && captures.Where(c => c.Variant == def.FieldF0).All(Reproduces);

				if (def.ObjectiveRegionVerified)
					verified++;
			}

			await db.SaveChangesAsync();
			return (verified, total);
		}

		// DERIVE a Quest_c record for a quest that has everything the login snapshot needs EXCEPT the record itself
		// (#389). Run AFTER VerifyObjectiveRegions, because the objective region's verdict is the gate it reads.
		//
		// The candidates are quests whose region provably rebuilds, whose line carries its 4CC, and which have all
		// five header text refs and a level - i.e. every part of the snapshot portion is already proven EXCEPT the
		// dozen record scalars, which no capture ever carried for them. Seventeen quests qualify today.
		//
		// ⚠️ This SYNTHESISES wire data, which SnapshotTails.IsSnapshotEligible warns against ("never by flipping a
		// quest's flag by hand"). What makes it defensible is that the record's SHAPE is fixed and only its VALUES
		// are invented, and every invented value was proven harmless IN-CLIENT by sweeping it on a live login:
		//   * @+0x14 takes five observed values and the client renders all five without complaint (only the `Share`
		//     button changes, on bit 4) - so an inherited or defaulted flag word is cosmetic, not structural;
		//   * @+0x28 is the XP the journal prints, so 0 renders "XP: 0" - wrong, but only wrong on screen;
		//   * @+0xf0 / @+0xf8 changed nothing visible at any value tried.
		// The scalars that ARE structural are the constants: FieldEc 1, Field114 0, Field11c 0, and the chain head,
		// which QuestcRecords.ForQuest already falls back to the quest's own id for.
		//
		// A quest with NO captured sibling in its line gets flags 2 - the modal captured value (52 of 99) and the one
		// with bit 4 CLEAR, i.e. shareable. That is the permissive choice on the only bit whose meaning is known.
		public static async Task<int> SeedDerivedSnapshotRecords(DatabaseContext db)
		{
			ArgumentNullException.ThrowIfNull(db);

			// The flag word is a property of the LINE, so a captured sibling states it for the whole line.
			var lineFlags = await db
				.QuestDefinitions.Where(q => q.HasSnapshotRecord && q.QuestLineFlags != 0)
				.GroupBy(q => q.QuestLineId)
				.Select(g => new { Line = g.Key, Flags = g.Max(q => q.QuestLineFlags) })
				.ToDictionaryAsync(x => x.Line, x => x.Flags);

			// Verified against a capture, OR buildable from the goal rows (#827) - the same widening
			// `IsSnapshotEligible` makes, and it has to be the same or the two disagree about which quests ship: the
			// gate would admit a quest this never gave a record to, and it would fall out of the snapshot anyway.
			// Buildability is evaluated below (it needs the goals loaded), so the query only narrows on what SQL can.
			var candidates = await db
				.QuestDefinitions.Include(q => q.Goals)
				.Include(q => q.TextRefs)
				.Include(q => q.Line)
				.Where(q => !q.HasSnapshotRecord && q.Level != 0)
				.ToListAsync();

			var seeded = 0;
			foreach (var def in candidates)
			{
				// Exactly the other arms of IsSnapshotEligible, so this can never flag a quest the gate would reject.
				if (def.Goals.Count == 0 || def.Line is not { Magic: not 0 })
					continue;

				if (!def.ObjectiveRegionVerified && !SnapshotTails.IsObjectiveRegionBuildable(def))
					continue;

				var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
				if (!SnapshotHeaderSlots.All(slots.Contains))
					continue;

				def.QuestLineFlags = lineFlags.TryGetValue(def.QuestLineId, out var f)
					? f
					: DefaultQuestLineFlags;
				def.FieldEc = 1;

				// @+0x118 runs level..level+5 across the captured records; the level itself is the low end of that
				// range and the only value we can derive rather than guess.
				def.Field118 = def.Level;
				def.HasSnapshotRecord = true;
				seeded++;
			}

			await db.SaveChangesAsync();
			return seeded;
		}

		// Mirrors SnapshotTails.HeaderSlots, which is private to the codec layer.
		private static readonly QuestTextSlot[] SnapshotHeaderSlots =
			[QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.QuestLineTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		// The modal captured @+0x14 (52 of 99) and the one with bit 4 clear - the shareable, permissive default.
		private const uint DefaultQuestLineFlags = 2;

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
				KeyText = LdbKey.Decode(text.Key) ?? new ProtoDecryptionKey(),
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
		internal static ConversationPacket BuildPacket(byte[] data, int sequence)
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

		// One entry per distinct mob in a capture. The identity is (MAP, handle), not the bare handle (#571): a handle
		// is scoped to the PLAYFIELD and the pool starts again on a zone change, so the same number names a different
		// NPC on the other side of a handoff. Keyed on the handle alone this kept the FIRST sighting and silently
		// dropped the second map's spawn - measured at 46 collisions across the corpus, every one of them cross-map
		// (`--report handle-recycling`), with not a single same-map reuse in 11,442 handles.
		private static List<NpcSpawnEntry> DistinctByHandle(IEnumerable<NpcSpawnEntry> entries)
		{
			var seen = new HashSet<(uint MapId, uint Handle)>();
			var result = new List<NpcSpawnEntry>();
			foreach (var e in entries)
			{
				if (seen.Add((e.MapId, e.Handle)))
					result.Add(e);
			}

			return result;
		}

		// Upserts the NpcTemplate TYPE rows (#244). Keyed by TemplateId; Id stays stable across re-imports.
		private static async Task<Dictionary<uint, NpcTemplate>> UpsertNpcTypes(DatabaseContext db, List<NpcSpawnEntry> entries)
		{
			var byTemplate = await db.NpcTemplates.Include(n => n.StatCore).ToDictionaryAsync(n => n.TemplateId);

			// The type's MOVEMENT MODE, taken as the MODAL value over every captured sighting of the type (#605).
			//
			// This replaces a scan that read a "minimal post-IIR recordCount" off the FIRST sighting of each type and
			// stored it in a second column. Two things were wrong with that, and the second is why the first was never
			// caught. (1) It is the same value as the character message's own `movement_mode`, so the type carried one
			// number twice, lifted from two different representative spawns - 45 of 368 types ended up disagreeing with
			// themselves, and a spawn built from such a row went out in a shape retail never sends. (2) The scan
			// stopped at the first sighting, which is what let it be documented as "type-stable - no type is ever seen
			// with two". A first-record-wins scan cannot observe a second value, so that claim was unfalsifiable rather
			// than measured ([[first_record_wins_silently_truncates_repeated_wire_records]]); the field is really
			// ~62% type-stable.
			//
			// The mode is a measurement of the type's OWN sightings, not a corpus-wide guess - which is the line #602
			// drew. It also covers strictly more types than the old scan: that one needed a minimal-form capture, while
			// `movement_mode` is present on every captured NPC spawn.
			//
			// `npc_stats.spawn_animation` gets the same treatment for the same reason. It is the other field #605
			// named, `--report field-predictors` finds NOTHING in the spawn body that predicts it (no coarse candidate
			// beats the 91.7% a constant scores), and it is 77.9% type-stable over fresh sightings - so it has no
			// derivation and no resting value, and the only honest improvement left is to stop letting the FIRST
			// sighting speak for the type.
			// ⚠️ **`bcc_state` and its `anim_idle_id` need it MORE than either of those, and not having it is what put
			// half of Old Tarantia on the floor.** They are the state-controller's state and the animation that goes
			// with it, and they are per-spawn-EVENT: over the corpus 102 of 1024 types are caught in more than one
			// `bcc_state`, and the split is lopsided every time - a Citizen (12460) is captured standing (state 1,
			// `anim_idle_id` 42024) 286 times and SITTING (state 9, idle 42176) 5 times. The representative that
			// reached the lift was one of the 5, so the type's definition of a Citizen became "sitting", and every
			// Citizen in the world sat down - while still walking around, because a MoveTask moves an NPC whatever
			// pose it is holding.
			//
			// The two are tallied SEPARATELY but move together, because they describe one thing: the majority of a
			// type's sightings carry both the resting state and its resting idle, so both modes come off the same
			// spawns. Tallying them independently is still the right shape - it is what keeps a type whose idle
			// varies for another reason from being dragged by its state.
			//
			// ⚠️ Not a blanket reset to 1. 14 types are NEVER captured in state 1 - the Door Warden (19619) is state
			// 11 in all 15 of its sightings - so a type CAN rest somewhere other than 1, and flattening the column
			// would trade one wrong pose for another. The mode is a measurement of that type's own sightings and
			// keeps the Door Warden exactly where retail puts it.
			var modes = new PerSpawnEventModes();
			foreach (var e in entries)
			{
				if (e.TemplateId == 0)
					continue;

				var b = WorldObjectColumns.ExtractBody(e.Data);
				if (b != null && SpawnBodyReader.TryReadCharacterMessage(b, out var seen))
					modes.Observe(e.TemplateId, seen.Message);
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
						// ⚠️ The core is lifted from ONE representative spawn, so anything that NPC happened to be doing
						// at capture time would become the type's definition of itself and be inherited by every other
						// placement (#602). ClearPerSpawnEventState resets the measured per-event fields first.
						if (npc.StatCore == null)
						{
							npc.StatCore = NpcStatCoreColumns.Lift(read.Message);
							NpcStatCoreColumns.ClearPerSpawnEventState(npc.StatCore);
						}

						npc.StatCore.IirHeader ??= NpcStatCoreColumns.ReadIirHeaderWord(body);
					}
				}

				// #605/#688: the fields the representative spawn must NOT decide on its own - all four are rolled per
				// spawn event, so the mode over ALL of this type's sightings replaces whatever Lift took from the
				// representative. See PerSpawnEventModes, which is shared with the pet/mount importers (#689).
				modes.Apply(e.TemplateId, npc.StatCore);
			}

			await db.SaveChangesAsync();
			return byTemplate;
		}
	}
}
