using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The live world: one <see cref="Zone" /> per (map, instance), created on demand. The authoritative in-memory
	///     model the GameServer simulation is built on. A single instance is shared across the listener; entities are
	///     placed when a player/NPC enters a map and removed on disconnect/despawn. Map changes move an entity between
	///     zones.
	/// </summary>
	public sealed class WorldState
	{
		// Seconds between an aggroed NPC's swings.
		public const double NpcAttackInterval = 2.0;

		// Companion follow: how far behind the leading player a follower trails before it steps up (retail keeps the
		// follower ~one step behind; its MoveTasks track the player ~6-7u apart). A follower never leashes/aggros.
		public const float FollowStopRange = 4f;

		// The client interpolates a MoveTask's from->to segment over the implied time, so the server must feed it
		// retail-sized segments - retail's chase/follow stream is ~6-7u steps. The model still steps Position every
		// tick (combat/interest fidelity), but a MoveTask is only broadcast once the entity has travelled this far since
		// the last one; a per-tick MoveTask flood is what the client cannot interpolate (jitter/teleport). At
		// NpcMoveSpeed (7 u/s) this broadcasts ~once a second.
		//
		// Do not lengthen this to thin out traffic: a broadcast segment is the straight line MoveFrom -> Position. At
		// ~6u the chord tracks a harvested polyline (median waypoint gap 5.8u), so the wire path is the street; at 25u
		// (tried in-client) the chord spans several waypoints and cuts through buildings, with the NPC covering the
		// chord too fast and then standing still. Idle patrol is not streamed at all - the route is issued and the
		// client walks it (see WalkRoute) - so this constant only ever paces a chase, a return or a follow.
		public const float MoveBroadcastSegment = 6f;

		// How close a player must be for a hostile NPC to aggro + attack (world units).
		public const float NpcAggroRange = 20f;

		// The speed an NPC walks its idle beat at. Capture-measured by watching where retail's patrollers actually were
		// over time, not by reading a field.
		//
		// Retail re-issues a patrol anchored at the NPC's then-current position, so consecutive re-issues of one handle
		// give ground speed directly (anchor delta / elapsed). Over 9,944 such pairs, bucketed by how far apart the two
		// re-issues were - a longer window contains more of the NPC's dwell, which drags the average below its walking
		// speed, so the ceiling of the short windows is the number wanted:
		//
		//   6-10s apart   n=1308   p50 1.34   p90 1.99   p99 2.69
		//   10-20s apart  n=3579   p50 1.09   p90 2.14   p99 2.23
		//
		// Both bands cap out at ~2.2 u/s however the window is cut, which is what a walking speed looks like when it is
		// being averaged with pauses.
		//
		// This deliberately does not come from the patrol's own trailer scalar, which `MoveTaskNodes.Path` describes
		// as "the patrol speed inside a patrol list". Read as a ground speed that field is wrong: it runs median
		// 6.75 u/s over 759 captured multi-point patrol nodes (p10 2.57, p90 14.37, max 159), 3-15x the speed the same
		// NPCs are observed moving at (Old Tarantia's Watchmen carry 31.73, Gingers 20.12, while the Scholar's Spirit
		// carries 2.02 and Phocas 3.50). It is a real per-route number that means something - it is just not u/s over
		// the ground.
		public const float RouteWalkSpeed = 2.2f;

		// A companion's follow speed, measured off retail rather than reused from the chase constant.
		//
		// Casilda's 52 follow MoveTasks in Hoxxling_mage-heraldofxotli_login_gameplay chain exactly - each segment
		// starts where the last ended - so the emitted path is her ground track and seg/dt is her speed. Over the 29
		// consecutive pairs where the player was actually moving (dt under 5s; the longer gaps are the player standing
		// still) the median is 4.94 u/s, mean 4.69, and the segments are 7.06u median.
		//
		// It matters because the client renders the walk at its own per-mode speed and only ever reacts to where the
		// server puts her (the executor advances by speed * dt from a per-entity field). Stepping her at
		// `NpcMoveSpeed` - 7 u/s, the chase constant - outruns the client by ~42%, so she walks the first segment and
		// is then snapped forward by each new one.
		public const float FollowStepSpeed = 4.94f;

		// How close a route walker must get to a waypoint to count as having reached it and take the next one. Small
		// relative to the 5.8u median gap between waypoints, so the walk stays on the harvested polyline rather than
		// cutting each corner - the corners are what keep it in the street.
		public const float RouteWaypointReached = 0.75f;

		// How long after a viewer streams an NPC in before its beat is issued. Retail's median gap between a creature's
		// spawn record and its first patrol (p50 4.02s over 2,519 captured NPCs; only 3% inside 0.1s).
		public const double RoutePatrolDelaySeconds = 4.0;

		// How long a route walker stands at the end of its beat before turning round. Retail patrols pause at the ends
		// rather than pivoting on the spot; the number is ours (no capture pins it), same as PatrolDwellSeconds.
		public const double RouteTurnaroundDwellSeconds = 3.0;

		// Line-of-sight tier 1 (cheap heuristic): the max vertical (Y/up) separation at which a hostile NPC will still
		// aggro a player. Aggro range is otherwise a 3D sphere, so without this an NPC aggros a player standing directly
		// above/below it - through a floor/ceiling or across a big drop. Gating on a vertical delta stops that with no
		// collision data; true wall/terrain occlusion (the RDB heightmap 1066611 / PhysX collision 1000012) is the
		// higher tier, applied in NearestPlayerInRange when the assets are available. ~6u is one storey. Open work
		// tracked in Gitea #284.
		public const float NpcAggroVerticalRange = 6f;

		// How close a chasing NPC actually walks up to its target (well inside NpcMeleeRange so it reaches contact and
		// stays comfortably in swing range, not hovering exactly on the swing boundary where a drifting target leaves
		// it perpetually a step away and never swinging). Must be < NpcMeleeRange.
		public const float NpcContactRange = 1.5f;

		// The radius of the ring attackers take their slots on, around the target. Measured off retail, and bracketed
		// rather than read off one number, because the corpus gives two estimates that lean opposite ways
		// (`--report melee-crowd`):
		//
		//   - the radius implied by each crowd's closest walking pair is a flat 0.96-1.08 over crowds of four to
		//     eight. That is an under-estimate: the arithmetic assumes evenly spaced occupants and real ones are not,
		//     so the closest pair is tighter than even spacing would give.
		//   - the measured distance from a walking crowd member to the player is a median 2.95u, p10 1.57u. That is an
		//     over-estimate: proximity is not aggro, so bystanders inside the sample radius pull it up.
		//
		// 1.5 sits between them, and it is `NpcContactRange` - the distance a chaser already stops at - so the slot
		// ring coincides with the stop ring instead of introducing a second number. What confirms the pick is that it
		// reproduces retail's neighbour spacing: 2.03u for three attackers against retail's measured 2.11, 1.33 for
		// five against 1.21, 0.84 for seven against 0.83.
		internal const float AttackerSlotRadius = NpcContactRange;

		// How far (horizontal) an NPC will chase from its spawn before giving up and returning home (so a player cannot
		// drag a mob across the whole zone). Once it disengages it keeps returning until home, ignoring re-aggro.
		// Retail's own number: `AIDefaults_MaximumChaseRadius` in the client's tuning sheet (RDB 1000516,
		// docs/rdb/client-tweak-table.md). Its two timer siblings are the two constants below.
		public const float NpcLeashRange = 45f;

		// How long a mob remembers who hit it, with no further hits: while this holds, its attacker is still its target
		// even from outside aggro range, so a mob shot from range chases the shooter. Retail's own number:
		// `AIDefaults_IrritationForgetPeriod` 4 (RDB 1000516) - "irritation" is the client's word for the hate an
		// attacker accrues, and forgetting it is exactly what this clock does.
		public const double NpcIrritationForgetSeconds = 4.0;

		// How long a mob that has lost its target keeps standing where the chase ended before turning for home. It is a
		// grace period, not a memory: a player who steps out of aggro range and straight back in is one engagement.
		// Retail's own number: `AIDefaults_ChaseDeaggroDelayTime` 5 (RDB 1000516).
		//
		// It does not gate the leash. Being dragged past `NpcLeashRange` turns the mob for home immediately - that is a
		// different decision ("this player has taken me too far") and delaying it would let a player camping the leash
		// edge keep re-aggroing.
		public const double NpcChaseDeaggroDelaySeconds = 5.0;

		// How long after the last hit the fight counts as still on for the anti-whittle full heal - below this a
		// damaged mob does not reset its health, above it the fight is over and it heals to full.
		//
		// Ours, not retail's: the tuning sheet says nothing about an anti-whittle rule, so there is no number to adopt.
		// It has to cover the gap between two casts of a slow spell (Smite is a 2 s cast on a 1 s recast) plus the walk
		// into range, so a ranged fight reads as one engagement rather than a series of first hits - at either retail
		// AI clock's value a heal would land mid-fight.
		public const double NpcAntiWhittleSeconds = 10.0;

		// How close (horizontal) an aggroed NPC must be to its target to swing; beyond this it chases.
		public const float NpcMeleeRange = 3f;

		// The client's own `TWK_RangeCheckSlack` (1.25, from the tuning table via `--report rdb-tweaks`): how far
		// inside its weapon's range an archer stands, so a shot is not sitting exactly on the boundary where one step
		// of player movement would break it and restart the approach.
		public const float RangeCheckSlack = 1.25f;

		// How fast a chasing NPC closes on its target (world units per second). Capture-measured: retail renders NPC
		// locomotion at ~6.9 u/s (median over 207 clear-locomotion samples, mode 7). This must equal the speed the
		// client renders the NPC at, because the client paces a no-duration 2-point MoveTask segment by the entity's
		// movement speed, not by the packet: a model that steps faster than the client renders outruns the on-screen
		// NPC and each new segment makes it jump forward. 7 matches the render speed so consecutive segments chain
		// smoothly.
		public const float NpcMoveSpeed = 7f;

		// A pet holds a tighter distance to its formation slot than a lone escort keeps to the player, so the fan-out
		// slots stay distinct (a 4u stop would let two pets share one slot).
		public const float PetFollowSlotStop = 1.5f;

		// The shared instance id every Shared map (and every pre-instancing call site) uses - one zone per map.
		public const uint DefaultInstance = 0;

		// The single playfield-instance token every burst-less world entry stamps - beach-derived, but threaded through
		// map-change / spawn / world-objects for every playfield so the client keeps the abilities tab (a token that
		// disagrees across those packets makes the client hide it). Shared so the cross-map teleport dest-announce can
		// carry the same value the destination's entry will stamp (WorldBroadcaster).
		public const uint DefaultPlayfieldToken = 0x004FE428;
		private const double ChatterChancePerCheck = 0.05;

		// Ambient bystander chatter: on each check a friendly/neutral NPC has a small chance to bark, so barks stay
		// sparse and staggered across NPCs rather than all firing at once. Cosmetic.
		private const double ChatterCheckSeconds = 4.0;

		// How close a player must come for a detection bark to fire. Matched to `WorldSimulation.SayRangeUnits` (50u),
		// which is how far a bark carries: greeting someone who is too far to hear it would be worse than not greeting
		// them. Provisional - no shipped number states an NPC's detection radius, and the one annotated capture times a
		// crier line but does not pin the distance the player crossed to earn it.
		public const float GreetingRange = 50f;

		// The band a greeted player must leave before the NPC will greet them again - hysteresis, so a player loitering
		// on the greeting boundary is not greeted, forgotten and greeted again every few ticks. 1.5x the greeting
		// range; the exact factor is ours, only the fact that it must exceed GreetingRange matters.
		public const float GreetingForgetRange = GreetingRange * 1.5f;

		// How long a creature stands between two ambient idle animations, on top of the animation's own length.
		// Capture-measured over 22,696 consecutive pairs (`--report ambient-animation`): p10 0.1s, p50 2.2s, p90 19.0s.
		//
		// Drawn as an exponential with this median rather than uniformly, because the measured shape is one-sided - most
		// pauses are short and a few are long, which a uniform draw inverts into "usually a middling wait". The
		// exponential's own p90 is 7.3s against retail's 19.0s, so the tail is thinner than retail's: an NPC that
		// occasionally stands about for half a minute is not reproduced. The median is the common case and it is what
		// this matches.
		public const double AmbientAnimationPauseMedianSeconds = 2.2;

		// The live zones keyed by (mapId, instanceId). A map can have several copies (instances): the default shared
		// instance plus any per-character Solo / per-party Group copies. DefaultInstance (0) is the shared copy every
		// Shared map uses, and what ZoneFor(mapId) resolves to.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), Zone> _zones = new ConcurrentDictionary<(uint MapId, uint InstanceId), Zone>();

		// Which copy of a map a team owns. Keyed by the team so every member routes to one zone; see TeamInstance for
		// why the entry is validated against the live zone rather than cleaned up on leave.
		private readonly ConcurrentDictionary<(uint TeamId, uint MapId, bool Epic), uint> _teamInstances = new ConcurrentDictionary<(uint TeamId, uint MapId, bool Epic), uint>();

		// Which copy of a minigame map a custom PvP match owns - both sides of the match land in it. Same live-zone
		// validation as the team dict above (MatchInstance).
		private readonly ConcurrentDictionary<(uint MatchId, uint MapId), uint> _matchInstances = new ConcurrentDictionary<(uint MatchId, uint MapId), uint>();

		// Server-side terrain (RDB heightmap), for the tier-2 aggro line-of-sight. Null = terrain unavailable (no
		// client RDB); the line-of-sight check then fails open, leaving the tier-1 vertical gate.
		private readonly TerrainService? _terrain;
		private readonly TickAccumulator _chatter = new TickAccumulator(ChatterCheckSeconds);

		// Server-global monotonic allocator for playfield-instance tokens, mirroring retail's behaviour (a steadily
		// increasing per-instance id stamped into despawns; see protocol_despawn_n3toclientquit). Every Zone gets a
		// fresh token on creation. Seeded clear of the observed retail pools (~0x004F.. / ~0x01EA..) so a minted token
		// is recognisably ours in a capture.
		private uint _nextPlayfieldToken = 0x00A00000;

		public WorldState(TerrainService? terrain = null)
		{
			_terrain = terrain;
		}

		// Mints the next playfield-instance token. Thread-safe (zones can be created from any connection).
		public uint NextPlayfieldToken()
		{
			return Interlocked.Increment(ref _nextPlayfieldToken);
		}

		// Adds the entity to the default shared instance of `mapId` (pre-instancing callers / tests). See the
		// instance-aware overload.
		public Zone Place(WorldEntity entity, uint mapId, Vector3 position, Quaternion rotation)
		{
			return Place(entity, mapId, DefaultInstance, position, rotation);
		}

		// Adds the entity to the (mapId, instanceId) zone with the given transform, removing it from its previous zone
		// if its map or instance changed. Returns the zone it now lives in so the caller can run interest queries
		// against it. The entity's MapId/InstanceId are updated to match - so every later ZoneForEntity lands in this
		// same copy.
		public Zone Place(WorldEntity entity, uint mapId, uint instanceId, Vector3 position, Quaternion rotation)
		{
			if ((entity.MapId != mapId || entity.InstanceId != instanceId) && _zones.TryGetValue((entity.MapId, entity.InstanceId), out var oldZone))
				oldZone.Remove(entity.Handle);

			entity.MapId = mapId;
			entity.InstanceId = instanceId;
			entity.Position = position;
			entity.Rotation = rotation;
			var zone = ZoneFor(mapId, instanceId);
			zone.Add(entity);
			return zone;
		}

		// Removes the entity from its current zone (disconnect / despawn).
		public void Remove(WorldEntity entity)
		{
			if (_zones.TryGetValue((entity.MapId, entity.InstanceId), out var zone))
				zone.Remove(entity.Handle);
		}

		/// <summary>
		///     Picks (or creates) the instance a player entering <paramref name="mapId" /> should join, given the map's
		///     instancing mode and the player's existing world entity (null on a fresh login). Same-map re-entry
		///     (respawn/teleport back) keeps the player's current copy; a fresh entry mints a private copy for a Solo map
		///     and joins the shared copy otherwise. The result is fed to <see cref="Place(WorldEntity, uint, uint, Vector3, Quaternion)" />.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A Group map routes by the player's team: <paramref name="teamId" /> is the team they are in (null when
		///         solo), read across the CS seam by the caller. The whole party lands in one copy, a member arriving
		///         later joins it rather than minting a second, and a player entering a group map with no team gets a
		///         private copy of their own (a group map is always somebody's).
		///     </para>
		///     <para>
		///         Retail's own leave message is "You will be unable to return to this instance", so a group instance is
		///         not re-enterable once left. <see cref="TeamInstance" /> models the half of that which follows from
		///         held state - the binding is dropped once the copy is empty, so a party that all leaves cannot come
		///         back to that copy. It does not stop a single member who stepped out from rejoining while the others
		///         are still inside, which retail refuses; that needs the leave-side message box the server does not
		///         send.
		///     </para>
		/// </remarks>
		public uint ResolveEntryInstance(WorldEntity? existing, uint mapId, MapInstanceMode mode, uint? teamId = null, bool epic = false, uint? pvpMatchId = null)
		{
			// A started custom PvP match owns one copy of its minigame map, and every member - both sides - lands in
			// it, whatever the map's own mode says. Checked before the stay-in rule so a member already on the map (a
			// re-entry) still converges on the match's copy rather than keeping a pre-match one.
			if (pvpMatchId is { } match)
				return MatchInstance(match, mapId);

			// The stay-in rule only holds when the copy's difficulty matches the entry's: retail's captured epic
			// re-entry to the same map minted a fresh instance (consecutive playfield tokens), it did not reuse the
			// normal copy the player had just left.
			if (existing != null && existing.MapId == mapId && existing.InstanceId != DefaultInstance
			    && _zones.TryGetValue((mapId, existing.InstanceId), out var current) && current.IsEpic == epic)
				return existing.InstanceId; // already in a matching private copy of this same map - stay in it

			// An epic copy never shares a zone with normal players - a Shared-mode map's epic entry gets a private
			// copy (the captured epic Spider Caverns entry was solo with a fresh instance), and a team's epic copy is
			// bound separately from its normal one.
			if (epic && mode == MapInstanceMode.Shared)
				return teamId is { } epicTeam ? TeamInstance(epicTeam, mapId, true) : NextPlayfieldToken();

			return mode switch
			{
				MapInstanceMode.Solo => NextPlayfieldToken(),
				MapInstanceMode.Group => teamId is { } team ? TeamInstance(team, mapId, epic) : NextPlayfieldToken(),
				_ => DefaultInstance
			};
		}

		// The copy of `mapId` owned by custom PvP match `matchId` - the minigame twin of TeamInstance, with the same
		// empty-means-gone lifetime: an entry whose remembered zone has emptied mints a fresh copy.
		private uint MatchInstance(uint matchId, uint mapId)
		{
			if (_matchInstances.TryGetValue((matchId, mapId), out var existing)
			    && _zones.TryGetValue((mapId, existing), out var zone)
			    && zone.Entities.Any(e => e.IsPlayer))
				return existing;

			var minted = NextPlayfieldToken();
			_matchInstances[(matchId, mapId)] = minted;
			return minted;
		}

		// The copy of `mapId` owned by `teamId`, minted on the team's first entry and reused by every later member.
		//
		// The binding is only kept while the copy still holds somebody: an entry whose remembered zone has emptied
		// mints a fresh instance instead. That is the "empty = gone" lifetime, expressed as a lookup rather than as a
		// cleanup pass - a zone with no players in it is indistinguishable from one that was never created, so there is
		// nothing to tear down and no timer to get wrong.
		private uint TeamInstance(uint teamId, uint mapId, bool epic = false)
		{
			// The binding is per difficulty: a team's epic copy and its normal copy of one map are two different
			// zones, so joining "the team's instance" must not land an epic entrant among normal mobs.
			if (_teamInstances.TryGetValue((teamId, mapId, epic), out var existing)
			    && _zones.TryGetValue((mapId, existing), out var zone)
			    && zone.Entities.Any(e => e.IsPlayer))
				return existing;

			var minted = NextPlayfieldToken();
			_teamInstances[(teamId, mapId, epic)] = minted;
			return minted;
		}

		/// <summary>Whether <paramref name="instanceId" /> of <paramref name="mapId" /> is the copy owned by a team.</summary>
		/// <remarks>
		///     A team-owned copy's <c>PlayfieldProxy</c> carries <c>(0xDEA9, teamId)</c> in its owner slots where a
		///     personal one carries the character. Asked of the zone rather than of the player, so the answer is the
		///     same for every member and does not change when one of them leaves their team.
		/// </remarks>
		public uint? TeamOwning(uint mapId, uint instanceId)
		{
			foreach (var ((teamId, map, _), instance) in _teamInstances)
			{
				if (map == mapId && instance == instanceId)
					return teamId;
			}

			return null;
		}

		/// <summary>
		///     Advances the world model by <paramref name="deltaSeconds" /> and returns the wire-visible effects for the
		///     caller to broadcast: dead NPCs whose respawn delay elapsed (revived here - vitals + transform reset), loot
		///     bags whose lifetime expired, aggroed NPCs chasing (NpcMove effects, broadcast as MoveTask) and swinging at
		///     a nearby player, and the ambient behaviours. Player transforms are driven by Move packets, not the tick.
		///     The model stays network-free - it reports effects and the simulation turns them into packets.
		/// </summary>
		public IReadOnlyList<WorldTickEffect> Tick(double deltaSeconds)
		{
			List<WorldTickEffect> effects = [];

			var chatterDue = _chatter.Advance(deltaSeconds, out _);

			foreach (var zone in _zones.Values)
			{
				foreach (var entity in zone.Entities)
				{
					if (entity.IsLoot)
					{
						if (entity.TickExpiry(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.LootExpired));
						continue;
					}

					if (entity.IsPlayer)
						continue;

					// A summon with a lifetime (the Frozen Hatred fatality pet). Checked before the dead/respawn logic so
					// an expiring pet leaves cleanly rather than lingering as a corpse.
					if (entity.DespawnAt is { } despawnAt && DateTimeOffset.UtcNow >= despawnAt)
					{
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.TimedDespawn));
						continue;
					}

					// A dead NPC drops its loot bag at once, then lies there as a corpse for CorpseFadeSeconds before the
					// body fades and its handle is quit - retail's sequence, and the bag outlives the body rather than
					// replacing it. The corpse and respawn timers run independently off the same death; the corpse is
					// long gone (~33s) before the respawn (~150s) puts the creature back.
					if (entity.IsDead)
					{
						switch (entity.TickCorpse(deltaSeconds))
						{
							case CorpseStage.Drop:
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseLootDropped));
								break;
							case CorpseStage.Fade:
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseFaded));
								break;
							case CorpseStage.Quit:
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseQuit));
								// Summoned NPCs and ambient pool members do not revive: their corpse sequence is their
								// whole afterlife, so once it has run they leave the world instead of waiting out a
								// respawn timer they will never use.
								if (entity.Route.NoRespawn)
								{
									effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseRemoved));
									// An encounter boss carries the lure it replaced. Once its corpse clears, put the
									// lure back where it stood so the encounter can fire again. Guarded so a corpse
									// cannot restore twice.
									if (entity is { Presentation.EncounterLureTemplateId: not null, Presentation.EncounterResetDone: false })
									{
										entity.Presentation.EncounterResetDone = true;
										effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterReset));
									}
								}

								break;
						}

						if (!entity.Route.NoRespawn && entity.TickRespawn(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcRespawned));

						continue;
					}

					// Past here the entity is a live NPC.
					// The ambient idle animation - a citizen leaning on a wall, scratching, looking round. Per-entity and
					// self-timed: the animation's own length plus the pause the creature stands for afterwards, because
					// retail never starts one before the last has finished.
					//
					// Only while standing and out of combat. That is retail's own split: 24,432 of the 25,750
					// attributable captured Emote records land on an actor with no MoveTask in the preceding 10s
					// (`--report ambient-animation`). Playing an idle over a walk would fight the walk animation anyway.
					// Never for a quest escortee: it is held in a scripted pose until its rescue (an idle played over
					// Casilda's captive pose replaces it), and it is walking behind the player afterwards, which the
					// standing gate already covers. Retail does animate her, but on combat events near the player rather
					// than on this self-timed rotation.
					if (!entity.IsQuestEscortee && entity.Presentation.TickAmbientAnimation(deltaSeconds))
					{
						if (!IsStanding(zone, entity))
						{
							entity.Presentation.ArmAmbientAnimation(AmbientAnimationPauseMedianSeconds); // busy - look again shortly
						}
						else if (AmbientAnimations.Pick(entity.Presentation.AmbientAnimations, Random.Shared) is { } picked)
						{
							entity.Presentation.AmbientAnimationPicked = picked;
							entity.Presentation.ArmAmbientAnimation(picked.DurationCentiseconds / 100.0 + ExponentialPause(AmbientAnimationPauseMedianSeconds));
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcAmbientAnimation));
						}
					}

					// While engaged, the defender moves one shield to another arc every 5-12s (the captured cadence), so
					// the attacker has to keep re-aiming at the weakest arc. Per-entity timer, not a shared accumulator -
					// each captured NPC shifts on its own clock.
					if (entity.TickShieldShift(deltaSeconds, Random.Shared))
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcShieldRose));

					// Ambient chatter: NPCs that have captured ambient bark lines occasionally speak, staggered by the
					// per-NPC random roll so they do not all bark at once. The processor picks a random line and sends
					// it to nearby players (cosmetic). NPCs without a captured ambient group stay silent.
					if (chatterDue && entity.Presentation.ChatterLines is { Length: > 0 } && Random.Shared.NextDouble() < ChatterChancePerCheck)
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcChatter));

					// A detection bark is not on that clock. The crier greets a player he has noticed arrive, so the cue
					// is the arrival itself - one line per player per approach, addressed at them by name.
					if (entity.Presentation.GreetingLines is { Length: > 0 })
						DetectGreetings(zone, entity);

					// Companion follow: a "released" NPC (e.g. Casilda after her rescue) follows its leader - step toward
					// them and broadcast a MoveTask each step (ungated; not combat), the server-driven incremental
					// follow retail uses. Takes priority over patrol/aggro/leash. Clears when the leader leaves the zone
					// or logs out.
					if (entity.FollowTargetHandle is { } followTargetHandle)
					{
						// The leader may be an NPC: an ambient formation-follow is one creature bound to another
						// creature's movement. A dead NPC leader does drop the binding - a formation has nothing to hold
						// onto once its leader is a corpse.
						if (!zone.TryGet(followTargetHandle, out var leader) || (!leader.IsPlayer && leader.IsDead))
						{
							entity.FollowTargetHandle = null;
							EndMove(entity, true, WorldTickEffectType.NpcFollow);
							continue;
						}

						// A pet (OwnerHandle != 0) follows a per-pet formation slot around the leader, not the leader's
						// exact spot, so multiple pets fan out instead of stacking. The slot is deterministic from the
						// handle (stable, no per-tick jitter) and settles at a tighter stop so it holds its place.
						// Escort/quest followers (Casilda, OwnerHandle == 0) keep the exact-follow path.
						//
						// An ambient formation follower takes the same slot treatment, and for the same reason retail
						// does: the four `Black Dragon Guard Patroller`s behind one captain each hold their own corner
						// of a 3x3 grid rather than standing in his back.
						var inFormation = entity.OwnerHandle != null || !leader.IsPlayer;
						var (ox, oz) = inFormation
							? FollowFormationOffset(entity.Handle)
							: (0f, 0f);
						var stop = inFormation
							? PetFollowSlotStop
							: FollowStopRange;
						var fdx = leader.Position.X + ox - entity.Position.X;
						var fdz = leader.Position.Z + oz - entity.Position.Z;
						var fdist = MathF.Sqrt(fdx * fdx + fdz * fdz);
						if (fdist > stop)
							StepAndStream(entity, fdx, fdz, fdist, stop, WorldTickEffectType.NpcFollow, leader, speed: FollowStepSpeed);
						else
							// Caught up: flush the trailing approach (a real ~6u segment toward the leader, so its facing
							// points straight at her) but send no StopIIR.
							//
							// A follower oscillates around FollowStopRange for as long as the player is walking - out of
							// range, step, back in range - so a Stop here would fire on every oscillation. Retail's
							// stream is bursts of Moves with no Stop between them (52 moves to 7 stops across the whole
							// capture, and none inside a burst); each Stop halts the client's walk and the next Move
							// restarts it, which plays the halt animation over and over while the server keeps
							// repositioning her.
							//
							// The Stop belongs to the follow ending - the leader-gone branch above still sends one - not
							// to her briefly catching up. With the task bound to the leader (mode-08) the client holds
							// its own position near them anyway.
							EndMove(entity, false, WorldTickEffectType.NpcFollow);
						continue;
					}

					if (entity.Vitals == null)
						continue;

					// Scripted encounter: a lure is a harmless NPC that watches for an approaching player and, at two
					// measured distances, cries out and then swaps itself for a boss. Checked before the aggro block
					// because a lure is not hostile and would otherwise never look at players at all.
					if (entity.Presentation.EncounterBossTemplateId != null)
					{
						var approaching = NearestPlayerInRange(zone, entity, MathF.Max(entity.Presentation.EncounterBarkRadius, entity.Presentation.EncounterTriggerRadius));
						if (approaching != null)
						{
							var edx = approaching.Position.X - entity.Position.X;
							var edz = approaching.Position.Z - entity.Position.Z;
							var edist = MathF.Sqrt(edx * edx + edz * edz);

							// The trigger wins when both are satisfied - a player who arrives inside the inner radius in
							// one step should spring the trap, not merely be shouted at.
							if (edist <= entity.Presentation.EncounterTriggerRadius)
							{
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterTrigger, approaching));
								continue; // the lure is about to be replaced; skip the rest of its tick
							}

							if (!entity.Presentation.EncounterBarked && edist <= entity.Presentation.EncounterBarkRadius)
							{
								entity.Presentation.EncounterBarked = true;
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterBark, approaching));
							}
						}
					}

					// Only hostile NPCs (mobs) aggro; talk-to NPCs (quest-givers/vendors) never attack.
					//
					// Being hit aggros too, not just proximity: `NearestPlayerInRange` alone would engage only someone
					// who walks inside NpcAggroRange, so a player attacking from further out - any ranged attack, and
					// any spell - would hit a mob that never engages, and an unengaged mob takes the `target == null`
					// branch below and revives to full.
					// Initiation and retaliation are separately gated. `Aggressive` decides whether walking past is
					// enough; `Hostile` alone is enough to fight back once hit, so a passive NPC (the Town Crier) is
					// unprovoked rather than inert - attack him and he still defends himself.
					var target = entity.Hostile
						? (entity.Aggressive
							? NearestPlayerInRange(zone, entity, NpcAggroRange)
							: null) ?? RecentAttackerOf(zone, entity)
						: null;

					var homeDx = entity.Route.SpawnPosition.X - entity.Position.X;
					var homeDz = entity.Route.SpawnPosition.Z - entity.Position.Z;
					var homeDist = MathF.Sqrt(homeDx * homeDx + homeDz * homeDz);

					// Leash: once led too far from its territory, disengage and keep returning home until arrived (ignoring
					// re-aggro) so a player hovering at the edge cannot ping-pong it. A returning NPC walks back, then
					// heals to full on arrival. Territory is spawn plus - for a patroller - its route; measured against
					// spawn alone, the wander and this check would fight over every far anchor.
					if (entity.Route.BeyondLeash(entity.Position, homeDist, NpcLeashRange))
						entity.Route.Returning = true;

					// De-aggro return: when an NPC loses its target (the player left aggro range) while away from its
					// spawn, walk it back home instead of leaving it standing where the chase ended. Patrollers are
					// exempt - their route drives them; everyone else returns to origin (then heals on arrival, below).
					//
					// After NpcChaseDeaggroDelaySeconds, not immediately - retail's own `AIDefaults_ChaseDeaggroDelayTime`.
					// The mob holds its ground for the delay first, so a player who steps out of aggro range and straight
					// back in is one engagement rather than a chase, a turn for home, and a fresh aggro. The stamp is
					// cleared the moment it has a target again, so the delay only ever measures an uninterrupted absence.
					if (target != null)
						entity.Route.TargetLostAt = null;
					else if (homeDist > NpcMeleeRange && !entity.Route.Patrols && !entity.Route.Returning)
					{
						entity.Route.TargetLostAt ??= DateTimeOffset.UtcNow;
						if ((DateTimeOffset.UtcNow - entity.Route.TargetLostAt.Value).TotalSeconds >= NpcChaseDeaggroDelaySeconds)
							entity.Route.Returning = true;
					}

					if (entity.Route.Returning)
					{
						SetCombat(entity, false); // disengaged: sheathe the weapon while walking home
						entity.ArmAttackCooldown(NpcAttackInterval);
						entity.Route.RouteInterrupted = true; // walked off its beat - rejoin at the nearest waypoint after
						if (homeDist > NpcMeleeRange)
						{
							// The walk home also descends to the spawn's altitude. Movement otherwise keeps the entity's
							// current Y ("keep terrain Y"), which is right for a chase (the ground under the path) but
							// wrong here - the stray Y is whatever height it was led/wandered to, so the NPC would walk
							// the whole way home at that altitude and arrive floating above its spawn.
							StepAndStream(entity, homeDx, homeDz, homeDist, 0f, WorldTickEffectType.NpcMove, null!, entity.Route.SpawnPosition.Y);
							continue;
						}

						entity.Route.Returning = false;
						// Home again - plant the spawn's altitude. The walk down closes the height gap in proportion to
						// the horizontal one, but it stops NpcMeleeRange short of the spawn, so that same fraction of the
						// gap would be left standing. Arriving is the point to be exact about it.
						entity.Position = new Vector3(entity.Position.X, entity.Route.SpawnPosition.Y, entity.Position.Z);
						entity.Vitals.ReviveToFull(); // home again
						EndMove(entity, true, WorldTickEffectType.NpcMove); // arrived: flush + stop
						continue;
					}

					if (target == null)
					{
						SetCombat(entity, false); // no target in range: disengage and sheathe
						entity.ArmAttackCooldown(NpcAttackInterval); // re-aggro should not swing instantly

						// A route walker resumes its beat here, and must not be stopped first - EndMove would close its
						// move stream every idle tick. It keeps one continuous walk instead, so the flush/stop below is
						// for the wander and the chase, which genuinely end.
						if (entity.Route.WalksRoute)
						{
							if (entity.Vitals.Health < entity.Vitals.MaxHealth && !entity.InCombatWithin(NpcAntiWhittleSeconds))
								entity.Vitals.ReviveToFull();

							WalkRoute(entity);
							continue;
						}

						EndMove(entity, true, WorldTickEffectType.NpcMove); // stop if mid-chase
						// Disengaged with no one in range: heal back to full so it cannot be whittled from out of range.
						//
						// Only once the fight is genuinely over - "nobody stands next to me" is not the same as "nobody
						// is fighting me", and a mob shot from range must not heal between hits. NpcAntiWhittleSeconds
						// after the last hit is the test; keeping the mob chasing is a shorter, separate clock.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth && !entity.InCombatWithin(NpcAntiWhittleSeconds))
							entity.Vitals.ReviveToFull();
						// Retail wander (capture-verified): a beach NPC drifts with a single short 2-point move to a
						// nearby anchor every ~25-60s - not a continuous client-walked route. Server-driven: the model
						// moves to the destination and the one MoveFrom->Position segment is broadcast, so every player
						// sees the same spot and a later chase starts from where the NPC actually is.
						if (entity.Route.Patrols && entity.Route.TickPatrolCycle(deltaSeconds))
						{
							entity.MoveFrom = entity.Position;
							// Ambient anti-stacking: an area-managed pool member (SpawnAreaId set) avoids drifting onto a
							// point another live entity already occupies (within ~2u, matching FreeAmbientPoint), so a pool
							// spreads across its area instead of piling up. Captured patrols (no SpawnAreaId) keep their route.
							Func<Vector3, bool>? occupied = entity.SpawnAreaId == null
								? null
								: pt =>
								{
									foreach (var other in zone.Entities)
									{
										if (ReferenceEquals(other, entity) || other.IsDead || other.IsLoot)
											continue;

										var odx = other.Position.X - pt.X;
										var odz = other.Position.Z - pt.Z;
										if (odx * odx + odz * odz < 4f)
											return true;
									}

									return false;
								};
							entity.Position = entity.Route.NextWanderPoint(entity.Position, occupied);
							entity.Route.ArmPatrolCycle();
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcMove));
						}

						continue;
					}

					// Engaged: a target is in aggro range and the mob is not leashing home. Draw the weapon on the
					// transition (the captured combat-mode stat) before the chase/swing, mirroring retail - the stat-20
					// update lands the tick the mob acquires its target, while it is still closing the distance. The
					// target rides the effect so the broadcaster can also push the aggro (HateList/ThreatLevel).
					SetCombat(entity, true, target);
					entity.Route.RouteInterrupted = true; // a chase leaves the beat; the walk rejoins at the nearest waypoint

					// Chase: if the target is beyond melee range, step toward it (horizontal plane; keep terrain Y). The move
					// is broadcast in retail-sized segments (StepAndStream throttle) so the client interpolates the run up
					// to the player smoothly; it swings once it arrives in melee range.
					// Each attacker chases its own slot on a ring around the target, not the target's exact point;
					// without the offset every attacker solves for the same sphere and they settle inside one body's
					// worth of space (five Tortage Pirates measured 0.03u-0.46u apart in-client, a distance retail
					// produces in 0.9% of cases).
					//
					// The geometry is retail's, measured rather than designed (`--report melee-crowd`, 25,638 NPC-NPC
					// pairs over 75 captures). Take each crowd's closest walking pair and divide it by the ring factor
					// `2*R*sin(pi/N)`, and the radius it implies is the same for every crowd size - 1.05, 1.03, 1.06,
					// 0.96, 1.08 for crowds of four to eight. That constancy is the finding: mobs packing in
					// opportunistically would imply a radius that falls away with N, and a ring is what holds it flat.
					var (slotX, slotZ) = AttackerSlotOffset(entity, target);
					var dx = target.Position.X + slotX - entity.Position.X;
					var dz = target.Position.Z + slotZ - entity.Position.Z;
					var horizontal = MathF.Sqrt(dx * dx + dz * dz);

					// The walk aims at the slot; the range check must still measure the target. They differ by up to
					// the ring radius, so reusing the slot distance would let a mob swing from out of range on one side
					// of the ring and refuse a legitimate swing on the other.
					var toTargetX = target.Position.X - entity.Position.X;
					var toTargetZ = target.Position.Z - entity.Position.Z;
					var targetDistance = MathF.Sqrt(toTargetX * toTargetX + toTargetZ * toTargetZ);

					// How close this creature needs to be to attack, and how close it therefore walks. A melee mob keeps
					// the measured pair: swing range 3u, walk into contact at 1.5u so it visibly closes instead of
					// stopping a swing away. An archer stops at its weapon's own `Attack Range` - 17.5u for every
					// captured bow against a melee weapon's 2.0 - because a bow carrier walked into contact range fights
					// like a melee mob.
					//
					// The stand-off is the weapon range less the client's own `TWK_RangeCheckSlack` (1.25), so the shot
					// is inside range rather than exactly on the boundary, where a step of player movement would break
					// it and restart the approach.
					var attackRange = entity.RangedAttacker
						? entity.AttackRange
						: NpcMeleeRange;
					var walkTo = entity.RangedAttacker
						? MathF.Max(NpcContactRange, entity.AttackRange - RangeCheckSlack)
						: NpcContactRange;

					if (horizontal > walkTo)
						StepAndStream(entity, dx, dz, horizontal, walkTo, WorldTickEffectType.NpcMove, target);
					else
						// In position: flush the trailing approach step so the client sees it close the last bit (no StopIIR -
						// the swing/combat-mode draw plants it).
						EndMove(entity, false, WorldTickEffectType.NpcMove);

					if (targetDistance > attackRange)
					{
						entity.ArmAttackCooldown(NpcAttackInterval); // still out of range: wind up for arrival
						continue;
					}

					if (entity.TickAttackReady(deltaSeconds, NpcAttackInterval))
						// A caster NPC casts a spell on the same cadence; everyone else swings.
					{
						effects.Add(new WorldTickEffect(entity, entity.IsCaster
							? WorldTickEffectType.NpcCast
							: WorldTickEffectType.NpcAttack, target));
					}
				}
			}

			return effects;

			// Flips an NPC's combat-engaged state and, on the transition, queues the combat-mode effect the broadcaster
			// turns into the stat-20 weapon draw/sheathe. No-op when already in that state, so it fires once per
			// engage/disengage.
			void SetCombat(WorldEntity e, bool inCombat, WorldEntity? target = null)
			{
				if (e.InCombat == inCombat)
					return;

				e.InCombat = inCombat;
				effects.Add(new WorldTickEffect(e, inCombat
					? WorldTickEffectType.NpcEnterCombat
					: WorldTickEffectType.NpcExitCombat, target));

				// Engaging opens the defender's shield rose (and arms its first shift); disengaging closes it, so a
				// fresh fight starts on a fresh distribution the way each captured engagement does.
				if (inCombat)
				{
					e.OpenShieldRose(Random.Shared);
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcShieldRose, target));
				}
				else
				{
					e.ShieldDistribution = 0;
				}
			}

			// Queues a detection bark for every player who has just walked into `e`'s greeting range, and forgets the
			// ones who have walked back out of it. This is the whole difference between a greeting and ambient chatter:
			// the cue is a player arriving, so the line lands as you walk up instead of being said to an empty street
			// on a clock that runs whether anyone is there or not.
			//
			// The forget range is deliberately wider than the greeting range: with one radius a player standing right on
			// the boundary would be greeted, forgotten and greeted again every few ticks. The gap is hysteresis, not a
			// second concept.
			void DetectGreetings(Zone z, WorldEntity e)
			{
				foreach (var player in z.PlayersInRange(e.Position, GreetingForgetRange, e.Handle))
				{
					if (Vector3.Distance(player.Position, e.Position) > GreetingRange)
						continue; // inside the forget band but not yet close enough to greet - leave the state alone

					if (e.Presentation.GreetedPlayers.Add(player.Handle))
						effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcGreeting, player));
				}

				if (e.Presentation.GreetedPlayers.Count == 0)
					return;

				// Forget anyone who has left the band (or the zone entirely), so their next approach greets again.
				e.Presentation.GreetedPlayers.RemoveWhere(handle => !z.TryGet(handle, out var p)
				                                       || Vector3.Distance(p.Position, e.Position) > GreetingForgetRange);
			}

			// Steps `e` toward (dx,dz) this tick and queues a move broadcast only once it has travelled a retail-sized
			// segment since the last one, so the client interpolates ~6u segments instead of a per-tick snap. The model
			// position advances every call regardless; `moveType` is NpcMove (chase/return) or NpcFollow (companion) so
			// the loop gates/handles it correctly. `target` rides the effect (the aggro/leader the broadcaster wants).
			void StepAndStream(WorldEntity e, float dx, float dz, float dist, float stopShort, WorldTickEffectType moveType, WorldEntity target, float? targetY = null,
				float speed = NpcMoveSpeed)
			{
				if (!e.Moving) // a fresh move: anchor the first segment at the start point
				{
					e.MoveOrigin = e.Position;
					e.Moving = true;
				}

				StepToward(e, dx, dz, dist, deltaSeconds, stopShort, targetY, speed);
				if (Vector3.DistanceSquared(e.MoveOrigin, e.Position) < MoveBroadcastSegment * MoveBroadcastSegment)
					return; // segment too short to broadcast yet - keep accumulating

				e.MoveFrom = e.MoveOrigin; // the segment the broadcaster sends (MoveFrom -> Position)
				e.MoveOrigin = e.Position; // re-anchor for the next segment
				effects.Add(new WorldTickEffect(e, moveType, target));
			}

			// Walks `e` along its harvested route - one waypoint at a time, in the order the route was captured in.
			//
			// A harvested route is a dense street-following polyline (median 5.8u between waypoints). Walking it in
			// order keeps every step on ground the retail NPC actually walked, with no collision data needed - the
			// route is the collision answer, sampled by whoever captured it; picking a random waypoint and moving there
			// in a straight line would go through the block rather than round it.
			//
			// The model advances with StepToward at the route's own speed; the client walks the beat it was issued (see
			// below). The waypoint's own Y is the target altitude, so a beat that climbs a staircase climbs it instead
			// of walking through the steps at the ground floor's height.
			void WalkRoute(WorldEntity e)
			{
				if (e.Route.RouteInterrupted)
				{
					e.Route.RejoinRouteAtNearest(e.Position); // chased or leashed off the beat: pick it up where it now stands
					e.Route.RouteReissueDue = true; // ...and the client's copy of the walk is now wrong, so re-issue it
				}

				if (!e.Route.TickRouteDwell(deltaSeconds))
					return; // standing at a turnaround; the client is already holding the same pose

				// The beat is issued to the client as the captured multi-waypoint patrol and walked there - the server
				// does not stream it. Streaming it as 2-point chase segments would cost 19.3 MoveTask/s against retail's
				// 3.09/s corpus maximum, make the NPC run (the chase form carries no duration, so the client paces it off
				// its run attribute), and tug it back once per segment. Retail sends this form: of the 759 captured
				// multi-point patrol nodes, 535 carry [duration][remaining], where 423 of 425 two-point ones do not.
				if (e.Route.RouteReissueDue)
				{
					e.Route.RouteReissueDue = false;
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcPatrol));
				}

				// A viewer that just streamed this NPC in is told the beat a few seconds later, as retail does - see
				// RoutePatrolDelaySeconds. Sent with the spawn, the client dashes to reconcile.
				if (e.Route.RoutePatrolDelayRemaining > 0)
				{
					e.Route.RoutePatrolDelayRemaining -= deltaSeconds;
					if (e.Route.RoutePatrolDelayRemaining <= 0)
					{
						e.Route.RoutePatrolDelayRemaining = 0;
						e.Route.RouteReissueDue = true;
					}
				}

				var waypoint = e.Route.RouteWaypoint;
				var dx = waypoint.X - e.Position.X;
				var dz = waypoint.Z - e.Position.Z;
				var distance = MathF.Sqrt(dx * dx + dz * dz);
				if (distance > RouteWaypointReached)
				{
					// The model only - no broadcast. It advances at the route's own captured speed so the server's idea of
					// where the NPC stands tracks what the client is rendering, which is what death-pose/loot/aggro need.
					StepToward(e, dx, dz, distance, deltaSeconds, 0f, waypoint.Y, e.Route.RouteSpeed);
					return;
				}

				// Arrived. Land exactly on it (so the next leg starts from the polyline, not a fraction short of it) and
				// take the next waypoint; pause only where the route turns round or a lap closes - and re-issue the beat
				// there, since that is where the client's own copy of the walk has run out.
				e.Position = new Vector3(waypoint.X, waypoint.Y, waypoint.Z);
				if (e.Route.AdvanceRoute())
				{
					e.Route.ArmRouteDwell(RouteTurnaroundDwellSeconds);
					e.Route.RouteReissueDue = true;
				}
			}

			// Ends `e`'s move (it reached its target / home / leader): flushes the trailing sub-segment the throttle did
			// not broadcast (so the client walks the last bit instead of snapping) and, when `stop`, closes the stream
			// with a single StopIIR. The flush's segment direction also gives the stop a non-degenerate facing. No-op if
			// `e` was not moving, so an idle entity never re-emits a stop.
			void EndMove(WorldEntity e, bool stop, WorldTickEffectType moveType)
			{
				if (!e.Moving)
					return;

				e.Moving = false;
				if (Vector3.DistanceSquared(e.MoveOrigin, e.Position) > 1e-4f)
				{
					e.MoveFrom = e.MoveOrigin;
					e.MoveOrigin = e.Position;
					effects.Add(new WorldTickEffect(e, moveType)); // final approach step (no target needed)
				}

				if (stop)
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcStopped));
			}
		}

		// Advances the staged visual sequences running on static world objects and returns the steps that came due - a
		// harvested node's mesh swap, its collision drop, its regrowth.
		//
		// A separate pass from `Tick` because its subjects are separate: a `WorldTickEffect` is about a `WorldEntity`,
		// and a world object deliberately is not one (see Zone.WorldObjects). Squeezing an object into that record
		// would mean making its entity nullable for every existing effect.
		public IReadOnlyList<DueDynelVisualStep> TickVisualSequences(double deltaSeconds)
		{
			List<DueDynelVisualStep> steps = [];
			foreach (var zone in _zones.Values)
				steps.AddRange(zone.TickVisualSequences(deltaSeconds));

			return steps;
		}

		// The harvest nodes whose gathering-pin window ran out this tick, with the zone each one lives in.
		public IReadOnlyList<(Zone Zone, LiveWorldObject Node)> TickGatheringPins(double deltaSeconds)
		{
			List<(Zone, LiveWorldObject)> due = [];
			foreach (var zone in _zones.Values)
			{
				foreach (var node in zone.TickGatheringPins(deltaSeconds))
					due.Add((zone, node));
			}

			return due;
		}

		// Read-only lookup of the default shared instance; does not mint a zone for an untracked map.
		public bool TryGetZone(uint mapId, [MaybeNullWhen(false)] out Zone zone)
		{
			return _zones.TryGetValue((mapId, DefaultInstance), out zone);
		}

		// The default shared instance of a map (pre-instancing callers, tests, the burst-token resolve path).
		public Zone ZoneFor(uint mapId)
		{
			return ZoneFor(mapId, DefaultInstance);
		}

		// The (mapId, instanceId) zone, minting it (with a fresh wire token) if it does not exist yet.
		public Zone ZoneFor(uint mapId, uint instanceId)
		{
			return _zones.GetOrAdd((mapId, instanceId), key => new Zone(key.MapId)
			{
				InstanceId = key.InstanceId,
				PlayfieldToken = NextPlayfieldToken()
			});
		}

		// The zone the entity currently lives in - its (MapId, InstanceId) copy. The routing primitive every
		// broadcast/interest query uses so it reaches only the player's own instance.
		public Zone ZoneForEntity(WorldEntity entity)
		{
			return ZoneFor(entity.MapId, entity.InstanceId);
		}

		// Every live instance of a map (all copies), for observers (MCP) that want the whole map regardless of which
		// copy an entity is in.
		public IEnumerable<Zone> ZonesForMap(uint mapId)
		{
			foreach (var kvp in _zones)
			{
				if (kvp.Key.MapId == mapId)
					yield return kvp.Value;
			}
		}

		// The player this mob is already fighting - whoever hit it last, while the combat memory holds - or null.
		//
		// This is the aggro source proximity cannot supply: a mob attacked from beyond NpcAggroRange has nobody "in
		// range" to engage, so without this it stays disengaged no matter how hard it is hit. It deliberately applies
		// none of NearestPlayerInRange's gates - not range, not line of sight, not the vertical band - because they all
		// answer "would this mob notice that player", and a mob that has been hit has already noticed. The leash is
		// what bounds the resulting chase, exactly as it bounds a proximity chase.
		//
		// Still honours `Invisible`, which is the one gate that is not about noticing: `.invisible` means "draw no
		// aggro" by GM fiat, and a GM who turns it on mid-fight should stop being chased.
		private static WorldEntity? RecentAttackerOf(Zone zone, WorldEntity npc)
		{
			if (npc.LastAttackerHandle is not { } attacker || !npc.InCombatWithin(NpcIrritationForgetSeconds))
				return null;

			return zone.TryGet(attacker, out var player) && player is { IsPlayer: true, Invisible: false }
				? player
				: null;
		}

		// The closest player within `range` of the NPC (its aggro target), or null if none.
		private WorldEntity? NearestPlayerInRange(Zone zone, WorldEntity npc, float range)
		{
			WorldEntity? nearest = null;
			var nearestDist = float.MaxValue;
			foreach (var player in zone.PlayersInRange(npc.Position, range))
			{
				if (player.Invisible) // .invisible: a hidden player draws no aggro
					continue;
				if (!npc.IsVisibleTo(player)) // a quest-gated NPC (e.g. Scavenger Tuut) must not aggro/attack a player it
					continue; // was never streamed to - else the player takes hits from an invisible body
				if (MathF.Abs(player.Position.Y - npc.Position.Y) > NpcAggroVerticalRange) // line-of-sight tier 1: not through a floor/drop
					continue;

				var dist = Vector3.Distance(player.Position, npc.Position);
				// Line-of-sight tier 2/3: real occlusion - do not aggro a player behind a hill/ridge (terrain heightmap)
				// or a building wall (PhysX collision mesh). Skipped at point-blank range: a player standing on top of
				// the NPC must always aggro, so a thin triangle between two adjacent eye-points cannot wedge the mob
				// into never attacking. Beyond that the line-of-sight gate fails open when the assets are unavailable,
				// so it only ever removes false aggros the range + tier-1 vertical gate would have wrongly allowed.
				if (dist > NpcMeleeRange && _terrain != null && !_terrain.HasLineOfSight(zone.MapId, npc.Position, player.Position))
					continue;

				if (dist < nearestDist)
				{
					nearestDist = dist;
					nearest = player;
				}
			}

			return nearest;
		}

		// A deterministic per-attacker slot offset (X, Z) on the contact ring around the target, so several mobs on one
		// player fan out instead of converging on its exact point. The combat twin of `FollowFormationOffset`.
		//
		// Deterministic from the handle pair, and that is the design decision worth stating. A slot indexed by the
		// attacker's rank among the target's current attackers would spread more evenly, but the rank changes as mobs
		// join and die, so every attacker would re-solve for a new angle mid-fight - the jitter `FollowFormationOffset`
		// exists to avoid. Mixing the target's handle in as well is what keeps a creature from taking the same angle
		// against every target it ever fights, which a handle-only hash would.
		//
		// The offset is a ring position, not a displacement to add to the stop distance: the mob still walks until it
		// is `NpcContactRange` from the slot point, so its distance to the target stays inside melee range. Retail's
		// own crowd sits at a median 2.95u from the player (p10 1.57u), which brackets this.
		//
		// Two attackers can land on nearby angles - the sequence makes no spacing promise. That is accepted: it
		// degrades to a collision for that pair only. Retail overlaps sometimes too (0.9% of its walking pairs are
		// inside 0.5u), so an occasional close pair is not itself wrong.
		internal static (float X, float Z) AttackerSlotOffset(WorldEntity attacker, WorldEntity target)
		{
			// A golden-angle sequence, not a hash. `GoldenStep` is 2^32/phi, so reading `handle * GoldenStep` as a
			// fraction of a turn walks the circle in golden-ratio steps - the arrangement that keeps any prefix of the
			// sequence as evenly spread as it can be. That is the property this needs, because handles are allocated
			// sequentially: a wave of spawns gets consecutive handles, which is exactly the input an ordinary
			// scrambling hash handles worst (one put two of five consecutive attackers 0.42u apart).
			//
			// The target's handle is added as a rotation of the whole ring rather than mixed in per attacker: that
			// stops a creature taking the same angle against every target it ever fights, while leaving the attackers'
			// spacing relative to each other untouched.
			var angle = (attacker.Handle * GoldenStep + target.Handle * GoldenStep) / (float)uint.MaxValue * MathF.Tau;

			return (AttackerSlotRadius * MathF.Cos(angle), AttackerSlotRadius * MathF.Sin(angle));
		}

		// 2^32 / phi - the multiplier that turns a sequential id into a golden-angle position on the circle.
		private const uint GoldenStep = 2654435769;

		// A deterministic per-pet formation slot offset (X, Z) on a small ring around the leader, so multiple pets
		// following one player fan out instead of piling on the same spot. Derived from the entity handle so it is
		// stable across ticks (no per-tick jitter that would make the pet jitter in place): the handle picks an angle
		// around the circle and one of a few radius rings (so more than a ring's worth of pets do not collide).
		private static (float X, float Z) FollowFormationOffset(uint handle)
		{
			var angle = handle % 3600 * (MathF.PI / 1800f); // 0..2pi in 0.1 degree steps, deterministic per handle
			var radius = 3f + handle % 3; // 3 / 4 / 5u rings
			return (radius * MathF.Cos(angle), radius * MathF.Sin(angle));
		}

		// Whether `entity` is genuinely standing still this tick - the state an ambient idle animation belongs to.
		// Every way the tick can be moving or occupying it, in one place: mid-chase or mid-walk-home (`Moving`), engaged,
		// escorting/following, walking a captured beat (a route walker only stands at the ends of it), or with a player
		// close enough that this tick is about to make it chase.
		//
		// `Moving` alone is not enough: a route walk is model-only - it does not stream, so it never sets `Moving` -
		// and gating on that flag alone would have every patroller stopping to scratch itself while walking down the
		// street.
		private bool IsStanding(Zone zone, WorldEntity entity)
		{
			if (entity.Moving || entity.InCombat || entity.Route.Returning || entity.FollowTargetHandle != null)
				return false;
			if (entity.Route.WalksRoute && !entity.Route.RouteDwelling)
				return false;

			// An NPC that never initiates keeps idling while a player stands beside it - it is not about to fight, so
			// it has no reason to drop its ambient animation.
			return !entity.Hostile || !entity.Aggressive || NearestPlayerInRange(zone, entity, NpcAggroRange) == null;
		}

		// A pause drawn from an exponential with the given median - the shape the measured ambient-animation gaps have
		// (mostly short, occasionally long). Clamped away from zero so a draw cannot schedule the next animation on the
		// same tick. See AmbientAnimationPauseMedianSeconds for the measurement and for what this does not reproduce.
		private static double ExponentialPause(double medianSeconds)
		{
			return Math.Max(0.1, -Math.Log(1.0 - Random.Shared.NextDouble()) * medianSeconds / Math.Log(2.0));
		}

		// Advances `entity` one tick's worth toward (dx, dz) - a horizontal step at `speed` (keeping terrain Y),
		// `stopShort` leaves that many units short of the point (melee range when chasing, follow range when following);
		// 0 lands on it (returning home). The broadcast anchor (MoveOrigin) is managed by StepAndStream, not here, so a
		// caller that wants the step broadcast must go through StepAndStream.
		// `targetY` closes the vertical gap over the same walk instead of keeping the current Y: the return home knows
		// the altitude it is walking to (the spawn's), so it descends smoothly rather than arriving and snapping.
		// `speed` defaults to NpcMoveSpeed, the run a chase/return uses; an idle route walk passes its own captured
		// speed instead, because a beat is walked and the two are not the same number.
		private static void StepToward(WorldEntity entity, float dx, float dz, float distance, double deltaSeconds, float stopShort = 0f, float? targetY = null,
			float speed = NpcMoveSpeed)
		{
			var travel = MathF.Min((float)(speed * deltaSeconds), distance - stopShort);
			var y = entity.Position.Y;
			if (targetY is { } destY)
			{
				// Close the same fraction of the height gap as of the horizontal distance, so both land together.
				var remaining = distance - stopShort;
				y += (destY - y) * (remaining > 1e-4f ? MathF.Min(1f, travel / remaining) : 1f);
			}

			entity.Position = new Vector3(entity.Position.X + dx / distance * travel, y, entity.Position.Z + dz / distance * travel);
		}
	}
}
