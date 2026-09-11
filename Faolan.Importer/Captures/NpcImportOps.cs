using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Capture-import orchestration for NPC types, spawns, spells, patrol routes and stationary placements - the
	///     upsert helpers the modules in Faolan.Importer.Modules drive above the per-subsystem capture importers and
	///     extractors.
	/// </summary>
	/// <remarks>
	///     The model is the three-tier Npc type / NpcSpawn placement / live entity split described in
	///     docs/subsystems/npc-three-tier-architecture.md.
	/// </remarks>
	public static class NpcImportOps
	{
		/// <remarks>
		///     Home facing for unique quest NPCs that turn during their quest: the burst-truth dedup can pick a session
		///     in which one had already turned, so the facing drifts from its home pose. Casilda's rotation is pinned
		///     to her create-dump FA015807 transform, the byte-exact facing she has while chained at the post.
		/// </remarks>
		private static readonly Dictionary<string, Quaternion> UniqueNpcFacings = new Dictionary<string, Quaternion>
		{
			["Casilda"] = new Quaternion(0f, 0.8661140203475952f, 0f, -0.4998469948768616f)
		};

		/// <remarks>
		///     Home positions for unique quest NPCs: they move during their quest, so the dedup must not strand them
		///     wherever a capture happened to see them.
		/// </remarks>
		private static readonly Dictionary<string, Vector3> UniqueNpcHomes = new Dictionary<string, Vector3>
		{
			["Casilda"] = new Vector3(361.64300537109375f, 153.34036254882813f, 717.66802978515625f)
		};

		/// <remarks>
		///     How near a spawn must stand to a candidate patrol route's nearest waypoint to be counted as walking it.
		///     Measured, not chosen: over the spawns that carry their own route by handle, spawn-to-nearest-waypoint is
		///     0.0 at the median, 4.4 at p90 and 7.8 at p95.
		/// </remarks>
		private const float RouteMatchRadius = 10f;

		/// <remarks>
		///     The widest spawn-to-route gap a handle-joined route may have before it is read as a mis-join rather than
		///     a route. The handle join is identity evidence and is deliberately far looser than
		///     <see cref="RouteMatchRadius" />: an NPC may have drifted from its recorded spawn point by the time its
		///     MoveTask was issued, and that is a route, not an error.
		///     What it guards against is handle recycling - a handle freed by a despawn is reissued inside the same
		///     capture, so a route observed for the first NPC can land on the second one's spawn. That failure is
		///     gross rather than marginal: the rejected routes are compact loops hundreds of units from the spawn they
		///     were joined to, which no NPC could be walking. Genuine matches leave a wide margin, spawn-to-nearest
		///     waypoint being 23.1u at p95 over all routes. A route rejected here still gets the positional backfill
		///     below, which is the correct owner-finding path.
		/// </remarks>
		private const float MaxHandleRouteGap = 40f;

		/// <param name="classSpecificMaps">
		///     The maps whose population is per class (Map.ClassSpecific - the city armories). A spawn harvested on one
		///     of them is evidence about one class's copy of the playfield, so it is stamped with the class the capture
		///     was played on and elected only against captures of that same class.
		/// </param>
		public static (List<NpcSpawnEntry> Spawns, List<PatrolPathEntry> Patrols, List<NpcSpawnEntry> ObservedTypes) BuildBurstTruthSpawns(IReadOnlyList<NamedCapture> captures, IReadOnlySet<string> petNames,
			IReadOnlySet<uint> classSpecificMaps, IReadOnlySet<uint> triggerSpawnedTypes)
		{
			// Summons are dropped per capture, before anything else sees them: a pet handle is session-scoped, so the
			// exclusion has to be too. Doing it here rather than at the placement step keeps a summon out of the
			// morph, equipment and route backfills and out of the richest-capture election, not just out of the final
			// placement list.
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

			// Keyed by template id, not name: many NPCs share a placeholder internal name - "Halen van Dummy" is the
			// internal name of some sixteen unrelated templates - so keying the richest-set backfill by name leaks a
			// geared level-80 NPC's gear and morphs onto a same-named level-10 child. The template id is the stable
			// per-type key: distinct templates stay separate, while the same NPC across captures still merges.
			var morphsByTemplate = new Dictionary<uint, IReadOnlyList<AppearanceEntry>>();
			foreach (var e in perCap.SelectMany(p => p.All))
			{
				if (e.Morphs.Count > 0 && (!morphsByTemplate.TryGetValue(e.TemplateId, out var cur) || e.Morphs.Count > cur.Count))
					morphsByTemplate[e.TemplateId] = e.Morphs;
			}

			// Richest worn-equipment set per template: the burst-truth dedup can pick a session in which an NPC wears
			// fewer pieces than another capture of the same NPC - Casilda at the post wears four, including the
			// slot-18 chains (template 402981), but only three while following the player. Keeping the fullest loadout
			// stops the chosen spawn being stripped of state-specific gear it has in another capture. Per template, so
			// it never leaks across the shared-placeholder-name templates above.
			var equipByTemplate = new Dictionary<uint, IReadOnlyList<NpcSpawnEquipment>>();
			foreach (var e in perCap.SelectMany(p => p.All))
			{
				if (e.Equipment.Count > 0 && (!equipByTemplate.TryGetValue(e.TemplateId, out var cur) || e.Equipment.Count > cur.Count))
					equipByTemplate[e.TemplateId] = e.Equipment;
			}

			// How many distinct NPCs each template stands for, because the merge above is only identity-preserving
			// when the answer is one. Casilda is a unique creature, so the richest kit seen on her template is her own
			// richest kit. A Poacher is not: one template stands for many individuals, and retail dresses them
			// individually - the captured beach Poachers wear three different kits, sharing only their leather legs -
			// so applying the richest kit to all of them would hand every Poacher in the world the same loadout.
			//
			// A placement is (map, position): a handle is session-volatile, and the same NPC seen in two captures is
			// the one standing in the same spot.
			var placementsPerTemplate = perCap.SelectMany(p => p.All)
				.GroupBy(e => e.TemplateId)
				.ToDictionary(g => g.Key, g => g.Select(e => (e.MapId, X: MathF.Round(e.X), Z: MathF.Round(e.Z))).Distinct().Count());

			// Richest patrol route per (map, template) - the third backfill of the same shape as the two above.
			//
			// A route observed only through the burst-truth capture's own handles means a capture that wins on spawn
			// count while happening to observe fewer NPCs mid-patrol takes the map's patrols down with it. But a route
			// is per-NPC content - the path is a stable asset of that spawn - so it can be backfilled from any capture
			// that saw the NPC, exactly as morphs and equipment are.
			//
			// Keyed by (map, template) for the reason above: a name is not a key, because unrelated templates share
			// internal names and keying by one would hand an NPC another's path.
			//
			// Template alone is not enough to pick the route either. Handing every same-template spawn the one route
			// observed for that template makes ten guards of one template - one of them ever seen walking - all walk
			// the same path and converge on one spot, a worse artefact than the missing route and one that looks like
			// a win in the row counts. So the route is tied to the spawn positionally: a spawn inherits a candidate
			// route only if it is standing on it, within the measured RouteMatchRadius, which accepts ~95% of genuine
			// matches while staying far tighter than the spacing between distinct routes on a map. An ambiguous spawn,
			// with two candidate routes in range, is left alone rather than guessed at.
			var routeCandidates = new Dictionary<(uint Map, uint Template), List<PatrolPathEntry>>();
			foreach (var cap in perCap)
			{
				// (map, handle), matching DistinctByHandle - a handle alone is not an identity across a zone change.
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

			// Elected per (map, type), not per map. The login burst is an instant - one snapshot of what is standing
			// there - so it gives true density without counting a respawn twice, and it stays the preferred source.
			// But electing one capture for a whole map and locking every type it saw to that capture does not survive
			// a large, interest-streamed zone: a capture that entered by a mid-session map change and polled almost
			// immediately has a burst window holding a handful of spawns while hundreds more arrive after the poll,
			// and if that capture's burst wins the map it excludes every other capture's later observations.
			//
			// So each type takes whichever single capture saw the most of it, preferring a burst when the burst is at
			// least as complete. One capture per type is still the rule - this only stops a type being pinned to a
			// capture that barely saw it. `Merge` dedups the result by position, so a respawn on the same anchor
			// inside the chosen capture still collapses.
			//
			// On a class-specific map the key also carries the class, so each class's copy of the armory elects its
			// own truth capture independently and the two sets never compete: captures of two classes see disjoint
			// quartermasters on the same map, and without the class in the key they would be unioned onto one map with
			// every class served all of them.
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

			// Pets are content of the creature type (lifted onto the type columns by CapturePetImporter and spawned
			// from the type at summon time), never world placements. A captured session's summoned pets otherwise leak
			// in here as permanent NpcSpawns and stand around forever where the player summoned them.
			//
			// This is the second of two nets and the weaker one. `SummonedHandles` above is the general rule - it asks
			// the wire who owns the creature - while this one only knows the names that have been seeded. Both are
			// kept because each catches what the other misses on the corpus: creatures whose owner-binding was
			// captured but which are in no name roster, and creatures in the roster for which no PetIIR was ever
			// captured. Dropping either one regresses.
			//
			// Trigger-spawned types go the same way, one level over. `triggerSpawnedTypes` is every creature the world
			// puts in play through a script rather than a placement - an NpcAmbush ambusher that a quest goal
			// advancing spawns, or an NpcEncounter boss that a proximity lure swaps into. A capture that caught one
			// caught the trigger firing, so harvesting it as a placement is the summoned-pet failure exactly: it
			// leaves the ambusher standing there permanently, for every player, regardless of quest state. Keyed by
			// template rather than by name, because unlike the pet roster these are real tables with real ids.
			var placements = spawns
				.Where(e => !petNames.Contains((e.Name ?? string.Empty).ToLowerInvariant()))
				.Where(e => !triggerSpawnedTypes.Contains(e.TemplateId))
				.ToList();

			// The placements are position-deduped; the types are not. A creature that loses the position dedup was
			// still observed, and its type must survive even though its placement does not: an encounter boss spawns
			// on the exact spot her lure vacated, so `Merge` - which keys on position and keeps the first - drops her,
			// and with her the whole type. Harvesting the type off the unmerged list keeps it while she correctly gets
			// no placement; a trigger spawn is not a world placement, and a type with no NpcSpawn row is an
			// established shape that pets and mounts already have.
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

			// Tags the spawns this capture saw on a class-specific map with the class it was played on, and leaves
			// every other spawn class-agnostic (null).
			//
			// A spawn on a class-specific map from a capture whose class could not be determined is dropped, not kept
			// as null: null means every class sees this, so keeping it would put one class's quartermasters in front
			// of all twelve - precisely what the column exists to prevent. Losing a few rows from an unattributable
			// trace is the cheap side of that trade, and it is reported rather than silent.
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

					// The richest-kit merge applies only where the template is one NPC; everywhere else a placement
					// keeps what it was seen wearing, and the template set is a fallback for an observation that
					// carried no equipment at all - the same rule the morph backfill above uses.
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
					// template, so a session that did not catch this NPC walking does not cost it its path. The handle
					// join is sanity-checked positionally (MaxHandleRouteGap) so a recycled handle cannot hand this
					// spawn a route belonging to a different NPC on the far side of the zone; a rejected route falls
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

		/// <summary>
		///     Upserts spawns by (map, spawn point); never wipes. Ensures the NpcTemplate type rows exist first.
		/// </summary>
		/// <param name="observedTypes">
		///     The unmerged observation list - every creature seen, including ones whose placement lost the position
		///     dedup. Types come from it, placements from <paramref name="spawns" />. Passing null falls back to the
		///     placements, which is what a caller with no wider view should do.
		/// </param>
		public static async Task ImportNpcSpawns(DatabaseContext db, List<NpcSpawnEntry> spawns, List<NpcSpawnEntry>? observedTypes = null)
		{
			var typeByTemplate = await UpsertNpcTypes(db, observedTypes ?? spawns);

			foreach (var e in spawns)
			{
				// The lookup key is the placement's full identity, class included: on a class-specific map two classes'
				// NPCs share a spot, and matching on position alone would have each class's import overwrite the
				// other's row.
				var existing = await db
					.NpcSpawns.Include(n => n.Morphs)
					.Include(n => n.Equipment)
					.FirstOrDefaultAsync(n => n.MapId == e.MapId && n.PositionX == e.X && n.PositionY == e.Y && n.PositionZ == e.Z && n.ClassId == e.ClassId);

				// NpcTemplateId is required, so a spawn whose type does not resolve is dropped - the same rule
				// ImportNpcSpells applies to typeless casters.
				if (e.TemplateId == 0 || !typeByTemplate.TryGetValue(e.TemplateId, out var npc))
					continue;

				// The id is derived from the placement (see NpcSpawn/DerivedKey), so a re-harvest reproduces it rather
				// than renumbering the spawn set and every equipment, morph and patrol row hanging off it. An
				// `existing` row already carries it.
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

		/// <summary>
		///     Caster NPCs' spells, upserted by (NpcTemplate type, spell id). The caster name is resolved to its
		///     NpcTemplate id - the types were upserted by <see cref="ImportNpcSpawns" /> just before - and stored only
		///     as that foreign key, so a cast whose caster has no captured type is dropped; the loader reads spells
		///     through the same navigation.
		/// </summary>
		/// <remarks>
		///     The player-name check is a belt, not the braces: it only knows characters in this database, so it never
		///     sees a stranger who stood near the capturing client on the retail server. What keeps those out is
		///     structural and upstream - their spawns carry player_stats, so NpcSpawnExtractor never gives them a
		///     handle-to-name entry for NpcSpellExtractor to join on. Both are kept because this one also catches the
		///     capture's own player once imported, whose spawn is dropped by handle rather than by message.
		/// </remarks>
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

		/// <summary>
		///     Decodes the captured 54-byte MagicBuffIIR data region into the NpcSpell columns.
		/// </summary>
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

		/// <summary>
		///     NPC patrol routes, one per NpcSpawn. The route is correlated to its NPC by the shared spawn position
		///     (the spawns are imported and saved just before), then keyed to it; the columnarized task tree is
		///     replaced wholesale on re-import.
		/// </summary>
		public static async Task ImportPatrolPaths(DatabaseContext db, List<PatrolPathEntry> paths)
		{
			// Resolve every route to its spawn first, keyed by spawn. Two captured routes can correlate to the same
			// spawn (they share a transform), and a per-entry loop cannot see what an earlier iteration already
			// queued, so the second route's nodes would collide with the first's on the primary key.
			//
			// The richest route wins, not the last one: last-wins makes the shipped path depend on which capture the
			// corpus happened to iterate last, so adding a capture of an already-covered map can replace a full patrol
			// with whatever that run observed. Two captures of one spawn are two observations of the same asset, so
			// the one carrying more of the route is the better record of it - the same rule ImportConversations
			// applies when a later capture is shorter.
			var bySpawn = new Dictionary<uint, (NpcSpawn Spawn, MoveTaskNode Task)>();
			foreach (var e in paths)
			{
				// Class included: on a class-specific map (map, position) matches two rows, and a route would
				// otherwise be handed to whichever class the query happened to return first.
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

			// Drop the persisted routes for those spawns before staging the replacements. The node key is derived from
			// the tree position, so a re-import reuses the very same keys, and queueing the deletes and the inserts
			// into one SaveChanges races them (unique constraint failed on PatrolNodeWaypoints) the same way it does
			// for the conversation tree. ExecuteDelete runs immediately, inside the import transaction, and cascades
			// to the waypoints and words.
			var spawnIds = bySpawn.Keys.ToList();
			if (spawnIds.Count > 0)
				await db.PatrolNodes.Where(n => spawnIds.Contains(n.NpcSpawnId)).ExecuteDeleteAsync();

			// Build each spawn's tree from the wire AST rather than reusing the extractor's instance: one entry's task
			// can be shared across spawns, and EF cannot track the same node object twice. Converting per spawn
			// through PatrolNodeMapper - the single wire-to-row authority - is what keeps every column populated; a
			// hand-written field-by-field clone has to be updated for each new column, and nothing forces that,
			// because the round-trip gate converts in memory and never reads a row back.
			foreach (var (spawn, task) in bySpawn.Values)
				AddNodeTree(spawn, PatrolNodeMapper.ToNode(task));

			// The beat identifies the placement, so stamp it now that every route is attached. Rows that come out with
			// the same key are sightings of one patroller in different capture sessions: spawns are keyed on MapId and
			// exact XYZ, and a patroller stands somewhere different every time it is seen, so each sighting mints a
			// placement row.
			//
			// Nothing is deleted - the redundant rows are the placement's variant pool. What differs between them is
			// exactly what retail re-rolls per spawn (level, morphs and the seven equipment slots), so the world draws
			// one at spawn rather than standing them all up at once.
			foreach (var (spawn, _) in bySpawn.Values)
				spawn.PlacementId = PatrolRoute.PlacementKey(spawn.PatrolNodes, spawn.NpcTemplateId);

			await db.SaveChangesAsync();

			var pools = bySpawn.Values.Select(v => v.Spawn.PlacementId).Where(p => p != null).Distinct().Count();
			Console.WriteLine($"Patrol placements: {bySpawn.Count} route-carrying spawn(s) -> {pools} placement(s) (#713).");
		}

		/// <summary>
		///     Flattens the task tree onto the spawn, stamping each node's position as it goes.
		/// </summary>
		/// <remarks>
		///     OrdinalPath is the chain of sibling ordinals from the root ("" at the root, then "1", "1&gt;0",...) and
		///     ParentPath is its parent's. That pair is the node's key (see PatrolNode), so it must be computed here
		///     rather than left to EF.
		/// </remarks>
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

		/// <summary>
		///     How much of a route an observation carries: total nodes, then total waypoints as the tiebreak. Used only
		///     to pick between two captures of the same spawn - see <see cref="ImportPatrolPaths" />.
		/// </summary>
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

		/// <summary>
		///     Stamps <c>PlacementId</c> on the stationary spawn clusters the corpus proves are one placement seen
		///     twice - the case the route key cannot reach, because these NPCs have no beat.
		/// </summary>
		/// <returns>How many rows were pooled: cluster size minus one, summed.</returns>
		/// <remarks>
		///     The decision is <see cref="StationaryPlacementResolver" />'s and the reasoning lives there; in short the
		///     test is a handle, not a distance. Retail genuinely places same-name NPCs a metre apart, so a tolerance
		///     would collapse the world's guard pairs, but one handle sighted at two of a cluster's positions walked
		///     between them, and that is one entity.
		///     Nothing is deleted, exactly as on the patrol side: the redundant rows are the placement's variant pool,
		///     and the world stands one entity up drawing its level, morphs and equipment from the group.
		/// </remarks>
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
			// Race and Sex are type-level: set on the NpcTemplate in UpsertNpcTypes, not on the spawn row.
			row.Level = e.Level;
			row.MonsterScale = e.MonsterScale;
			row.MonsterData = e.MonsterData;
			row.HeadMesh = e.HeadMesh;
			row.HasEquipment = e.HasEquipment;
			row.Health = e.Health;

			// Per-placement stat pools: lift the level-scaled, per-placement values from this spawn's capture through
			// the same codec the type core uses, overriding the type StatCore when the body is built.
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
					Word2 = e.Morphs[i].Word2, // captured tables carry it; dropping it declines them on the wire
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

		/// <summary>
		///     Every entity handle this session's server bound to an owner, i.e. every summoned creature, read off the
		///     wire's own owner-binding (PetIIR mode 0 assign / mode 1 dismiss) rather than a roster of pet names.
		/// </summary>
		/// <remarks>
		///     The name roster is the seeded SpellEffect SummonPet templates, so it only ever excludes summons that are
		///     already known and fails silently for any that are not - a summon missing from the seed gets planted as
		///     permanent scenery wherever a capture had one out. Owner-binding is the general fact: a creature is a pet
		///     exactly when the server said it belongs to somebody, whatever its class or name.
		///     Handles are per-session, so this set is only valid for the capture it came from.
		/// </remarks>
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

		/// <summary>
		///     One entry per distinct mob in a capture, keyed on (map, handle).
		/// </summary>
		/// <remarks>
		///     A handle is scoped to the playfield and the pool starts again on a zone change, so the same number names
		///     a different NPC on the other side of a handoff. Keyed on the handle alone this keeps the first sighting
		///     and silently drops the second map's spawn; every collision measured over the corpus is cross-map, with
		///     no same-map reuse at all (`--report handle-recycling`).
		/// </remarks>
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

		/// <summary>
		///     Upserts the NpcTemplate type rows, keyed by TemplateId so the id stays stable across re-imports.
		/// </summary>
		private static async Task<Dictionary<uint, NpcTemplate>> UpsertNpcTypes(DatabaseContext db, List<NpcSpawnEntry> entries)
		{
			var byTemplate = await db.NpcTemplates.Include(n => n.StatCore).ToDictionaryAsync(n => n.TemplateId);

			// Four fields are rolled per spawn event rather than being properties of the type, so the modal value over
			// every captured sighting decides them instead of whatever the representative spawn was doing:
			// `movement_mode`, `npc_stats.spawn_animation`, `bcc_state` and its `anim_idle_id`.
			//
			// `movement_mode` is only ~62% type-stable and `spawn_animation` ~78%, and nothing in the spawn body
			// predicts either (`--report field-predictors`), so neither has a derivation or a resting value.
			//
			// `bcc_state` and `anim_idle_id` need it most: they are the state controller's state and the animation
			// that goes with it, and a type caught in a rare pose defines itself by it. A Citizen captured standing
			// hundreds of times and sitting a handful became "sitting" when the representative was one of the few -
			// and then sat down all over the city while still walking around, because a MoveTask moves an NPC whatever
			// pose it is holding.
			//
			// The two are tallied separately but move together, because the majority of a type's sightings carry both
			// the resting state and its resting idle. Tallying them independently is still the right shape: it keeps a
			// type whose idle varies for another reason from being dragged by its state.
			//
			// This is not a blanket reset to a resting value. Some types are never captured in the common state - the
			// Door Warden (19619) is bcc_state 11 in every sighting - so flattening the column would trade one wrong
			// pose for another. The mode is a measurement of that type's own sightings.
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
					// Community, Race and Sex live on StatCore, set below via Lift.
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

				// Lift the type-level CharacterIIR stat core from a representative capture (the first spawn of the type
				// that decodes), so a spawn body can be constructed from typed columns rather than a captured blob.
				if (npc.StatCore == null || npc.StatCore.IirHeader == null)
				{
					var body = WorldObjectColumns.ExtractBody(e.Data);
					if (body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					{
						// The core is lifted from one representative spawn, so anything that NPC happened to be doing
						// at capture time would become the type's definition of itself and be inherited by every other
						// placement. ClearPerSpawnEventState resets the per-event fields first.
						if (npc.StatCore == null)
						{
							npc.StatCore = NpcStatCoreColumns.Lift(read.Message);
							NpcStatCoreColumns.ClearPerSpawnEventState(npc.StatCore);
						}

						npc.StatCore.IirHeader ??= NpcStatCoreColumns.ReadIirHeaderWord(body);
					}
				}

				// The four fields the representative spawn must not decide on its own: they are rolled per spawn
				// event, so the mode over all of this type's sightings replaces whatever Lift took from the
				// representative. See PerSpawnEventModes, which the pet and mount importers share.
				modes.Apply(e.TemplateId, npc.StatCore);
			}

			await db.SaveChangesAsync();
			return byTemplate;
		}
	}
}
