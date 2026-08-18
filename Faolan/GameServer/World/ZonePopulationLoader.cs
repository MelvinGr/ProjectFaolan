using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
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
	// Fills a zone with its POPULATION, once per (map, instance): the captured NPC spawns and the static world objects
	// a map ships, read out of the DB and turned into live `WorldEntity` / `LiveWorldObject` instances in the world
	// model. Also answers why a class-specific map looks empty (`DescribeMissingClassImport`).
	//
	// It writes nothing to the wire - that is the whole point of it being its own class (#826). Loading happens on the
	// FIRST entry to an instance, before anyone can see it; the streaming that follows is interest-driven and belongs
	// to WorldBroadcaster, which is also where the per-viewer questions live. The three caches here
	// (quest-gated NPCs, story-kill targets, escort-hidden NPCs) are read-once catalog maps, not per-player state.
	//
	// ⚠️ The load is idempotent by the `TryAdd` gates, and each INSTANCE gets its own population (#268) - two copies of
	// a playfield must not share entities, so the key is (mapId, instanceId) and never mapId alone.
	public sealed class ZonePopulationLoader
	{		// #703: how close a route's last waypoint must come back to its first for the route to count as CLOSED (walked
		// round and round) rather than open (walked back down again). 494 of the 1,318 stored routes close this way.
		private const float RouteClosedTolerance = 5f;

		private const double PatrolDwellSeconds = 2.0;

		// #363 live ambient spawner: the capture-INDEPENDENT open-world
		// population, managed as spawn-group POOLS. Each RESOLVED NpcSpawnArea becomes an AmbientPool that keeps ~MaxCount
		// of its creature alive within the group's area: the initial fill spawns one per RDB point, each member roams the
		// group's points (its PatrolWaypoints -> the #266 wander) and fades on death (NoRespawn); MaintainAmbientPools
		// then respawns the deficit at a free point. Built WHOLE from the creature TYPE (TryBuildFromType, the pet path).
		// Worked example - Tortage beach (4010): the four T_Crocodile-* areas become four Crocodile pools (8/7/8/11)
		// that each hold that many crocs roaming their stretch of shore, refilled as the player kills them.
		// #741: bind each formation FOLLOWER to the nearest live leader of a type it walks with. Retail expresses this
		// as a MoveTaskIIR whose task names the other creature's handle, so it is a per-PLACEMENT binding built out of
		// a per-TYPE rule - which is exactly what the DB holds (NpcFollowPair) and what cannot be resolved until every
		// entity is in the zone.
		//
		// NEAREST, and within FormationBindRange: a map carries several groups of the same two types (five separate
		// `Nemedian Deathstalker` groups in one capture, four `Ginger`/`Scruffy` pairs across three), so binding to
		// "any leader of that type" would march a follower across the zone to the wrong one. The range is ours, not
		// retail's - the corpus states the pairing, never a maximum distance - so it is set at the leash territory a
		// patroller already works in rather than invented smaller.
		//
		// A leader that is itself a follower is fine (nothing in the corpus chains, but nothing forbids it); a
		// follower that finds no leader in range simply keeps its own behaviour.
		// How far a formation follower will look for its leader at zone load. OURS, not retail's - the corpus states
		// which creature walks with which and never a maximum distance - so it is the leash territory a patroller
		// already works in rather than a smaller invented number.
		// The ordinary standing idle - shared by 1,008 of the 1,208 templates, against a story pose like Casilda's
		// chained 156196 which exactly one template carries. Used as the freed escortee's held pose (#827).
		private const uint GenericIdleAnimationId = 42024;

		// The ordinary BEHAVIOUR-CONTROL state - 1,186 of the templates that declare one carry it, against Casilda's
		// captive 80. The client calls it "BCC State" (CharacterStatId 0x14) and it selects the behaviour tree, so a
		// creature left in a story state never runs its locomotion: she slid instead of walking (#827).
		private const uint OrdinaryBccState = 1;

		private const float FormationBindRange = WorldState.NpcLeashRange;

		private readonly AmbientPoolService _ambient;

		private readonly ComboService _combos;

		private readonly IDatabaseRepository _database;

		private readonly ILogger _logger;

		private readonly WorldSimulation _sim;

		// (mapId, instanceId) zones whose static WORLD OBJECTS have been loaded.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _objectLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// #262 escort NPCs hidden until their quest is active: TEMPLATE id -> the quest that reveals it.
		private Dictionary<uint, uint> _escortHiddenNpcs = null!;

		// (mapId, instanceId) zones whose NPCs have been loaded into the world model (once per instance, on first entry
		// to that copy - each instance gets its own NPC population) (#268).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _npcLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// Quest-gated NPC TEMPLATE id -> the quest ids that reveal it (#261, re-keyed off the name in #521). Cached once;
		// an NPC in this map is streamed only to players holding one of its quests active (see WorldEntity.GateQuestIds
		// / IsVisibleTo).
		private Dictionary<uint, uint[]> _questGatedNpcs = null!;

		// #708: the NPCs a quest kills ONCE. A separate set from _questGatedNpcs on purpose - visibility and respawn
		// are independent, and one column answering both could not express a quest-only target that must come back.
		private HashSet<uint> _storyKillTargets = null!;

		public ZonePopulationLoader(WorldSimulation sim, IDatabaseRepository database, ComboService combos, AmbientPoolService ambient, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_combos = combos;
			_ambient = ambient;
			_logger = logger;
		}

		// Loads a map's NPCs from NpcSpawns into the world model ONCE (on first entry), so the server has an
		// authoritative shared view of the map's NPCs - the model fix at the heart of the roadmap (NPCs are otherwise
		// only per-player burst replays the server doesn't track). The tick/AI/combat will drive these; clients still
		// render them from the burst for now, so this is model bookkeeping (no wire change).
		//
		// `forClass` is the class of the character this copy is being populated for. It only matters on a
		// class-specific map (Map.ClassSpecific - the city armories and The Reliquary), where the playfield holds a
		// different NPC set per
		// class; such a map is necessarily Solo, so the copy has exactly one owner and one class. Everywhere else every
		// spawn is class-agnostic and this is ignored.
		public async Task EnsureZoneNpcs(uint mapId, uint instanceId, ConanClass forClass)
		{
			if (!_npcLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var npcs = await _database.GetNpcSpawns(mapId, forClass);

			// #502: the map's scripted encounters, by LURE type - one query per zone load rather than per placement.
			var encounters = (await _database.GetNpcEncounters(mapId)).ToDictionary(e => e.LureNpcTemplateId);

			// #528: the map's TRIGGER-SPAWNED types - a quest ambusher and an encounter BOSS alike. Neither stands in
			// the world (one appears when a goal advances, the other when a lure swaps), so a captured "placement" of
			// one is a harvest of the trigger FIRING, and it is skipped below. The importer already excludes them from
			// the burst-truth set (ImportOps.BuildBurstTruthSpawns, alongside the summoned pets), so on a current
			// database this finds nothing; it is the second net, for a database built before that rule.
			var triggerSpawned = (await _database.GetNpcAmbushes(mapId)).Select(a => a.NpcTemplateId)
				.Concat(encounters.Values.Select(e => e.BossNpcTemplateId))
				.ToHashSet();
			var zone = _sim.World.ZoneFor(mapId, instanceId);

			// #363 dev labels: the map's RDB spawn areas, so each NPC carries its area label ready for the live
			// `.togglenpcgroup` toggle. Computed at load (a cheap query, stored as a string) so the toggle is instant and
			// also catches NPCs that stream in while it's on - the toggle only gates whether the label is SENT.
			var spawnAreas = await _database.GetNpcSpawnAreas(mapId);

			// #261 quest-gated spawns: NPC template id -> the quest ids that reveal it (kill-target NPCs that aren't givers).
			_questGatedNpcs ??= await _database.GetQuestGatedNpcMap();
			_storyKillTargets ??= await _database.GetStoryKillTargets();
			_escortHiddenNpcs ??= await _database.GetEscortHiddenNpcMap();

			// NpcSpawns is the SOURCE OF TRUTH: the importer's burst-truth extraction (#266, BuildBurstTruthSpawns)
			// already produced one clean row per intended placement (keyed by MapId + exact XYZ), so this load path
			// renders EVERY row verbatim - NO runtime dedup. The old name/cell collapse silently dropped legitimately
			// distinct NPCs: neutral animals of the same name (5 Hens, 4 Scorpions), and faction guards standing 1-4
			// units apart (the Tarantia Noble District line and the Castle of King Conan door pairs). Any genuine
			// over-density is a data bug fixed in the importer, never hidden here.
			//
			// Wire handles are MINTED here (Zone.AllocateHandle), never read off the row. They used to be the spawn's
			// CapturedHandle - whichever handle the capture the importer last saw it in happened to assign - which is a
			// session-volatile value serving as a persistence key, and it was not even unique across the merged DB, so
			// this loop needed a collision dance to stop one row silently overwriting another in the handle-keyed zone.
			// The zone's counter is unique by construction and shared with the static objects, which the two separate
			// captured ranges never guaranteed against each other.
			// #713: rows sharing a PlacementId are SIGHTINGS of one patroller, not separate NPCs - the importer says
			// so, having identified them by their harvested beat. One entity per placement, and the row it renders as
			// is drawn from the others: they differ in exactly what retail re-rolls per spawn (level, morphs, all 7
			// equipment slots), so the pool is what makes two spawns of one placement look different.
			//
			// This is NOT the runtime dedup the note above forbids. That rule is about this layer inventing a collapse
			// (the old name/cell heuristic, which silently dropped legitimately distinct NPCs); this renders a
			// relationship the DATA declares. A row with no PlacementId stands for itself and is rendered verbatim;
			// each pool contributes ONE row, drawn at random, so the beat gets one NPC wearing one of its rolls.
			var spawnRows = npcs
				.Where(n => n.PlacementId == null)
				.Concat(npcs.Where(n => n.PlacementId != null)
					.GroupBy(n => n.PlacementId!.Value)
					.Select(pool => pool.ElementAt(Random.Shared.Next(pool.Count()))))
				.ToList();

			var labelledCount = 0; // #363 diagnostic: how many captured spawns got a non-null area label this load
			// #840: creatures with NO harvested disposition, which are served FRIENDLY and never aggro. Counted so the
			// gap is stated at load instead of being discovered in play as "the crocodiles ignore me".
			var undisposed = 0;
			var undisposedNames = new HashSet<string>();
			string? labelSample = null;
			var followers = new Dictionary<WorldEntity, HashSet<uint>>(); // #741: follower -> the leader TYPES it may bind to
			foreach (var npc in spawnRows)
			{
				// #528/#501: this type only exists inside a triggered spawn, so a row for it is not a creature standing
				// in the world.
				if (triggerSpawned.Contains(npc.NpcTemplateId))
					continue;

				var npcName = npc.NpcTemplate.Name;

				// Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
				// importer), falling back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
				// World entry is burst-less: every NPC streams in on the player's first move (interest), under the handle
				// this zone mints for it.
				var handle = zone.AllocateHandle();

				var entity = WorldEntity.ForNpc(handle, mapId, npc.Position, npc.Rotation, npcName ?? "NPC", vitals, npc.NpcTemplateId);
				entity.InstanceId = instanceId; // this NPC belongs to THIS copy of the map (#268)
				// #671: a mob that SHOOTS deals about a fifth of what one that swings does. Decided here, off the
				// equipment rows this query already Includes, so the combat tick reads a flag rather than a join.
				entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment);
				entity.AttackRange = NpcWeapon.AttackRange(npc.Equipment, WorldState.NpcMeleeRange); // #838: hold a bow's distance
				// #623: and which `combo_npc_generic_*` its swings perform, off the same rows.
				entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment);
				entity.Level = npc.Level;
				// #675: and the class-family rows it may swing instead, level-gated on the level just stamped.
				entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level, npc.NpcTemplate.StatCore?.ComboClass);
				// #261: if this NPC's spawn is quest-gated, stamp the revealing quest ids so interest only streams it to
				// players holding one of them (e.g. Scavenger Tuut while you carry Casilda's kill quest).
				// #262: an escortee NPC vanishes once its escort quest is COMPLETED (e.g. Casilda after the beach escort);
				// keyed by template id, complement of the "show while active" gate below.
				if (_escortHiddenNpcs.TryGetValue(npc.NpcTemplateId, out var hideQuest))
					entity.HideWhenQuestCompletedId = hideQuest;

				if (_questGatedNpcs.TryGetValue(npc.NpcTemplateId, out var gateQuests))
					entity.GateQuestIds = gateQuests;

				// …and, SEPARATELY, whether it stays dead (#708). A story kill target must not respawn at all -
				// killing Scavenger Tuut for Casilda's quest completes the goal and he should stay dead (retail
				// solo-instances him), so he despawns after death instead of reviving; his loot bag (the quest key) is
				// a separate entity and still drops. ⚠️ Not implied by the gate: a quest-only target on a REPEATABLE
				// quest is gated and must come back, which is the case the welded column could not express.
				if (_storyKillTargets.Contains(npc.NpcTemplateId))
					entity.NoRespawn = true;

				// #266 patrol: attach this NPC's captured route (linked by exact spawn position). REPLAY the captured
				// MoveTask re-addressed to this entity (handle + from = its spawn) - the original form carries the real
				// speed/flags, so the client walks the route at the right pace (a rebuilt mode-1 path lost the speed and
				// run-crawled). The cycle timer (path time + dwell) re-issues it so it keeps looping.
				if (npc.PatrolNodes is { Count: > 0 } patrol)
				{
					var waypoints = PatrolRoute.Waypoints(patrol);
					if (waypoints.Count > 0)
					{
						entity.PatrolWaypoints = waypoints;
						// #703: an ORDERED route, so the tick WALKS it waypoint by waypoint instead of drifting to a
						// random one of its waypoints - that straight line is what sent Old Tarantia's guards through
						// the houses. A route whose ends meet is walked round and round; an open one is walked back
						// down again; either way it joins at the waypoint nearest where it spawned.
						entity.WalksRoute = true;
						entity.RouteLoops = waypoints.Count > 2 && Vector3.Distance(waypoints[0], waypoints[^1]) <= RouteClosedTolerance;
						entity.RejoinRouteAtNearest();
						entity.PatrolBody = MoveTaskCodec.Build(new MoveTaskIIR(handle, entity.Position, MoveTaskNodes.Patrol(patrol)));
						// #714: a beat is WALKED, at the speed retail's own patrollers are observed moving at - not at the
						// chase speed, and not at the route's trailer scalar (which is not a ground speed; see
						// WorldState.RouteWalkSpeed for the measurement that rules it out).
						entity.RouteSpeed = WorldState.RouteWalkSpeed;
						entity.PatrolCycleSeconds = PatrolRoute.TotalLength(entity.Position, entity.PatrolWaypoints) / entity.RouteSpeed + PatrolDwellSeconds;
						entity.ArmPatrolCycle();
					}
				}

				// Disposition drives BOTH the overhead name colour and whether this NPC aggros. It is the captured
				// retail cCategory (NpcStatCore.Disposition, #256/aggro) - the SOLE correct hostility source. Community
				// is faction MEMBERSHIP, NOT hostility: community 1 is the civilian/default faction shared by Citizens,
				// vendors and guards, so the old `community > 0` test wrongly aggroed every townsperson. NULL disposition
				// -> friendly (the client's green default for an un-told-about NPC). Only cCategory 1 (hostile) aggros.
				//
				// ⚠️ That default is a real gap, not a neutral choice, and it is COUNTED rather than passed over (#840):
				// a creature nobody ever polled and that no capture caught fighting stands in the world green and
				// passive. The beach crocodiles were the reported case. The count is reported per zone below, because a
				// world that is quietly missing its monsters looks exactly like a world with no monsters in it.
				//
				// The gap is now the RESIDUE of four tiers rather than of one (#841, `NpcDispositionTiers`): own poll,
				// caught attacking, faction roll-up, then the static mob rule. 645 placements over 154 types came down
				// to 102 over 61 - and what is left is Refugees, Commoners, a Target Dummy and named story NPCs, which
				// is the population the friendly default is actually right for.
				entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
				entity.Hostile = entity.Disposition == WorldEntity.DispositionHostile;
				// #822: hostile-COLOURED is not hostile-BEHAVED. An armed humanoid engages; an unarmed one is a
				// civilian retail happens to colour red (the Town Crier, the Tarantia nobles, the Dockworkers), and
				// anything non-humanoid needs no weapon to mean it. Morph rows are the humanoid signal.
				entity.Aggressive = NpcWeapon.Initiates(npc.Equipment, npc.Morphs.Count > 0);
				if (npc.NpcTemplate.StatCore?.Disposition == null)
				{
					undisposed++;
					undisposedNames.Add(npc.NpcTemplate.Name ?? $"#{npc.NpcTemplateId}");
				}

				// The creature's death animation (#362): use the type's harvested death behaviorId, else the humanoid
				// default. Sent in the death BccIIR by CombatService.OnNpcKilled so demons/beasts play their own death.
				if (npc.NpcTemplate.DeathBehaviorId is { } deathBehavior)
					entity.DeathBehaviorId = deathBehavior;
				entity.DeathActivationTime = npc.NpcTemplate.DeathActivationTime;

				// The loot container this type drops on death (Bag by default, a bigger chest for bosses) - picked by
				// the kill-drop path (SpawnLootBagAt) via its LootContainerTemplate variant.
				entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId;
				// #310: the Khitai faction this creature belongs to, so a kill can pay reputation. Null for every
				// base-game NPC - the ids are the expansion's.
				entity.FactionId = npc.NpcTemplate.StatCore?.FactionId;

				// Keep the NPC's FA015807 spawn body (addressed to the chosen handle) so interest/respawn can re-render
				// it for nearby players (#244): built WHOLE from typed columns (the captured Data blob was retired) with
				// this zone's load-time playfield token baked in. SendWorldSpawn re-stamps the entering player's LIVE
				// token per-send (SpawnTransformPatch), since that token isn't known until the player enters.
				// #363 dev label: the client ignores the spawn-body free `Name` for an NPC's floating nameplate (it uses
				// the template_id LDB name), so we DON'T bake the label into the spawn body - we push the AREA LABEL via a
				// SetNameIIR after the spawn (SendWorldSpawn), gated on the .togglenpcgroup toggle. The client hard-caps a
				// SetNameIIR name at 31 bytes, so we show the label alone (group | tier xN, ~24 chars) - the group name is
				// what a labeling pass needs. Stashed here unconditionally so the toggle is instant.
				if (SpawnAreaLabels.AreaLabelFor(spawnAreas, npc) is { } areaLabel)
				{
					entity.OverheadNameOverride = areaLabel;
					labelledCount++;
					labelSample ??= areaLabel;
				}

				// #714: a creature that WALKS a captured beat spawns in movement mode 1, not the type's stored one.
				// The mode is what the client picks its locomotion speed by (PathMoveTask advances by a per-mode speed
				// field), and it is per-spawn STATE rather than type identity - retail sends 1 and 2 for the same
				// creature. Ours were locked to whatever value the type happened to be lifted with, which for the
				// Old Tarantia patrollers is 2, so they ran their beats.
				var spawnMovementMode = npc.PatrolNodes is { Count: > 0 } ? SpawnPostIirs.MovementModeWalk : (uint?)null;
				var token = _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken;
				entity.SpawnBody = NpcSpawnBuilder.BuildLive(npc, handle, token, spawnMovementMode);

				// #827: a quest ESCORTEE gets a second body for after its rescue. Two things differ, and both were
				// reported in-client against Casilda:
				//
				//   * the POSE. `AnimIdleId` is the pose the creature HOLDS, and hers is 156196 - her chained struggle,
				//     unique across all 1,208 templates where the generic idle 42024 is shared by 1,008. It ships in
				//     the spawn record, so every stream-in re-asserted the chains no matter what BCC pose followed;
				//     she stood up the moment the record carried the ordinary idle.
				// Built here rather than at the rescue because this is where the row is in hand.
				//
				// ⚠️ The MODE is deliberately left as the type's (mode 2, run) and is NOT forced to walk. Forcing walk
				// was tried and made things worse: a follower is stepped by the server at `NpcMoveSpeed` (7 u/s, the
				// chase constant) while the client animates at the per-mode speed from ITS OWN template data (#714), so
				// walk mode widened the gap the 6-unit segment broadcast then closes by snapping her forward. The
				// remaining slide is that same server-step vs client-speed mismatch and is NOT fixed here - see #829.
				if (entity.IsQuestEscortee)
					entity.FreedSpawnBody = NpcSpawnBuilder.BuildLive(npc, handle, token, null, GenericIdleAnimationId, OrdinaryBccState);

				// Caster NPCs (#232): the spells this NPC type casts, read from the #244 type FK (npc.NpcTemplate.Spells, loaded
				// with the spawn) - this replaces the fragile NpcName string join. Empty for non-casters.
				if (npc.NpcTemplate.Spells is { Count: > 0 } typeSpells)
				{
					foreach (var s in typeSpells)
						entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));
				}

				// Bark lines for this NPC type (per-NPC; empty for NPCs without a captured group -> silent). The entity
				// caches the bark VALUES; the chat tier renders them (#539), so the world model never holds pre-built
				// wire text - the same reason wave 6 took OriginalData off the snapshot sub-class.
				//
				// #694: split by CUE, not pooled. A bark's channel says what makes the NPC say it - ambient chatter runs
				// on the tick's clock, a `sendtell` greeting fires when a player is DETECTED arriving, and an `event`
				// line belongs to the encounter script's own trigger (#502). Pooling them put the crier's greeting and
				// the Damsel's cry for help on the ambient clock, which is why the crier greeted an empty street and the
				// Damsel called for help twice.
				if (npc.NpcTemplate.Chatter is { Count: > 0 } chatter)
				{
					var barks = chatter.Select(c => NpcChatterColumns.ToBark(c)).ToLookup(NpcChatterBarks.TriggerOf);
					entity.ChatterLines = barks[BarkTrigger.Ambient].ToArray();
					entity.GreetingLines = barks[BarkTrigger.Detection].ToArray();
					entity.EncounterLines = barks[BarkTrigger.EncounterScript].ToArray();
				}

				// #766: the ambient idle ANIMATIONS this creature performs while standing about, weighted as retail was
				// captured playing them. A type with none stands still - the honest gap, and the reason this is not
				// defaulted to the type's single `AnimIdleId` (that is the pose it holds, not something it does).
				if (npc.NpcTemplate.AmbientAnimations is { Count: > 0 } ambient)
				{
					entity.AmbientAnimations = ambient
						.Select(a => new AmbientAnimation(a.AnimationId, a.DurationCentiseconds, a.Weight))
						.ToArray();
					// Staggered, so a street's worth of citizens do not all start an animation on the same tick.
					entity.ArmAmbientAnimation(Random.Shared.NextDouble() * WorldState.AmbientAnimationPauseMedianSeconds * 4);
				}

				// #363: a captured spawn LINKED to an ambient area is a member of that area's POOL (the softer alternative
				// to graduating/deleting it). It renders with its captured appearance but the POOL owns its respawn (fades
				// on death, replaced by the pool), and it counts toward MaxCount so the pool only tops up the DEFICIT -
				// no captured/ambient overlap.
				if (npc.NpcSpawnAreaId is { } linkedAreaId)
				{
					entity.SpawnAreaId = linkedAreaId;
					entity.NoRespawn = true;
				}

				// #502: a placement whose TYPE has a scripted encounter on this map becomes a LURE - it watches for an
				// approaching player and swaps itself for its boss. Every placement of the type is armed, since the
				// definition is keyed by (map, type) rather than by one spot.
				entity.ArmEncounter(encounters.GetValueOrDefault(npc.NpcTemplateId));

				// #741: the creature TYPES this one walks with, kept until every entity is in the zone - a follower is
				// bound to a LIVE leader, so the binding cannot be made while the leader may not be loaded yet.
				if (npc.NpcTemplate.Follows is { Count: > 0 } follows)
					followers[entity] = follows.Select(f => f.LeaderTemplateId).ToHashSet();

				zone.Add(entity);
			}

			BindFormationFollowers(zone, followers);

			await _ambient.LoadAsync(mapId, instanceId, zone);

			_logger.LogInformation("World: loaded {Count} NPC(s) into map {Map} instance 0x{Instance:X8} ({Raw} DB rows, no runtime dedup).", zone.Count, mapId, instanceId, npcs.Length);
			if (undisposed > 0)
			{
				// WARNING, not information: these creatures are being served green and passive because nothing in the
				// corpus ever said otherwise, which in play reads as "the monsters are broken" (#840).
				_logger.LogWarning("World: {Count} NPC(s) on map {Map} have NO harvested disposition and are served FRIENDLY - they will not aggro: {Names}.", undisposed, mapId,
					string.Join(", ", undisposedNames.Order().Take(8)) + (undisposedNames.Count > 8
						? $", +{undisposedNames.Count - 8} more"
						: ""));
			}
			_logger.LogDebug("World: #363 {Labelled}/{Total} captured spawns have a spawn-area label on map {Map} (.togglenpcgroup to show; sample: {Sample}).", labelledCount, npcs.Length, mapId,
				labelSample ?? "<none>");
		}

		// Loads a map's static world objects into its zone ONCE, the world-object twin of EnsureZoneNpcs (#424). Before
		// this the entry path re-queried them per world entry, which is also how a missing `Include(o => o.Words)`
		// managed to ship invisible doors, vines and destructibles (ef9b59de) - one load per zone is one place to get
		// that right.
		public async Task EnsureZoneWorldObjects(uint mapId, uint instanceId)
		{
			if (!_objectLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var zone = _sim.World.ZoneFor(mapId, instanceId);
			zone.WorldObjects =
			[
				.. (await _database.GetWorldObjects(mapId)).Select(row => new LiveWorldObject(zone.AllocateHandle(), row)
				{
					// A harvest node starts FULL on every zone load (#494). Retail's nodes are shared across the whole
					// playfield and regrow, and neither the sharing nor a respawn cadence is measured - so a fresh zone
					// giving a full node is the closest honest model, and it means a harvested node comes back rather
					// than being gone for good. Null for everything that is not a node.
					Health = HarvestCatalog.IsHarvestable(row.DisplayCategory, row.DisplayId)
						? HarvestCatalog.NodeMaxHealth
						: null
				})
			];
		}

		// Why a class-specific destination may not be enterable yet, or null when it is.
		//
		// A class-specific playfield (Map.ClassSpecific - the city armories, and The Reliquary) holds a different NPC
		// set per class, and we
		// only have the classes some capture actually walked into. Teleporting a class we hold no set for would drop the
		// player into an empty room - and on a map with no captured world objects either, one with no door back out. So
		// the zone-line reports the gap in-game instead of teleporting, the same rule as an un-imported vendor.
		public async Task<string?> DescribeMissingClassImport(uint mapId, ConanClass forClass)
		{
			var map = await _database.GetMap(mapId);
			if (map is not { ClassSpecific: true })
				return null;

			var imported = await _database.GetImportedSpawnClasses(mapId);
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
