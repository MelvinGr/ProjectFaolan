using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Fills a zone with its population, once per map and instance: the captured NPC spawns and the static world
	///     objects a map ships, read out of the database and turned into live <see cref="WorldEntity" /> and
	///     <see cref="LiveWorldObject" /> instances in the world model.
	/// </summary>
	/// <remarks>
	///     It writes nothing to the wire, which is the point of it being its own class. Loading happens on the first
	///     entry to an instance, before anyone can see it; the streaming that follows is interest-driven and belongs to
	///     <see cref="WorldBroadcaster" />, which is also where the per-viewer questions live. The three caches here -
	///     quest-gated NPCs, story-kill targets and escort-hidden NPCs - are read-once catalog maps, not per-player
	///     state.
	///     <para>
	///         The load is idempotent through its <c>TryAdd</c> gates, and each instance gets its own population: two
	///         copies of a playfield must not share entities, so the key is (mapId, instanceId) and never mapId alone.
	///     </para>
	///     It also answers why a class-specific map looks empty - see <see cref="DescribeMissingClassImport" />.
	/// </remarks>
	public sealed class ZonePopulationLoader
	{
		/// <summary>
		///     How close a route's last waypoint must come back to its first for the route to count as closed - walked
		///     round and round - rather than open, walked back down again. 494 of the 1,318 stored routes close this way.
		/// </summary>
		private const float RouteClosedTolerance = 5f;

		private const double PatrolDwellSeconds = 2.0;

		/// <summary>
		///     The ordinary standing idle, shared by 1,008 of the 1,208 templates, against a story pose such as
		///     Casilda's chained 156196 which exactly one template carries. Used as a freed escortee's held pose.
		/// </summary>
		private const uint GenericIdleAnimationId = 42024;

		/// <summary>
		///     The ordinary behaviour-control state: 1,186 of the templates that declare one carry it, against
		///     Casilda's captive 80.
		/// </summary>
		/// <remarks>
		///     The client calls it the BCC state (<c>CharacterStatId</c> 0x14) and it selects the behaviour tree, so a
		///     creature left in a story state never runs its locomotion and slides instead of walking.
		/// </remarks>
		private const uint OrdinaryBccState = 1;

		/// <summary>How far a formation follower looks for its leader at zone load.</summary>
		/// <remarks>
		///     Ours rather than retail's: the corpus states which creature walks with which and never a maximum
		///     distance, so it is set at the leash territory a patroller already works in rather than a smaller
		///     invented number.
		/// </remarks>
		private const float FormationBindRange = WorldState.NpcLeashRange;

		private readonly AmbientPoolService _ambient;

		private readonly ComboService _combos;

		private readonly INpcRepository _npcs;
		private readonly IWorldRepository _world;

		private readonly ILogger _logger;

		private readonly WorldSimulation _sim;

		// The zones whose static world objects have been loaded.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _objectLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// Escort NPCs hidden once their quest is complete: template id -> the quest that hides it.
		private Dictionary<uint, uint> _escortHiddenNpcs = null!;

		// The zones whose NPCs have been loaded into the world model: once per instance, on first entry to that copy,
		// because each instance gets its own NPC population.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _npcLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// Quest-gated NPC template id -> the quest ids that reveal it, cached once. Such an NPC is streamed only to
		// players holding one of its quests active; see WorldEntity.GateQuestIds and IsVisibleTo.
		private Dictionary<uint, uint[]> _questGatedNpcs = null!;

		// The NPCs a quest kills once. A separate set from the quest-gated map on purpose: visibility and respawn are
		// independent, and one column answering both could not express a quest-only target that must come back.
		private HashSet<uint> _storyKillTargets = null!;

		public ZonePopulationLoader(WorldSimulation sim, INpcRepository npcs, IWorldRepository world, ComboService combos, AmbientPoolService ambient, ILogger logger)
		{
			_sim = sim;
			_npcs = npcs;
			_world = world;
			_combos = combos;
			_ambient = ambient;
			_logger = logger;
		}

		/// <summary>
		///     Loads a map's NPCs from the spawn rows into the world model once, on first entry, so the server has an
		///     authoritative shared view of them for the tick, the AI and combat to drive.
		/// </summary>
		/// <param name="forClass">
		///     The class of the character this copy is being populated for. It matters only on a class-specific map -
		///     <c>Map.ClassSpecific</c>, the city armories and The Reliquary - where the playfield holds a different NPC
		///     set per class; such a map is necessarily Solo, so the copy has exactly one owner and one class. Elsewhere
		///     every spawn is class-agnostic and this is ignored.
		/// </param>
		public async Task EnsureZoneNpcs(uint mapId, uint instanceId, ConanClass forClass)
		{
			if (!_npcLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var clock = System.Diagnostics.Stopwatch.StartNew();
			var npcs = await _npcs.GetNpcSpawns(mapId, forClass);

			// The map's scripted encounters, keyed by lure type - one query per zone load rather than per placement.
			var encounters = (await _npcs.GetNpcEncounters(mapId)).ToDictionary(e => e.LureNpcTemplateId);

			// The map's trigger-spawned types: a quest ambusher and an encounter boss alike. Neither stands in the
			// world - one appears when a goal advances, the other when a lure swaps - so a captured placement of one is
			// a harvest of the trigger firing, and it is skipped below. The importer already excludes them from the
			// burst-truth set, so on a current database this finds nothing; it is the second net, for a database built
			// before that rule.
			var triggerSpawned = (await _npcs.GetNpcAmbushes(mapId)).Select(a => a.NpcTemplateId)
				.Concat(encounters.Values.Select(e => e.BossNpcTemplateId))
				.ToHashSet();
			var zone = _sim.World.ZoneFor(mapId, instanceId);

			// The map's RDB spawn areas, so each NPC carries its area label ready for the live .togglenpcgroup toggle.
			// Computed at load - a cheap query stored as a string - so the toggle is instant and also catches NPCs that
			// stream in while it is on; the toggle only gates whether the label is sent.
			var spawnAreas = await _npcs.GetNpcSpawnAreas(mapId);

			// The quest-gated spawns: NPC template id -> the quest ids that reveal it, for kill targets that are not
			// quest givers.
			if (_questGatedNpcs == null || _storyKillTargets == null || _escortHiddenNpcs == null)
			{
				var maps = System.Diagnostics.Stopwatch.StartNew();
				_questGatedNpcs ??= await _npcs.GetQuestGatedNpcMap();
				_storyKillTargets ??= await _npcs.GetStoryKillTargets();
				_escortHiddenNpcs ??= await _npcs.GetEscortHiddenNpcMap();
				_logger.LogInformation("World: quest-gated / story-kill / escort-hidden NPC maps built in {Ms} ms (once per server).", maps.ElapsedMilliseconds);
			}

			// The spawn rows are the source of truth: the importer's burst-truth extraction already produced one clean
			// row per intended placement, keyed by map and exact position, so this load path renders every row
			// verbatim with no runtime deduplication. A name-or-cell collapse here silently drops legitimately distinct
			// NPCs - neutral animals of the same name (5 Hens, 4 Scorpions) and faction guards standing 1 to 4 units
			// apart (the Tarantia Noble District line, the Castle of King Conan door pairs). Genuine over-density is a
			// data bug to fix in the importer, never to hide here.
			//
			// Wire handles are minted here and never read off the row: a captured handle is session-volatile, is not
			// unique across the merged database, and would need a collision dance to stop one row silently overwriting
			// another in the handle-keyed zone. The zone's counter is unique by construction and shared with the static
			// objects, which two separate captured ranges could never guarantee against each other.
			//
			// Rows sharing a PlacementId are sightings of one patroller rather than separate NPCs - the importer says
			// so, having identified them by their harvested beat - so each placement contributes one entity, and the
			// row it renders as is drawn at random from the pool: the rows differ in exactly what retail re-rolls per
			// spawn (level, morphs, all seven equipment slots), which is what makes two spawns of one placement look
			// different. That is not the runtime deduplication forbidden above, which is about this layer inventing a
			// collapse; this renders a relationship the data declares. A row with no PlacementId stands for itself.
			var spawnRows = npcs
				.Where(n => n.PlacementId == null)
				.Concat(npcs.Where(n => n.PlacementId != null)
					.GroupBy(n => n.PlacementId!.Value)
					.Select(pool => pool.ElementAt(Random.Shared.Next(pool.Count()))))
				.ToList();

			var labelledCount = 0; // diagnostic: how many captured spawns got a non-null area label this load
			// Creatures with no harvested disposition, which are served friendly and never aggro. Counted so the gap is
			// stated at load rather than discovered in play as creatures that ignore the player.
			var undisposed = 0;
			var undisposedNames = new HashSet<string>();
			string? labelSample = null;
			var followers = new Dictionary<WorldEntity, HashSet<uint>>(); // follower -> the leader types it may bind to
			foreach (var npc in spawnRows)
			{
				// This type only exists inside a triggered spawn, so a row for it is not a creature standing in the
				// world.
				if (triggerSpawned.Contains(npc.NpcTemplateId))
					continue;

				var npcName = npc.NpcTemplate.Name;

				// Seed killable vitals from the NPC's real max health, which the importer lifts from the spawn, falling
				// back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
				// World entry is burst-less: every NPC streams in on the player's first move (interest), under the handle
				// this zone mints for it.
				var handle = zone.AllocateHandle();

				var entity = WorldEntity.ForNpc(handle, mapId, npc.Position, npc.Rotation, npcName ?? "NPC", vitals, npc.NpcTemplateId);
				entity.InstanceId = instanceId; // this NPC belongs to this copy of the map
				// Whether the mob shoots rather than swings, which is worth about a fifth of the damage. Decided here,
				// off the equipment rows this query already includes, so the combat tick reads a flag rather than a join.
				entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment);
				entity.AttackRange = NpcWeapon.AttackRange(npc.Equipment, WorldState.NpcMeleeRange); // hold a bow's distance
				// And which generic weapon combo its swings perform, off the same rows.
				entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment);
				entity.Level = npc.Level;
				// And the class-family rows it may swing instead, gated on the level just stamped.
				entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level, npc.NpcTemplate.StatCore?.ComboClass);
				// An escortee NPC vanishes once its escort quest is completed - Casilda after the beach escort - keyed
				// by template id. This is the complement of the show-while-active gate below, which stamps the quest
				// ids that reveal a gated NPC so interest streams it only to players holding one of them.
				if (_escortHiddenNpcs.TryGetValue(npc.NpcTemplateId, out var hideQuest))
					entity.HideWhenQuestCompletedId = hideQuest;

				if (_questGatedNpcs.TryGetValue(npc.NpcTemplateId, out var gateQuests))
					entity.GateQuestIds = gateQuests;

				// And, separately, whether it stays dead. A story kill target must not respawn: killing Scavenger Tuut
				// for Casilda's quest completes the goal and he should stay dead, retail solo-instancing him, so he
				// despawns after death instead of reviving, while his loot bag - the quest key - is a separate entity
				// and still drops. This is not implied by the visibility gate: a quest-only target on a repeatable
				// quest is gated and must come back.
				if (_storyKillTargets.Contains(npc.NpcTemplateId))
					entity.Route.NoRespawn = true;

				// Attach this NPC's captured route, linked by exact spawn position, replaying the captured MoveTask
				// re-addressed to this entity. The original form carries the real speed and flags, so the client walks
				// the route at the right pace; a rebuilt mode-1 path loses the speed and crawls. The cycle timer - path
				// time plus dwell - re-issues it so the route keeps looping.
				if (npc.PatrolNodes is { Count: > 0 } patrol)
				{
					var waypoints = PatrolRoute.Waypoints(patrol);
					if (waypoints.Count > 0)
					{
						entity.Route.PatrolWaypoints = waypoints;
						// An ordered route, so the tick walks it waypoint by waypoint instead of drifting to a random
						// one of its waypoints - that straight line is what sends Old Tarantia's guards through the
						// houses. A route whose ends meet is walked round and round and an open one is walked back
						// down again; either way it joins at the waypoint nearest where it spawned.
						entity.Route.WalksRoute = true;
						entity.Route.RouteLoops = waypoints.Count > 2 && Vector3.Distance(waypoints[0], waypoints[^1]) <= RouteClosedTolerance;
						entity.Route.RejoinRouteAtNearest(entity.Position);
						entity.Route.PatrolBody = MoveTaskCodec.Build(new MoveTaskIIR(handle, entity.Position, MoveTaskNodes.Patrol(patrol)));
						// A beat is walked, at the speed retail's own patrollers are observed moving at - not the chase
						// speed, and not the route's trailer scalar, which is not a ground speed; see
						// WorldState.RouteWalkSpeed for the measurement that rules it out.
						entity.Route.RouteSpeed = WorldState.RouteWalkSpeed;
						entity.Route.PatrolCycleSeconds = PatrolRoute.TotalLength(entity.Position, entity.Route.PatrolWaypoints) / entity.Route.RouteSpeed + PatrolDwellSeconds;
						entity.Route.ArmPatrolCycle();
					}
				}

				// Disposition drives both the overhead name colour and whether this NPC aggros. It is the captured
				// retail cCategory, and the only correct hostility source: community is faction membership rather than
				// hostility, since community 1 is the civilian default shared by citizens, vendors and guards, so
				// testing on it aggros every townsperson. A null disposition means friendly, the client's green default
				// for an NPC it has been told nothing about, and only cCategory 1 aggros.
				//
				// That default is a real gap rather than a neutral choice, so it is counted rather than passed over: a
				// creature nobody ever polled and that no capture caught fighting stands in the world green and
				// passive. The count is reported per zone below, because a world quietly missing its monsters looks
				// exactly like a world with no monsters in it.
				//
				// The gap is the residue of four tiers (see NpcDispositionTiers): the creature's own poll, being caught
				// attacking, a faction roll-up, then the static mob rule. That takes 645 placements over 154 types down
				// to 102 over 61, and what is left is Refugees, Commoners, a Target Dummy and named story NPCs - the
				// population the friendly default is right for.
				entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
				entity.Hostile = entity.Disposition == WorldEntity.DispositionHostile;
				// Hostile-coloured is not hostile-behaved. An armed humanoid engages; an unarmed one is a civilian
				// retail happens to colour red - the Town Crier, the Tarantia nobles, the Dockworkers - and anything
				// non-humanoid needs no weapon to mean it. Morph rows are the humanoid signal.
				entity.Aggressive = NpcWeapon.Initiates(npc.Equipment, npc.Morphs.Count > 0);
				if (npc.NpcTemplate.StatCore?.Disposition == null)
				{
					undisposed++;
					undisposedNames.Add(npc.NpcTemplate.Name ?? $"#{npc.NpcTemplateId}");
				}

				// The creature's death animation: the type's harvested death behaviour id, else the humanoid default.
				// The kill path sends it in the death BccIIR, so demons and beasts play their own death.
				if (npc.NpcTemplate.DeathBehaviorId is { } deathBehavior)
					entity.DeathBehaviorId = deathBehavior;
				entity.DeathActivationTime = npc.NpcTemplate.DeathActivationTime;

				// The loot container this type drops on death - a bag by default, a bigger chest for bosses - which the
				// kill-drop path picks by its container variant.
				entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId;
				// The Khitai faction this creature belongs to, so a kill can pay reputation. Null for every base-game
				// NPC, because the ids are the expansion's.
				entity.FactionId = npc.NpcTemplate.StatCore?.FactionId;
				// What this creature's kill is worth to the alternate-advancement pool is a property of its template:
				// the named tier pays roughly double and one flagged tier about 15% more - see AlternateAdvancementRules.
				entity.NpcType = npc.NpcTemplate.StatCore?.NpcType;
				entity.NpcFlags = npc.NpcTemplate.StatCore?.NpcFlags;

				// Keep the NPC's FA015807 spawn body, addressed to the chosen handle, so interest and respawn can
				// re-render it for nearby players. It is built whole from typed columns with this zone's load-time
				// playfield token baked in, and the send re-stamps the entering player's live token, which is not known
				// until that player enters.
				//
				// The area label is not baked into the spawn body: the client ignores the body's free-text name for an
				// NPC's floating nameplate, using the template's LDB name instead, so the label is pushed as a separate
				// name record after the spawn, gated on the .togglenpcgroup toggle. That record's name is hard-capped
				// at 31 bytes by the client, so it carries the label alone - group and tier, about 24 characters - which
				// is what a labelling pass needs. It is stashed here unconditionally so the toggle is instant.
				if (SpawnAreaLabels.AreaLabelFor(spawnAreas, npc) is { } areaLabel)
				{
					entity.OverheadNameOverride = areaLabel;
					labelledCount++;
					labelSample ??= areaLabel;
				}

				// A creature that walks a captured beat spawns in movement mode 1 rather than the type's stored mode.
				// The mode is what the client picks its locomotion speed by - the path task advances by a per-mode
				// speed field - and it is per-spawn state rather than type identity: retail sends 1 and 2 for the same
				// creature. Taking the type's value gives the Old Tarantia patrollers mode 2, and they run their beats.
				var spawnMovementMode = npc.PatrolNodes is { Count: > 0 } ? SpawnPostIirs.MovementModeWalk : (uint?)null;
				var bodyZone = _sim.World.ZoneFor(mapId, instanceId);
				var token = bodyZone.PlayfieldToken;
				// An epic copy serves the normal population wearing the elite decorations: the one spawn-level change
				// beside health the captures show, and health is deliberately not retuned yet.
				var epicNormalset = bodyZone.IsEpic ? EpicDecorations.NormalsetFor(npc.NpcTemplate.StatCore) : null;
				entity.SpawnBody = NpcSpawnBuilder.BuildLive(npc, handle, token, spawnMovementMode, normalsetOverride: epicNormalset);

				// A quest escortee gets a second body for after its rescue, built here rather than at the rescue
				// because this is where the row is in hand. What differs is the pose: AnimIdleId is the pose the
				// creature holds, and Casilda's is 156196, her chained struggle, unique across all 1,208 templates
				// where the generic idle is shared by 1,008. It ships in the spawn record, so every stream-in
				// re-asserts the chains no matter what behaviour pose follows.
				//
				// The movement mode is deliberately left as the type's - mode 2, run - and not forced to walk. A
				// follower is stepped by the server at the chase speed while the client animates at the per-mode speed
				// from its own template data, so walk mode widens the gap that the segment broadcast then closes by
				// snapping the follower forward.
				if (entity.IsQuestEscortee)
					entity.FreedSpawnBody = NpcSpawnBuilder.BuildLive(npc, handle, token, null, GenericIdleAnimationId, OrdinaryBccState, epicNormalset);

				// The spells this NPC type casts, read from the template's own rows, which are loaded with the spawn.
				// Empty for a non-caster.
				if (npc.NpcTemplate.Spells is { Count: > 0 } typeSpells)
				{
					foreach (var s in typeSpells)
						entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

					// The ability a physical hit names in its health record. This is the only place it is assigned, and
					// without it the guard in the NPC combat path never fires, so a melee swing puts no health record
					// on the wire at all. The base id is the spell row's wire id shifted right by 8, and this is where
					// that row is in hand.
					//
					// The first row wins: a creature's rows are its abilities and retail names one of them per physical
					// hit (Ripper Volley, Rend, Butcher), so which one is a fidelity question where having none at all
					// is a missing record.
					entity.AttackSpellBaseId = typeSpells.First().SpellId >> 8;
				}

				// The bark lines for this NPC type; an NPC with no captured group stays silent. The entity caches the
				// bark values and the chat tier renders them, so the world model never holds pre-built wire text.
				//
				// They are split by cue rather than pooled, because a bark's channel says what makes the NPC say it:
				// ambient chatter runs on the tick's clock, a greeting fires when a player is detected arriving, and an
				// event line belongs to the encounter script's own trigger. Pooling them puts a greeting on the ambient
				// clock, so the NPC greets an empty street.
				if (npc.NpcTemplate.Chatter is { Count: > 0 } chatter)
				{
					var barks = chatter.Select(c => NpcChatterColumns.ToBark(c)).ToLookup(NpcChatterBarks.TriggerOf);
					entity.Presentation.ChatterLines = barks[BarkTrigger.Ambient].ToArray();
					entity.Presentation.GreetingLines = barks[BarkTrigger.Detection].ToArray();
					entity.Presentation.EncounterLines = barks[BarkTrigger.EncounterScript].ToArray();
				}

				// The ambient idle animations this creature performs while standing about, weighted as retail was
				// captured playing them. A type with none stands still, which is the honest gap and the reason this is
				// not defaulted to the type's single AnimIdleId: that is the pose it holds, not something it does.
				if (npc.NpcTemplate.AmbientAnimations is { Count: > 0 } ambient)
				{
					entity.Presentation.AmbientAnimations = ambient
						.Select(a => new AmbientAnimation(a.AnimationId, a.DurationCentiseconds, a.Weight))
						.ToArray();
					// Staggered, so a street's worth of citizens do not all start an animation on the same tick.
					entity.Presentation.ArmAmbientAnimation(Random.Shared.NextDouble() * WorldState.AmbientAnimationPauseMedianSeconds * 4);
				}

				// A captured spawn linked to an ambient area is a member of that area's pool: it renders with its
				// captured appearance, but the pool owns its respawn - it fades on death and the pool replaces it - and
				// it counts toward the pool's MaxCount, so the pool only tops up the deficit and the two populations
				// never overlap.
				if (npc.NpcSpawnAreaId is { } linkedAreaId)
				{
					entity.SpawnAreaId = linkedAreaId;
					entity.Route.NoRespawn = true;
				}

				// A placement whose type has a scripted encounter on this map becomes a lure: it watches for an
				// approaching player and swaps itself for its boss. Every placement of the type is armed, because the
				// definition is keyed by map and type rather than by one spot.
				entity.Presentation.ArmEncounter(encounters.GetValueOrDefault(npc.NpcTemplateId));

				// The creature types this one walks with, kept until every entity is in the zone: a follower is bound
				// to a live leader, so the binding cannot be made while the leader may not be loaded yet.
				if (npc.NpcTemplate.Follows is { Count: > 0 } follows)
					followers[entity] = follows.Select(f => f.LeaderTemplateId).ToHashSet();

				zone.Add(entity);
			}

			BindFormationFollowers(zone, followers);

			await _ambient.LoadAsync(mapId, instanceId, zone);

			_logger.LogInformation("World: loaded {Count} NPC(s) into map {Map} instance 0x{Instance:X8} ({Raw} DB rows, no runtime dedup) in {Ms} ms.", zone.Count, mapId, instanceId, npcs.Length, clock.ElapsedMilliseconds);
			if (undisposed > 0)
			{
				// A warning rather than information: these creatures are served green and passive because nothing in
				// the corpus said otherwise, which in play reads as the monsters being broken.
				_logger.LogWarning("World: {Count} NPC(s) on map {Map} have NO harvested disposition and are served FRIENDLY - they will not aggro: {Names}.", undisposed, mapId,
					string.Join(", ", undisposedNames.Order().Take(8)) + (undisposedNames.Count > 8
						? $", +{undisposedNames.Count - 8} more"
						: ""));
			}
			_logger.LogDebug("World: #363 {Labelled}/{Total} captured spawns have a spawn-area label on map {Map} (.togglenpcgroup to show; sample: {Sample}).", labelledCount, npcs.Length, mapId,
				labelSample ?? "<none>");
		}

		/// <summary>
		///     Loads a map's static world objects into its zone once: the world-object twin of
		///     <see cref="EnsureZoneNpcs" />.
		/// </summary>
		/// <remarks>
		///     One load per zone is one place to get the query right. Re-querying per world entry instead spreads that
		///     across the entry path, where a missing include has already shipped invisible doors, vines and
		///     destructibles.
		/// </remarks>
		public async Task EnsureZoneWorldObjects(uint mapId, uint instanceId)
		{
			if (!_objectLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var zone = _sim.World.ZoneFor(mapId, instanceId);
			zone.WorldObjects =
			[
				.. (await _world.GetWorldObjects(mapId)).Select(row => new LiveWorldObject(zone.AllocateHandle(), row)
				{
					// A harvest node starts full on every zone load. Retail's nodes are shared across the whole
					// playfield and regrow, and neither the sharing nor a respawn cadence is measured, so a fresh zone
					// giving a full node is the closest honest model and means a harvested node comes back rather than
					// being gone for good. Null for everything that is not a node.
					Health = HarvestCatalog.IsHarvestable(row.DisplayCategory, row.DisplayId)
						? HarvestCatalog.NodeMaxHealth
						: null
				})
			];
		}

		/// <summary>Why a class-specific destination may not be enterable yet, or null when it is.</summary>
		/// <remarks>
		///     A class-specific playfield - <c>Map.ClassSpecific</c>, the city armories and The Reliquary - holds a
		///     different NPC set per class, and only the classes a capture actually walked into are imported.
		///     Teleporting a class with no set would drop the player into an empty room, and on a map with no captured
		///     world objects either, one with no door back out. The zone-line therefore reports the gap in-game instead
		///     of teleporting, the same rule an un-imported vendor takes.
		/// </remarks>
		public async Task<string?> DescribeMissingClassImport(uint mapId, ConanClass forClass)
		{
			var map = await _world.GetMap(mapId);
			if (map is not { ClassSpecific: true })
				return null;

			var imported = await _npcs.GetImportedSpawnClasses(mapId);
			if (imported.Contains(forClass))
				return null;

			var have = imported.Length > 0
				? $"imported so far: {string.Join(", ", imported.Select(c => c.ToString()).OrderBy(n => n))}"
				: "no class imported yet";
			var name = string.IsNullOrEmpty(map.Name)
				? $"Map {mapId}"
				: map.Name;
			return $"{name} is class-specific and has no import for {forClass} - {have}.";
		}

		/// <summary>
		///     Binds each formation follower to the nearest live leader of a type it walks with.
		/// </summary>
		/// <remarks>
		///     Retail expresses this as a move task whose task names the other creature's handle, so it is a
		///     per-placement binding built out of a per-type rule - which is what the database holds, and what cannot be
		///     resolved until every entity is in the zone.
		///     <para>
		///         The leader is the nearest one within <see cref="FormationBindRange" />: a map carries several groups
		///         of the same two types - five separate Nemedian Deathstalker groups in one capture, four Ginger and
		///         Scruffy pairs across three - so binding to any leader of that type would march a follower across the
		///         zone to the wrong one. A leader that is itself a follower is allowed, and a follower that finds no
		///         leader in range keeps its own behaviour.
		///     </para>
		/// </remarks>
		private void BindFormationFollowers(Zone zone, Dictionary<WorldEntity, HashSet<uint>> followers)
		{
			if (followers.Count == 0)
				return;

			var bound = 0;
			foreach (var (follower, leaderTemplates) in followers)
			{
				WorldEntity? nearest = null;
				var nearestDist = FormationBindRange * FormationBindRange;
				foreach (var candidate in zone.Entities)
				{
					if (candidate == follower || candidate.IsPlayer || !leaderTemplates.Contains(candidate.NpcTemplateId))
						continue;

					var dx = candidate.Position.X - follower.Position.X;
					var dz = candidate.Position.Z - follower.Position.Z;
					var dist = dx * dx + dz * dz;
					if (dist >= nearestDist)
						continue;

					nearestDist = dist;
					nearest = candidate;
				}

				if (nearest == null)
					continue;

				follower.FollowTargetHandle = nearest.Handle;
				bound++;
			}

			_logger.LogDebug("World: #741 bound {Bound} of {Total} formation follower(s) to a leader within {Range}u.", bound, followers.Count, FormationBindRange);
		}
	}
}
