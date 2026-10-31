using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     What an NPC does, told to everyone who can see it: aggro, turning hostile, entering and leaving combat mode,
	///     its idle assert and ambient animation, its movement, patrol route and follow, its respawn and its scripted
	///     exit - plus putting a runtime NPC or pet into the world in the first place, and taking one back out.
	/// </summary>
	/// <remarks>
	///     The seam against <see cref="WorldBroadcaster" /> is not the subject - both are about a
	///     <see cref="WorldEntity" /> - but the question: the broadcaster answers who can see what, the interest pass
	///     and the spawn, despawn and move wire it produces, while this answers what the creature is doing. Nothing in
	///     the broadcaster calls in here, which is what makes the split one-way; this calls out for the three sends the
	///     visibility system owns.
	///     <para>
	///         <see cref="RuntimeHandleAllocator" /> is shared with the broadcaster rather than owned here: loot bags
	///         are minted there and NPCs, pets and ambient members here, so a second counter would hand out colliding
	///         handles.
	///     </para>
	/// </remarks>
	public sealed class NpcBroadcaster
	{
		/// <summary>
		///     The behaviour-control state ids from the client's own behaviour trees (RDB 1010203; see
		///     <c>CharacterStatId.CombatMode</c>): 4 is <c>Fighting</c>, which draws the weapon, and 1 is <c>Idle</c>,
		///     which sheathes it.
		/// </summary>
		/// <remarks>
		///     The captured beach melee mobs push <c>CombatMode = 4</c> on the tick before each mob's first swing. The
		///     sheathe value is 1 and not 0: no shipped tree declares state 0, so it is a value retail has no meaning
		///     for.
		/// </remarks>
		private const uint NpcCombatModeValue = 4;

		/// <inheritdoc cref="NpcCombatModeValue" />
		private const uint NpcIdleStateValue = 1;

		// A scripted exit's two beats, timed from the farewell window closing - see ScheduleScriptedExit.
		private const int ScriptedExitMoveDelayMs = 4000;
		private const int ScriptedExitDespawnDelayMs = 1200;

		private readonly AmbientPoolService _ambient;
		private readonly WorldBroadcaster _broadcaster;
		private readonly ComboService _combos;
		private readonly INpcRepository _database;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public NpcBroadcaster(WorldSimulation sim, INpcRepository database, WorldBroadcaster broadcaster, AmbientPoolService ambient, ComboService combos,
			RuntimeHandleAllocator handles, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_broadcaster = broadcaster;
			_ambient = ambient;
			_combos = combos;
			_handles = handles;
			_logger = logger;
		}

		/// <summary>
		///     Make the NPC of the given template id in a map's zone follow, or stop following, the given player: the
		///     quest-driven escort companion trigger, as for Casilda after her rescue stage.
		/// </summary>
		/// <remarks>
		///     The escortee's template is the escort goal's <c>TrailingRefId</c>. See the private
		///     <c>SetNpcFollow</c> overload for the instance-aware zone resolution.
		/// </remarks>
		/// <returns>False when no such NPC is loaded in the zone.</returns>
		public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow)
		{
			return SetNpcFollow(mapId, playerHandle, follow, e => e.NpcTemplateId == templateId, $"template {templateId}");
		}

		/// <summary>
		///     An NPC engaged a target: push its aggro to every viewer in range that sees it - a hate-list update, a
		///     threat level, and the NPC's target lock - so the aggro is visible on the tick combat starts.
		/// </summary>
		/// <remarks>
		///     Additive to the combat-mode weapon draw, and gated with the rest of the combat simulation by the caller.
		///     The three records go out in retail's order: <c>NecroL80_..._login_combat</c> shows LockedToChanged arriving
		///     right after a hate-list and threat-level pair. LockedToChanged carries [npc handle] -> [target ref], and
		///     every captured instance is a low NPC handle locking onto a player, which is the aggro moment.
		///     <para>
		///         It goes to viewers rather than only to the target, which is measured: in
		///         <c>login_2-clients_group-trade-friend</c> the same (npc, target) pair is sent on both clients'
		///         streams (frames 3146/3147 and 3394/3395) even though only one of them is the target. A
		///         single-client capture cannot tell those apart.
		///     </para>
		/// </remarks>
		public async Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			if (target == null)
				return;

			// The hate and threat records are addressed to the target, not to the NPC: it is the player's threat list
			// that gains an entry, not the creature's. Measured over the whole corpus, 270 of 270 hate-list updates
			// carry the session player as the subject and name the other party as the target, and the threat level is
			// the player's on 681 of 712. Built the other way round they address a handle the client has no threat list
			// for, so it drops them - and with them the re-poll that follows, since retail's client answers this pair by
			// asking for the NPC's aggro status again and only then learns the creature turned hostile. That is how a
			// mob comes to attack a player while still drawing green and refusing to be attacked back.
			var hate = HateListUpdateCodec.Build(target.Handle, npc.Handle);
			// Retail opens with threat 0 and then 1 - an initialise followed by the first real level, both on the
			// player - and climbs from there as damage accrues, reaching 3 by the second swing in that fight.
			var threatOpen = ThreatLevelCodec.Build(target.Handle, 0, 1);
			var threat = ThreatLevelCodec.Build(target.Handle, 1, 1);
			// LockedToChanged is the record that is the NPC's: [npc] -> [target], exactly as captured.
			var lockedTo = LockedToChangedCodec.Build(npc.Handle, target.Handle);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
				{
					await WorldIir.Send(viewer.Client, hate);
					await WorldIir.Send(viewer.Client, threatOpen);
					await WorldIir.Send(viewer.Client, threat);
					await WorldIir.Send(viewer.Client, lockedTo);
				}
			}
		}

		/// <summary>
		///     A scripted exit: the NPC walks to a point and then leaves the world. Casilda's <c>RunToTortage</c> is
		///     the case it exists for - her escort is over, so she heads for the city gate and is gone.
		/// </summary>
		/// <remarks>
		///     The timing is retail's, measured over both beach-to-city captures, which agree to within 0.3 s. Timed
		///     from the farewell window closing:
		///     <code>
		///     +4.0 s   a MoveTask carries her the last few units to the gate
		///     +5.2 s   n3ToClientQuit - she is gone
		///     </code>
		///     (<c>xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city</c> frames 11419 close, 11438 move,
		///     11446 quit; <c>xxx_mage-demo_login_play_beach-to-enter-tortage_city</c> frames 10239, 10254, 10264.)
		///     <para>
		///         Scheduled rather than awaited: the caller is finishing a conversation, and holding its reply chain
		///         for five seconds would stall the window's own close.
		///     </para>
		/// </remarks>
		public void ScheduleScriptedExit(WorldEntity npc, Vector3 exit)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var zone = _sim.World.ZoneForEntity(npc);

			// Each step runs on the NPC's zone loop, and only while the NPC is still in its zone: it may be gone by then -
			// the player zoned, the zone reloaded, or a second trigger fired - and an exit for an entity that has left is
			// dropped rather than broadcast against a stale handle.
			Task.RunAfter(ScriptedExitMoveDelayMs, () => _sim.RunInZoneOf(npc, async () =>
			{
				npc.MoveFrom = npc.Position;
				npc.Position = exit;
				await BroadcastNpcMove(npc);

				Task.RunAfter(ScriptedExitDespawnDelayMs, () => _sim.RunInZoneOf(npc, async () =>
				{
					await _broadcaster.BroadcastEntityDespawn(npc);
					zone.Remove(npc.Handle);
					_logger.LogInformation("Script: {Npc} reached its exit and left the world.", npc.Name);
				}), _logger);
			}), _logger);
		}

		/// <summary>
		///     An NPC that was standing in the world peaceful has turned on the player: restate its collision group to
		///     the mob one, for every viewer that already knows it.
		/// </summary>
		/// <remarks>
		///     This is what makes the flip real to the client. Without it the creature is hostile on the server only -
		///     it swings, the hate list arrives, the re-poll answers hostile - and the player still sees a peaceful
		///     nameplate and cannot hit back, because the creature's <c>CarsGroup</c> is still the townsfolk group it
		///     spawned in.
		///     <para>
		///         Measured rather than guessed: over all 134 captures every character <c>CarsGroup</c> record carrying
		///         the mob group belongs to one of the three captured Saddur fights - three captures, three handles, no
		///         other occurrence - and in all three the order is identical: a "Fight" scry event, then this record,
		///         then the hate list, then the client's own re-poll. It is the first thing retail sends on the flip.
		///     </para>
		///     <para>
		///         Two details are copied from the capture rather than reasoned out: the record is a character one
		///         carrying the world-object <c>env</c> byte (0x00 rather than the 0x01 characters normally take), and
		///         retail sends it twice, as two packets in one segment, in all three captures.
		///         [V0: why the second copy.]
		///     </para>
		/// </remarks>
		public async Task BroadcastNpcTurnedHostile(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var body = StatCodec.Build(StatIirs.ForCharacter(npc.Handle, (uint)CharacterStatId.CarsGroup, NpcStatCore.MobCarsGroup, StatIirs.WorldObjectEnv));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Knowledge.Known.Contains(npc.Handle))
					continue;

				await WorldIir.Send(viewer.Client, body);
				await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     An NPC engaged or disengaged combat: push the combat-mode stat to every viewer in range that sees it, so
		///     the client draws or sheathes the NPC's weapon.
		/// </summary>
		/// <remarks>
		///     Without it the NPC swings empty-handed: the swing animation plays but no weapon is in hand, because the
		///     client never put the NPC into combat stance. Entering sends <see cref="NpcCombatModeValue" /> and leaving
		///     sends <see cref="NpcIdleStateValue" />. Only viewers who already know the handle get it, as with
		///     <see cref="BroadcastNpcMove" />.
		/// </remarks>
		public async Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			var body = CombatModeBody(npc, inCombat);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     Re-assert an already-fighting NPC's combat mode to one viewer, when that viewer has just built its dynel.
		/// </summary>
		/// <remarks>
		///     <see cref="BroadcastNpcCombatMode" /> fires on the engage transition and only reaches viewers who already
		///     know the handle, so a mob that aggroed before the player could see it would never draw its weapon for
		///     that player and would swing empty-handed for the rest of the fight.
		///     <para>
		///         Measured, and a one-to-one match with what was on screen: over one five-pirate fight the server sent
		///         1,315 stat-update frames and exactly one went to an NPC - handle 0x65, the pirate the player had
		///         selected, which transitioned later through the retaliation path when its handle was already known.
		///         The other four aggroed by proximity on the tick the player arrived and are exactly the four
		///         rendering sheathed.
		///     </para>
		///     <para>
		///         Retail does not treat this as a one-shot either: over all 138 dumps (10,373 well-formed stat-update
		///         frames) there are 593 stat-20 records across 43 captures, and per capture and handle 25% are sent
		///         more than once, one of them 28 times.
		///     </para>
		/// </remarks>
		public static Task SendNpcCombatMode(INetworkClient client, WorldEntity npc)
		{
			return WorldIir.Send(client, CombatModeBody(npc, true));
		}

		private static byte[] CombatModeBody(WorldEntity npc, bool inCombat)
		{
			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, inCombat
				? NpcCombatModeValue
				: NpcIdleStateValue);

			return StatUpdateCodec.Build(npc.Handle, stats);
		}

		/// <summary>
		///     The "this entity is alive and idling" assert: a short-form behaviour record naming the one fixed idle
		///     behaviour, sent to the client that has just confirmed building this NPC's dynel.
		/// </summary>
		/// <remarks>
		///     Once per entity per viewer, which is what retail does. Measured over the corpus
		///     (<c>--report ambient-animation</c>): 37,965 records over 7,969 actors, p50 one record per actor, p90 two.
		///     Retail asserts it as an entity comes into view and then stops. Streaming it as a per-NPC heartbeat
		///     instead reaches 119.6 records a second at one client in Old Tarantia.
		///     <para>
		///         The apparent continuous stream in the corpus is one actor in one capture - a necromancer's pet, which
		///         takes 1,250 of that capture's 1,353 records in bursts, 60 inside one second, with a p50 gap of
		///         0.000 s. What genuinely recurs for an ordinary NPC is the ambient animation, a different command -
		///         see <see cref="BroadcastNpcAmbientAnimation" />.
		///     </para>
		///     <para>
		///         Confirmed-gated by construction, the caller being the confirm, because a record for a handle the
		///         client knows but has not built crashes its loader on a null dynel.
		///     </para>
		/// </remarks>
		public static Task SendNpcIdleAssert(INetworkClient recipient, WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(recipient);
			ArgumentNullException.ThrowIfNull(npc);

			return WorldIir.Send(recipient, BehaviorControlCenterCodec.Build(BehaviorAnimations.Heartbeat(npc.Handle, ++npc.HeartbeatTick)));
		}

		/// <summary>
		///     The NPC performs one of its creature's ambient idle animations: an emote record naming the animation the
		///     tick picked and its own length, so the client both plays it and knows how long it runs.
		/// </summary>
		/// <remarks>
		///     The same confirmed gating as <see cref="SendNpcIdleAssert" />, because the animation plays on the dynel
		///     and a still-loading viewer has nothing to play it on. A no-op when the tick picked nothing: a creature
		///     with no captured animations never gets here.
		/// </remarks>
		public async Task BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			if (npc.Presentation.AmbientAnimationPicked is not { } animation)
				return;

			var record = BehaviorControlCenterCodec.Build(
				BehaviorAnimations.AmbientEmote(npc.Handle, animation.AnimationId, npc.NextBehaviorPhase(), animation.DurationCentiseconds));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Confirmed.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		/// <summary>
		///     The companion-follow step, in the shape retail sends it: a mode-04 list whose second child binds the task
		///     to the leader, rather than the bare path <see cref="BroadcastNpcMove" /> sends for a chase.
		/// </summary>
		/// <remarks>
		///     A bare path is a line the client interpolates and then sits at the end of, so the server has to re-anchor
		///     it every few units and the NPC snaps between segments. The bound form hands the follow to the client,
		///     which then owns the position and the walk animation together.
		/// </remarks>
		public async Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			ArgumentNullException.ThrowIfNull(npc);
			ArgumentNullException.ThrowIfNull(leader);

			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.FollowLeader(npc.MoveFrom, npc.Position, leader.Handle)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     An NPC moved a retail-sized segment - chasing a target, or returning home - so send its move task to
		///     every player in range that already sees it.
		/// </summary>
		/// <remarks>
		///     The tick's throttle only queues this once the NPC has covered a full broadcast segment, about six units,
		///     which is retail's chase step, so the client interpolates a real segment over time instead of receiving a
		///     one-unit snap every tick. Only viewers who know the handle get it; anyone else would receive a move for
		///     an entity they do not have.
		/// </remarks>
		public async Task BroadcastNpcMove(WorldEntity npc)
		{
			// The mode-1 walk form, which the client interpolates over time, rather than the mode-2 transform, which is
			// a position set the client snaps to. The segment runs from MoveFrom to Position - the distance the
			// throttle accumulated - so the client renders the NPC where the server model has it, walking that segment,
			// rather than lagging behind a far-goal path while the model jumps into melee, which desynchronises: the
			// player takes hits from an NPC the client still shows mid-walk. The walk body's trailer must anchor at the
			// segment end; anchoring it at the start makes the walk net-zero.
			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.Walk(npc.MoveFrom, npc.Position)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     An entity jumped: it is at a new position and did not walk there. The mode-02 form is retail's own for
		///     that, a step the client snaps to, which is why <see cref="BroadcastNpcMove" /> does not use it for the
		///     per-tick chase. Here the snap is the point.
		/// </summary>
		/// <remarks>
		///     This only reaches viewers who already hold the handle and are still in range of the destination. A jump
		///     large enough to leave someone's interest radius is not a move for them at all - it is a despawn, and for
		///     a player near the destination it is a spawn. <c>WorldSimulation.RelocateEntity</c> runs the interest pass
		///     that settles both; this call covers only the third case, the viewer who watches it go.
		/// </remarks>
		public async Task BroadcastEntitySnap(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			var body = MoveTaskCodec.Build(new MoveTaskIIR(entity.Handle, entity.Position, MoveTaskNodes.Rotate(entity.Position, entity.Rotation)));

			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(entity.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     Re-issue a patrolling NPC's captured route to every in-range viewer that already sees it, so it keeps
		///     walking its loop. The tick fires this on each patrol cycle, as retail re-paths idle patrols.
		/// </summary>
		public async Task BroadcastNpcPatrol(WorldEntity npc)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
					await SendPatrol(viewer.Client, npc);
			}
		}

		/// <summary>
		///     An NPC respawned, its timer having elapsed on the tick: re-render it for every player in range by
		///     re-sending its stored FA015807 spawn body, which the send re-frames and position-patches per recipient.
		/// </summary>
		/// <remarks>
		///     This is the only delivery that carries the spawn-animation flag. Retail sets it when the entity is
		///     created or recreated, measured by <c>--report spawn-animation</c>'s respawn test, which matches a death
		///     to the same template coming back at the same spot and finds 20 of 35 such respawns flagged against 8.1
		///     expected from those same templates' other first sightings. The cleanest lines are the solitary animals,
		///     where a positional match cannot pick up a pack-mate standing nearby: Panther 3/3 flagged against 1/68,
		///     Wild Cow 2/2 against 0/147, Wild Pig 1/1 against 0/83.
		///     <para>
		///         This deliberately under-sends relative to retail, which also flags 6 to 9% of ordinary first
		///         sightings - creatures that respawned before the player arrived and are still inside the window - and
		///         that cannot be reproduced without a duration the corpus does not contain, since the gap from death
		///         to delivery has an unmeasured respawn timer inside it. Flagging only the respawns a player witnesses
		///         is the justifiable subset, and it is the one that matters visually. It also avoids a rule keyed to
		///         "the entity was created recently", which would flag every NPC in the zone on the first login after a
		///         server start, because the world builds them all at load.
		///     </para>
		/// </remarks>
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				// Re-add to Known: the corpse's quit removed the handle, so the viewer no longer has it. Adding it
				// gates the spawn to viewers who need it and restores the invariant that Known means "was sent", so
				// the later move and combat broadcasts, which check Known, reach them.
				if (viewer.Client != null && viewer.Knowledge.Known.Add(npc.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, npc, spawnAnimation: true); // no route replay: the tick drives the wander
			}
		}

		public async Task BroadcastNpcStop(WorldEntity npc)
		{
			var facing = npc.MoveFrom.FaceTowards(npc.Position);
			var record = StopCodec.Build(npc.Handle, npc.Position, facing);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		/// <summary>
		///     Despawns a non-player entity by handle in the calling player's zone: the n3ToClientQuit removal to every
		///     player in range, and a drop from the model. The complement of <see cref="SpawnNpcForPlayer" />, and it
		///     works on burst NPCs and loot bags too.
		/// </summary>
		/// <returns>The removed entity, or null when the handle is unknown or belongs to a player.</returns>
		public async Task<WorldEntity?> DespawnEntityForPlayer(INetworkClient client, uint entityHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(entityHandle, out var entity) || entity.IsPlayer)
				return null;

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				// Only despawn for a viewer that actually has this handle: a despawn for a handle the recipient was
				// never sent crashes it. The handle comes out of Known as it goes.
				if (viewer.Client != null && viewer.Knowledge.Known.Remove(entityHandle))
					await _broadcaster.SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entityHandle);
			}

			_sim.World.Remove(entity);
			_logger.LogInformation("Despawn: removed {Name} 0x{Handle:X8} (by {Player}).", entity.Name, entityHandle, player.Name);
			return entity;
		}

		/// <summary>
		///     Ambient pool maintenance, called on a slow cadence from the simulation tick: keep each loaded zone's
		///     pools at their MaxCount.
		/// </summary>
		/// <remarks>
		///     A killed member fades and is removed at the end of its corpse sequence, dropping the pool's live count.
		///     One replacement per under-strength pool per pass is spawned at a free point and streamed to nearby
		///     players, so the cadence is itself the respawn delay.
		/// </remarks>
		public async Task MaintainAmbientPools(Zone zone)
		{
			// The service spawns the deficit and yields each member as it goes; the streaming is this method's.
			// Because Replenish is a lazy iterator, this keeps the spawn-then-send order rather than spawning every
			// pool's deficit up front.
			foreach (var (entity, _, pos) in _ambient.Replenish(zone))
			{
				foreach (var viewer in zone.PlayersInRange(pos, WorldSimulation.PlayerInterestRange))
				{
					if (viewer.Client != null && viewer.Knowledge.Known.Add(entity.Handle))
						await _broadcaster.SendWorldSpawn(viewer.Client, entity);
				}
			}
		}

		/// <summary>
		///     Make the given NPC follow the client's player, or stop following. The world tick then steps it toward
		///     the player and broadcasts a move task each step.
		/// </summary>
		/// <remarks>Used by the <c>.npcfollow</c> GM command and by the escort rescue trigger.</remarks>
		/// <returns>The NPC, or null when the handle is not a non-player entity in the caller's zone.</returns>
		public WorldEntity? SetNpcFollow(INetworkClient client, uint npcHandle, bool follow)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) || npc.IsPlayer)
				return null;

			npc.FollowTargetHandle = follow
				? player.Handle
				: null;
			_logger.LogInformation("Follow: {Name} 0x{Handle:X8} {State} {Player}.", npc.Name, npcHandle, follow
				? "now follows"
				: "stopped following", player.Name);
			return npc;
		}

		/// <summary>
		///     Spawns a captured NPC, matched by name - the stable key - at the player's position and renders it for
		///     nearby players.
		/// </summary>
		/// <remarks>
		///     The body is built from typed columns as the map-load spawns are, then re-framed and position-patched per
		///     recipient by the send. When killable, the entity gets its vitals, level and hostility from its row
		///     exactly as the zone load does, so it joins combat and loot as a full world citizen.
		/// </remarks>
		/// <returns>The spawned entity, or null when no spawnable row matches the name.</returns>
		public async Task<WorldEntity?> SpawnNpcForPlayer(INetworkClient client, string npcName, bool killable = true)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var npc = await _database.GetNpcSpawnByName(npcName);
			if (npc == null)
			{
				_logger.LogInformation("Spawn: no spawnable '{NpcTemplate}' template found.", npcName);
				return null;
			}

			var handle = _handles.Next(); // a fresh summoned-entity handle, which cannot collide with a player or NPC
			// Built wholly from columns. The send re-stamps the position and playfield token per recipient, so the
			// values baked in here are placeholders.
			var body = NpcSpawnBuilder.BuildLive(npc, handle, _sim.World.ZoneForEntity(player).PlayfieldToken);

			EntityVitals? vitals = null;
			if (killable)
			{
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			}

			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npc.NpcTemplate.Name ?? npcName, vitals, npc.NpcTemplateId);
			entity.InstanceId = player.InstanceId; // spawn into the summoner's map instance - see SpawnPetForPlayer
			entity.Level = npc.Level;
			entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment); // the combat kit, as in the zone-load path
			entity.AttackRange = NpcWeapon.AttackRange(npc.Equipment, WorldState.NpcMeleeRange);
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment);
			entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level, npc.NpcTemplate.StatCore?.ComboClass);
			entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
			entity.Hostile = killable && entity.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId; // drop this type's container on death
			entity.FactionId = npc.NpcTemplate.StatCore?.FactionId;
			entity.NpcType = npc.NpcTemplate.StatCore?.NpcType;
			entity.NpcFlags = npc.NpcTemplate.StatCore?.NpcFlags;
			entity.SpawnBody = body;
			entity.Route.NoRespawn = true; // a summoned NPC does not revive; it fades out after death

			// Load this NPC type's caster spells, as the zone-load path does, so a summoned caster casts instead of
			// only meleeing: the simulation's cast branch fires once the entity carries spells.
			if (entity.Name is { Length: > 0 } spellName)
			{
				foreach (var s in await _database.GetNpcSpellsByName(spellName))
					entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));
			}

			_sim.World.ZoneForEntity(player).Add(entity);

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// Register the spawn in the viewer's Known set. The follow and patrol move tasks are Known-gated, and the
				// client only renders a spawned model once a move task arrives at its position, so a directly spawned pet
				// or NPC that is not in Known sends its spawn record but never its move task, and stays invisible.
				if (viewer.Client != null && viewer.Knowledge.Known.Add(entity.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Spawn: spawned {NpcTemplate} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
			return entity;
		}

		/// <summary>
		///     Spawns a creature type at the player as a pet: no world placement row, the spawn record assembled from
		///     the type's own columns at the summoner's position and streamed to nearby viewers.
		/// </summary>
		/// <remarks>
		///     The owner assignment and the follow are the caller's. The builder throws if the type carries no lifted
		///     stat core, which is the fail-fast signal for un-lifted or partially imported data.
		/// </remarks>
		public async Task<WorldEntity?> SpawnPetForPlayer(INetworkClient client, NpcTemplate npcType)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var handle = _handles.Next();
			// Bake the summoner's zone's 4-byte token: a zero token ships a 1-byte one and desyncs the client's version
			// read.
			var body = NpcSpawnBuilder.BuildFromType(npcType, handle, player.Position, player.Rotation, _sim.World.ZoneForEntity(player).PlayfieldToken);

			var level = npcType.StatCore?.Level ?? 0;
			var maxHealth = level > 0
				? CombatResolver.NpcMaxHealthForLevel(level)
				: 1;
			var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals, npcType.TemplateId);
			entity.RangedAttacker = NpcWeapon.IsRanged(npcType.Equipment); // the combat kit comes from the type: a pet has no placement row
			entity.AttackRange = NpcWeapon.AttackRange(npcType.Equipment, WorldState.NpcMeleeRange);
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npcType.Equipment);
			entity.ClassComboIds = await _combos.NpcClassComboIds(npcType.Equipment, level, npcType.StatCore?.ComboClass);
			// The pet belongs to the summoner's map instance rather than the default one. Without this the pet's zone
			// lookup resolves a different zone and token from the player's, so the send re-stamps the wrong playfield
			// token and the client silently drops the spawn: no pet bar and no model.
			entity.InstanceId = player.InstanceId;
			entity.Level = level;
			entity.SpawnBody = body;
			entity.Route.NoRespawn = true;

			foreach (var s in npcType.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			_sim.World.ZoneForEntity(player).Add(entity);
			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// Register the spawn in the viewer's Known set. The follow and patrol move tasks are Known-gated, and the
				// client only renders a spawned model once a move task arrives at its position, so a directly spawned pet
				// or NPC that is not in Known sends its spawn record but never its move task, and stays invisible.
				if (viewer.Client != null && viewer.Knowledge.Known.Add(entity.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Pet: spawned creature type {Type} 0x{Handle:X8} for {Player}.", npcType.Name, handle, player.Name);
			return entity;
		}

		/// <summary>
		///     Resolves the player's own instance rather than the default one, then looks the NPC up in that copy.
		/// </summary>
		/// <remarks>
		///     A Solo-instanced map puts the player and their escortee in a private playfield copy, not the shared
		///     instance 0, so a default-instance lookup misses the escortee entirely and the follow silently does
		///     nothing.
		/// </remarks>
		private bool SetNpcFollow(uint mapId, uint playerHandle, bool follow, Func<WorldEntity, bool> match, string label)
		{
			var zone = _sim.World.ZonesForMap(mapId).FirstOrDefault(z => z.TryGet(playerHandle, out _));

			var npc = zone?.Entities.FirstOrDefault(e => !e.IsPlayer && match(e));
			if (npc == null)
				return false;

			npc.FollowTargetHandle = follow
				? playerHandle
				: null;
			_logger.LogInformation("Follow: {Label} {State} player 0x{Player:X8}.", label, follow
				? "now follows"
				: "stopped following", playerHandle);
			return true;
		}

		/// <summary>
		///     Issue the NPC's captured patrol route as a move task so the client walks it: retail drives idle patrol
		///     client-side, sending the route once per spawn or respawn rather than a step per tick.
		/// </summary>
		/// <remarks>
		///     The anchor is the NPC's live position, because the client's render gate requires the move task's position
		///     to match the spawn it was just sent; the waypoints are absolute captured world coordinates.
		/// </remarks>
		private static Task SendPatrol(INetworkClient recipient, WorldEntity npc)
		{
			if (!npc.Route.Patrols)
				return Task.CompletedTask;

			return WorldIir.Send(recipient, npc.Route.WalksRoute ? BuildRouteBody(npc) : npc.Route.PatrolBody);
		}

		/// <summary>The beat as this NPC is about to walk it, anchored where it now stands.</summary>
		/// <remarks>
		///     The captured patrol body is anchored at the entity's spawn, so re-issuing it at the end of a lap, or to a
		///     viewer arriving mid-beat, tells the client to restart the route from the beginning, which reads as the
		///     NPC teleporting back to the start. Rebuilding from the live position removes the jump.
		///     <para>
		///         A rebuild must supply the trailer's speed slot, which paces the walk; without it the client paces
		///         off something else and crawls. It is supplied as the walk speed
		///         (<see cref="WorldState.RouteWalkSpeed" />) rather than the captured scalar, which is not a ground
		///         speed and makes the fast-scalar beats sprint.
		///     </para>
		/// </remarks>
		private static byte[] BuildRouteBody(WorldEntity npc)
		{
			var ahead = PatrolRoute.FromHere(npc.Route.PatrolWaypoints, npc.Route.RouteIndex, npc.Route.RouteBackwards, npc.Route.RouteLoops);
			if (ahead.Count == 0)
				return npc.Route.PatrolBody;

			// The walk starts where the NPC is standing, so its live position is the path's first waypoint. Beginning
			// the path at the next waypoint instead leaves the client to cover the gap from wherever it has the NPC
			// onto the route before the timed walk begins, which it does at its own speed - the catch-up dash, visible
			// only on the first body after a spawn, since every later re-issue fires at a turnaround where the NPC is
			// already standing on that waypoint and the gap is zero.
			//
			// It also makes the duration below exact rather than approximate: it is the length of the path actually
			// sent, not of a path plus an approach leg the client paces separately.
			var path = new List<Vector3>(ahead.Count + 1) { npc.Position };
			path.AddRange(ahead);

			// State how long the walk takes, so the client is paced by time rather than by its own movement-speed
			// attribute, which for an NPC is its run speed and makes it sprint the beat and then stand through the
			// remainder.
			var seconds = PatrolRoute.TotalLength(npc.Position, ahead) / npc.Route.RouteSpeed;

			return MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.Position,
				MoveTaskNodes.PatrolTimed(path, npc.Route.RouteSpeed, npc.Position, seconds, seconds)));
		}
	}
}
