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
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Shared capture-import orchestration the modules (Faolan.Importer.Modules) drive - the upsert/wipe-rebuild
	// helpers that sit above the per-subsystem CaptureX importers/extractors.
	// NPC types, spawns, spells, patrol routes and stationary placements from the captures - the three-tier Npc / NpcSpawn model (docs/subsystems/npc-three-tier-architecture.md). One of the five domain halves of what was one 2,064-line ImportOps (#928 item 7).
	public static class NpcImportOps
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
			var morphsByTemplate = new Dictionary<uint, IReadOnlyList<AppearanceEntry>>();
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
