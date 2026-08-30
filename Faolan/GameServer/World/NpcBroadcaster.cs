using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// What an NPC DOES, told to everyone who can see it (#826): aggro, turning hostile, entering and leaving combat
	// mode, its idle assert and ambient animation, its movement / patrol route / follow, its respawn and its scripted
	// exit - plus putting a runtime NPC or pet into the world in the first place, and taking one back out.
	//
	// The seam against `WorldBroadcaster` is not the subject (both are about a `WorldEntity`) but the QUESTION:
	// the broadcaster answers **who can see what** - the interest pass and the spawn / despawn / move wire it
	// produces - while this answers **what this creature is doing**. Nothing in the broadcaster calls in here, which
	// is the property that makes the split legal; this calls out for the three sends the visibility system owns
	// (`SendWorldSpawn`, `SendWorldDespawn`, `BroadcastEntityDespawn`).
	//
	// ⚠️ `RuntimeHandleAllocator` is SHARED with the broadcaster rather than owned here. Loot bags are minted there
	// and NPCs / pets / ambient members here, and a second counter would hand out colliding handles - so it is built
	// once in the GameServer ctor and injected into both.
	public sealed class NpcBroadcaster
	{
		// The combat-mode stat value the captured beach melee mobs send at aggro (strickenbla capture, #218): the tick
		// before each mob's first swing it pushes `CombatMode = 4`, which is what draws its weapon. (The player uses 1
		// and one non-swinging entity used 1 - the field is a stance/mode code that isn't fully decoded, but 4 is the
		// value every actual melee swinger sends.)
		private const uint NpcCombatModeValue = 4;

		// A scripted exit's two beats, from the farewell window closing - see ScheduleScriptedExit for the frames.
		private const int ScriptedExitMoveDelayMs = 4000;
		private const int ScriptedExitDespawnDelayMs = 1200;

		private readonly AmbientPoolService _ambient;
		private readonly WorldBroadcaster _broadcaster;
		private readonly ComboService _combos;
		private readonly IDatabaseRepository _database;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public NpcBroadcaster(WorldSimulation sim, IDatabaseRepository database, WorldBroadcaster broadcaster, AmbientPoolService ambient, ComboService combos,
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

		// Make the named NPC in `mapId`'s zone follow (or stop following) the given player handle - the quest-driven
		// entry point (e.g. Casilda after her rescue stage), keyed by NAME since handles are session-volatile. No-op
		// (returns false) if no such NPC is loaded in the zone.
		// #262: make the NPC of TEMPLATE id `templateId` in `mapId`'s zone follow (or stop following) the player - the
		// id-based escort companion trigger (prefer ids over names). The escortee's template is the Escort goal's
		// TrailingRefId (e.g. Casilda 9411). See SetNpcFollow for the instance-aware zone resolution.
		public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow)
		{
			return SetNpcFollow(mapId, playerHandle, follow, e => e.NpcTemplateId == templateId, $"template {templateId}");
		}

		// An NPC engaged a target: push its aggro to every viewer in range that sees it - a HateListUpdate (the NPC now
		// hates the target), a ThreatLevel, and the NPC's target LOCK, so the threat/aggro is visible the tick combat
		// starts (#216). Additive to the combat-mode weapon-draw; gated with the rest of the combat sim by the caller.
		//
		// The three go out in retail's order: `NecroL80_..._login_combat` shows LockedToChanged arriving right after a
		// HateListUpdate/ThreatLevel pair. LockedToChanged carries [npc handle] -> [target ref], and every captured
		// instance is a low NPC handle locking onto a PLAYER, which is the aggro moment (#405).
		//
		// ⚠️ It goes to VIEWERS, not just to the target, and that is measured rather than assumed: in
		// `login_2-clients_group-trade-friend` the same (npc, target) pair is sent on BOTH clients' streams (frames
		// 3146/3147 and 3394/3395) even though only one of them is the target. A single-client capture cannot tell
		// those apart, which is why the two-client one is the one that settles it.
		public async Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			if (target == null)
				return;

			// ⚠️ The hate/threat records are addressed to the TARGET, not to the NPC - it is the PLAYER's threat list
			// that gains an entry, not the creature's. Measured over the whole corpus: 270 of 270 HateListUpdates carry
			// the session player as the subject and name the other party as the target, and ThreatLevel is the player's
			// on 681 of 712. Built the other way round they are addressed to a handle the client has no threat list
			// for, so it drops them - and with them the re-poll that follows: retail's client answers this pair by
			// asking RequestAggroStatus for the NPC again (frame 8482 in the Saddur fight) and only then learns the
			// creature turned hostile. That is why a mob could attack a player while still drawing GREEN and refusing
			// to be attacked back (#839): the flip reached the server model and nothing told the client.
			var hate = HateListUpdateCodec.Build(target.Handle, npc.Handle);
			// Retail opens with threat 0 and then 1 - an initialise followed by the first real level, both on the
			// player - and climbs from there as damage accrues (3 by the second swing in that fight).
			var threatOpen = ThreatLevelCodec.Build(target.Handle, 0, 1);
			var threat = ThreatLevelCodec.Build(target.Handle, 1, 1);
			// LockedToChanged is the one that IS the NPC's: [npc] -> [target], exactly as captured (#405).
			var lockedTo = LockedToChangedCodec.Build(npc.Handle, target.Handle);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
				{
					await WorldIir.Send(viewer.Client, hate);
					await WorldIir.Send(viewer.Client, threatOpen);
					await WorldIir.Send(viewer.Client, threat);
					await WorldIir.Send(viewer.Client, lockedTo);
				}
			}
		}

		// A scripted EXIT: the NPC walks to a point and then leaves the world (#839). Casilda's `RunToTortage` is the
		// case it exists for - her escort is over, so she heads for the city gate and is gone.
		//
		// The shape is retail's, measured over BOTH beach-to-city captures, and they agree to within 0.3 s. Timed from
		// the farewell window closing (`KnubotCloseChatWindow`):
		//
		//   +4.0 s   a MoveTask carries her the last few units to the gate
		//   +5.2 s   n3ToClientQuit - she is gone
		//
		// (`xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` fr11419 close, fr11438 move, fr11446 quit;
		// `xxx_mage-demo_login_play_beach-to-enter-tortage_city` fr10239, fr10254, fr10264.)
		//
		// ⚠️ Scheduled rather than awaited: the caller is finishing a conversation, and holding its reply chain for
		// five seconds would stall the window's own close.
		public void ScheduleScriptedExit(WorldEntity npc, Vector3 exit)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var zone = _sim.World.ZoneForEntity(npc);

			Task.RunAfter(ScriptedExitMoveDelayMs, async () =>
			{
				// She may already be gone - the player zoned, the zone reloaded, or a second trigger fired. An exit
				// for an entity that has left is dropped rather than broadcast against a stale handle.
				if (!zone.TryGet(npc.Handle, out _))
					return;

				npc.MoveFrom = npc.Position;
				npc.Position = exit;
				await BroadcastNpcMove(npc);

				Task.RunAfter(ScriptedExitDespawnDelayMs, async () =>
				{
					if (!zone.TryGet(npc.Handle, out _))
						return;

					await _broadcaster.BroadcastEntityDespawn(npc);
					zone.Remove(npc.Handle);
					_logger.LogInformation("Script: {Npc} reached its exit and left the world.", npc.Name);
				}, _logger);
			}, _logger);
		}

		// An NPC that was standing in the world PEACEFUL has just turned on the player: restate its collision group to
		// the mob one, for every viewer that already knows it.
		//
		// ⭐ This is what makes the flip real to the CLIENT, and without it the creature is hostile on the server only:
		// it swings, the hate list arrives, the re-poll answers cCategory 1 - and the player still sees a peaceful
		// nameplate and cannot hit back. Saddur was the reported case (#839): he attacked for 8 damage while refusing
		// to be attacked, because his `CarsGroup` was still 1, the townsfolk group he spawned in.
		//
		// Measured rather than guessed. Over all 134 captures EVERY character `CarsGroup` record carrying the mob group
		// is one of the three captured Saddur fights - three captures, three handles, no other occurrence - and in all
		// three the order is identical: `SendScryEvent "Fight"`, then this record, then the hate list, then the client's
		// own re-poll. So it is the first thing retail sends on the flip, ahead of everything we already send.
		//
		// ⚠️ Two things here are copied from the capture rather than reasoned out, and both are marked as such:
		// the record is a CHARACTER one carrying the world-object `env` byte (0x00, not the 0x01 characters normally
		// take - the same "restate the dynel layer first" shape `SkillPointService` documents), and retail sends it
		// TWICE, as two packets in one segment, in all three captures. [V0: why the second copy.]
		public async Task BroadcastNpcTurnedHostile(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var body = StatCodec.Build(StatIirs.ForCharacter(npc.Handle, (uint)CharacterStatId.CarsGroup, NpcStatCore.MobCarsGroup, StatIirs.WorldObjectEnv));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Known.Contains(npc.Handle))
					continue;

				await WorldIir.Send(viewer.Client, body);
				await WorldIir.Send(viewer.Client, body);
			}
		}

		// An NPC engaged or disengaged combat: push the combat-mode stat (stat 20) to every viewer in range that sees it,
		// so the client DRAWS (enter) or SHEATHES (exit) the NPC's weapon. Without it the NPC swings empty-handed (#218) -
		// the swing animation plays but no weapon is in hand because the client never put the NPC into combat stance.
		// Enter sends the captured melee-mob value (4); exit sends 0 (the idle default - a spawn carries no stat-20 record
		// at all, so 0 sheathes; the sheathe value isn't separately captured). Only viewers who already know the handle
		// get it (mirrors BroadcastNpcMove).
		public async Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, inCombat
				? NpcCombatModeValue
				: 0u);
			var body = StatUpdateCodec.Build(npc.Handle, stats);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// The "this entity is alive and idling" assert (#225): a short-form BehaviorControlCenterIIR (BehaviorId.BccAction)
		// naming the one fixed idle behaviour, sent to the client that has just CONFIRMED building this NPC's dynel.
		//
		// ⚠️ ONCE per entity per viewer, which is what retail does and is not what this used to do. It was streamed as a
		// per-NPC ~1/s heartbeat - and #716 then had to cap the aggregate, because in Old Tarantia that was 119.6
		// BCC/s at one client. Measured over the corpus (`--report ambient-animation`): 37,965 records over 7,969
		// actors, **p50 1 record per actor**, p90 2. Retail asserts it as an entity comes into view and stops.
		//
		// The "continuous ~10/s per rendered entity" #225 was filed on was ONE actor in one capture - the necromancer's
		// Nemedian Deathstalker pet, which takes 1,250 of that capture's 1,353 records in bursts (60 inside one second,
		// p50 gap 0.000s). Generalising that actor to every entity is what produced the flood. What genuinely recurs for
		// an ordinary NPC is the ambient ANIMATION, a different command - see BroadcastNpcAmbientAnimation (#766).
		//
		// Confirmed-gated by construction (the caller is the confirm), because a record for a Known-but-not-Confirmed
		// handle hits the pcDynel-NULL loader crash - the hang the original heartbeat experiment ran into.
		public Task SendNpcIdleAssert(INetworkClient recipient, WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(recipient);
			ArgumentNullException.ThrowIfNull(npc);

			return WorldIir.Send(recipient, BehaviorControlCenterCodec.Build(BehaviorAnimations.Heartbeat(npc.Handle, ++npc.HeartbeatTick)));
		}

		// The NPC performs one of its creature's ambient idle animations (#766): a BehaviorId.Emote record naming the
		// animation the TICK picked and its own length, so the client both plays it and knows how long it runs. Same
		// Confirmed gating as the heartbeat - the animation plays ON the dynel, so a still-loading viewer has nothing to
		// play it on. No-op when the tick picked nothing (a creature with no captured animations never gets here).
		public async Task BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			if (npc.AmbientAnimationPicked is not { } animation)
				return;

			var record = BehaviorControlCenterCodec.Build(
				BehaviorAnimations.AmbientEmote(npc.Handle, animation.AnimationId, npc.NextBehaviorPhase(), animation.DurationCentiseconds));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Confirmed.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// An NPC moved a retail-sized segment (chasing a target, or returning home): send its MoveTask to every player in
		// range that already sees it. The body is the 61-byte transform form for the SEGMENT (MoveFrom -> Position, facing
		// the travel direction); the throttle in WorldState.Tick (#272) only queues this once the NPC has covered
		// MoveBroadcastSegment (~6u, retail's chase step) since the last one, so the client interpolates a real segment
		// over time instead of getting a 1.2u snap every tick (the jitter/teleport). Only viewers who know the handle get
		// it (else they'd get a move for an unknown entity).
		// #827: the companion-follow step, in the shape retail sends it - a mode-04 LIST whose second child BINDS the
		// task to the leader (MoveTaskNodes.FollowLeader), not the bare path `BroadcastNpcMove` sends for a chase.
		//
		// The bare path is why a follower slid: it is a line the client interpolates and then sits at the end of, so
		// the server re-anchors it every ~6u and the NPC snaps between segments. The bound form hands the follow to
		// the client, which then owns the position and the walk animation together.
		public async Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			ArgumentNullException.ThrowIfNull(npc);
			ArgumentNullException.ThrowIfNull(leader);

			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.FollowLeader(npc.MoveFrom, npc.Position, leader.Handle)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		public async Task BroadcastNpcMove(WorldEntity npc)
		{
			// #272: send the mode-1 PathMoveTask WALK form (the client interpolates the segment over time) rather than the
			// mode-2 RotateMoveTask transform, which is a position-SET the client SNAPS to. The segment is MoveFrom ->
			// Position (the ~6u the throttle accumulated), so the client renders the NPC where the server model has it -
			// walking that segment - rather than lagging behind a far-goal path while the model jumps into melee (which
			// desyncs: the player takes hits from an NPC the client still shows mid-walk). The earlier path attempt reverted
			// because its trailer anchored at the START (walk then back = net-zero); BuildWalkBody anchors it at the segment
			// end (Position). Residual: consecutive segments still re-spline (a small snap) - the remaining #272 polish.
			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.Walk(npc.MoveFrom, npc.Position)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// #266: (re-)issue a patrolling NPC's captured route to every in-range viewer that already sees it, so it keeps
		// walking its loop (the tick fires this on each patrol cycle; retail re-paths idle patrols). Only known handles.
		public async Task BroadcastNpcPatrol(WorldEntity npc)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await SendPatrol(viewer.Client, npc);
			}
		}

		// An NPC respawned (its timer elapsed on the tick): re-render it for every player in range by re-sending its
		// stored FA015807 spawn body (re-framed + position-patched per recipient by SendWorldSpawn).
		//
		// ⭐ #803: this delivery carries `npc_stats.spawn_animation`, and it is the ONLY one that does. Retail sets
		// the flag when the entity is (re)created - measured by `--report spawn-animation`'s respawn test, which
		// matches a death to the same template coming back at the same spot and finds 20 of 35 such respawns flagged
		// against 8.1 expected from those same templates' OTHER first-sightings. The cleanest lines are the solitary
		// animals, where a positional match cannot pick up a pack-mate standing nearby: Panther 3/3 flagged against
		// 1/68, Wild Cow 2/2 against 0/147, Wild Pig 1/1 against 0/83.
		//
		// ⚠️ This UNDER-sends relative to retail, deliberately. Retail flags ~6-9% of ordinary first-sightings too -
		// creatures that respawned before the player arrived and are still inside the window - and we cannot
		// reproduce that without a duration the corpus does not contain (the death->delivery gap has an unmeasured
		// respawn timer inside it, so no flagged delivery bounds the window from below). Flagging only the respawns a
		// player actually witnesses is the subset we can justify, and it is the subset that matters visually. It also
		// sidesteps the failure #803 warned about: a rule keyed to "the entity was created recently" would flag every
		// NPC in the zone on the first login after a server start, because our world builds them all at load - a
		// respawn is a distinct event from world-load creation, so keying on it cannot do that.
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				// Re-add to Known: the corpse's quit (#246) removed the handle, so the player
				// no longer has it. Known.Add gates the spawn to viewers who actually need it + restores the #245
				// invariant (Known == "was sent"), so later move/combat broadcasts (which check Known) reach it.
				if (viewer.Client != null && viewer.Known.Add(npc.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, npc, spawnAnimation: true); // #266: no route replay - the tick drives a server-side wander
			}
		}

		public async Task BroadcastNpcStop(WorldEntity npc)
		{
			var facing = npc.MoveFrom.FaceTowards(npc.Position);
			var record = StopCodec.Build(npc.Handle, npc.Position, facing);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// Despawns a non-player entity by handle in the calling player's zone: the decoded n3ToClientQuit removal
		// to every player in range + drop from the model (the cleanup complement to SpawnNpcForPlayer; also works
		// on burst NPCs and loot bags). Returns the removed entity, or null when the handle is unknown or a player.
		public async Task<WorldEntity?> DespawnEntityForPlayer(INetworkClient client, uint entityHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(entityHandle, out var entity) || entity.IsPlayer)
				return null;

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				// #245 invariant: only despawn for a viewer that actually has this handle (a despawn for a handle the
				// recipient was never sent crashes it). Removes it from Known as it goes.
				if (viewer.Client != null && viewer.Known.Remove(entityHandle))
					await _broadcaster.SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entityHandle);
			}

			_sim.World.Remove(entity);
			_logger.LogInformation("Despawn: removed {Name} 0x{Handle:X8} (by {Player}).", entity.Name, entityHandle, player.Name);
			return entity;
		}

		// #363 pool maintenance (called on a slow cadence from the sim tick): keep each loaded zone's ambient pools at
		// MaxCount. A killed member fades + is removed at the end of its corpse sequence (NpcCorpseRemoved), dropping
		// the pool's live count; here we respawn ONE
		// replacement per under-strength pool per pass (a gentle trickle - the cadence IS the respawn delay) at a free
		// point, streamed to nearby players.
		public async Task MaintainAmbientPools()
		{
			// The service spawns the deficit and yields each member as it goes; the streaming is ours. Because
			// Replenish is a lazy iterator, this keeps the original spawn-then-send order rather than spawning every
			// pool's deficit up front.
			foreach (var (entity, zone, pos) in _ambient.Replenish())
			{
				foreach (var viewer in zone.PlayersInRange(pos, WorldSimulation.PlayerInterestRange))
				{
					if (viewer.Client != null && viewer.Known.Add(entity.Handle))
						await _broadcaster.SendWorldSpawn(viewer.Client, entity);
				}
			}
		}

		// #262 companion-follow: make the NPC `npcHandle` follow `client`'s player (or stop, when follow=false). The
		// world tick (WorldState) then steps it toward the player and broadcasts a MoveTask each step. Returns the NPC,
		// or null if the handle isn't a non-player entity in the caller's zone. Used by the .npcfollow GM command and by
		// the Casilda rescue trigger.
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

		// Spawns a captured NPC (any NpcSpawns row with spawn Data, matched by NAME - the stable key) at the
		// player's position and renders it for nearby players. Built byte-exact via NpcSpawnBuilder (like the
		// map-load NPC spawns), re-framed + position-patched per recipient by SendWorldSpawn. When killable, the
		// entity gets vitals/level/hostility from its row exactly like EnsureZoneNpcs, so it joins combat + loot
		// as a full world citizen. Returns the spawned entity, or null when no spawnable row matches the name.
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

			var handle = _handles.Next(); // a fresh summoned-entity handle (high range, no player/NPC collision)
			// Build wholly from columns (#243/#244); SendWorldSpawn re-stamps the position + token per recipient, so the
			// baked ones are just placeholders (the player's zone token here).
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
			entity.InstanceId = player.InstanceId; // #268: spawn into the summoner's map instance (see SpawnPetForPlayer).
			entity.Level = npc.Level;
			entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment); // #671, as in the zone-load path
			entity.AttackRange = NpcWeapon.AttackRange(npc.Equipment, WorldState.NpcMeleeRange); // #838
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level, npc.NpcTemplate.StatCore?.ComboClass); // #675, likewise
			entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
			entity.Hostile = killable && entity.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId; // drop this type's container on death
			entity.FactionId = npc.NpcTemplate.StatCore?.FactionId; // #310
			entity.SpawnBody = body;
			entity.NoRespawn = true; // summoned test NPCs don't revive - they fade out after death

			// Load this NPC type's caster spells (same as the zone-load path) so a summoned caster actually casts
			// (#232) instead of only meleeing - the sim's cast branch lights up when entity.Spells is non-empty.
			if (entity.Name is { Length: > 0 } spellName)
			{
				foreach (var s in await _database.GetNpcSpellsByName(spellName))
					entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));
			}

			_sim.World.ZoneForEntity(player).Add(entity);

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Spawn: spawned {NpcTemplate} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
			return entity;
		}

		// Spawns a creature TYPE at the player as a PET (#307/#244) - the RIGHT model: no NpcSpawns world placement,
		// the FA015807 is assembled from the type's columns (StatCore + Equipment + Morphs) via BuildFromType at the
		// summoner's position, then streamed to nearby viewers. The PetIIR owner-assign + follow are done by the caller
		// (SummonPetForPlayer). BuildFromType throws if the type carries no lifted stat core (not yet imported/lifted).
		public async Task<WorldEntity?> SpawnPetForPlayer(INetworkClient client, NpcTemplate npcType)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			// BuildFromType throws for a type with no lifted StatCore.IirHeader - the fail-fast signal for un-lifted /
			// partially-imported data; it never happens for a shipped pet/mount type.
			var handle = _handles.Next();
			// Bake the summoner's zone token (4-byte) - see SpawnAmbientMember; a 0 token desyncs the client version read.
			var body = NpcSpawnBuilder.BuildFromType(npcType, handle, player.Position, player.Rotation, _sim.World.ZoneForEntity(player).PlayfieldToken);

			var level = npcType.StatCore?.Level ?? 0;
			var maxHealth = level > 0
				? CombatResolver.NpcMaxHealthForLevel(level)
				: 1;
			var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals, npcType.TemplateId);
			entity.RangedAttacker = NpcWeapon.IsRanged(npcType.Equipment); // #671 - from the TYPE's equipment; a pet has no NpcSpawn row
			entity.AttackRange = NpcWeapon.AttackRange(npcType.Equipment, WorldState.NpcMeleeRange); // #838
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npcType.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npcType.Equipment, level, npcType.StatCore?.ComboClass); // #675, likewise
			entity.InstanceId = player.InstanceId; // #268: the pet belongs to the SUMMONER's map INSTANCE, not the default
			// (instance 0). Without this ZoneForEntity(pet) resolves a different zone/token
			// than the player's, so SendWorldSpawn re-stamps the wrong playfield token and the
			// client silently drops the spawn - no pet bar, no model. (Broke when #268 instanced.)
			entity.Level = level;
			entity.SpawnBody = body;
			entity.NoRespawn = true;

			foreach (var s in npcType.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			_sim.World.ZoneForEntity(player).Add(entity);
			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await _broadcaster.SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Pet: spawned creature type {Type} 0x{Handle:X8} for {Player}.", npcType.Name, handle, player.Name);
			return entity;
		}

		// Resolve the PLAYER's instance, not the default one: the beach is Solo-instanced (#268), so the player and
		// their own escortee live in a private playfield copy (e.g. 0x00A00001), not the shared instance 0. Find the
		// map's live zone that actually contains this player, then look up the escortee (by `match`) in THAT copy - a
		// TryGetZone(mapId) default-instance lookup misses her entirely and the follow silently no-ops.
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

		// #266 patrol: issue the NPC's captured patrol route as a MoveTask so the CLIENT walks it (retail drives idle
		// patrol client-side - the server sends the route once per spawn/respawn, not a per-tick step). The anchor /
		// current position is the NPC's live spawn position (the render-gate requires the MoveTask position to match
		// the spawn it was just sent); the waypoints are the absolute captured world coords.
		private Task SendPatrol(INetworkClient recipient, WorldEntity npc)
		{
			if (!npc.Patrols)
				return Task.CompletedTask;

			// Encode emits a bare 0x2000 body (no length prefix), so it rides WorldIir.Send.
			return WorldIir.Send(recipient, npc.WalksRoute ? BuildRouteBody(npc) : npc.PatrolBody);
		}

		// #716: the beat as this NPC is about to walk it, anchored where it NOW stands.
		//
		// The captured `PatrolBody` is anchored at the entity's SPAWN, so re-issuing it at the end of a lap - or to a
		// viewer arriving mid-beat - told the client to restart the route from the beginning, which reads as the NPC
		// teleporting back to the start. Rebuilding from the live position removes the jump.
		//
		// Rebuilding was tried before (#266) and reverted because it "lost the speed and run-crawled": the trailer slot
		// paces the walk, and a rebuild that did not supply it left the client pacing off something else. It is supplied
		// here as the WALK speed (WorldState.RouteWalkSpeed) rather than the captured scalar - the scalar is not a ground
		// speed, and feeding it back made the fast-scalar beats sprint. See #714 for the measurement.
		private byte[] BuildRouteBody(WorldEntity npc)
		{
			var ahead = PatrolRoute.FromHere(npc.PatrolWaypoints, npc.RouteIndex, npc.RouteBackwards, npc.RouteLoops);
			if (ahead.Count == 0)
				return npc.PatrolBody;

			// ⚠️ The walk STARTS where the NPC is standing, so its live position is the path's first waypoint (#714).
			//
			// Without it the path began at `ahead[0]` - the next waypoint - and the client had to cover the gap from
			// wherever it had the NPC onto the route before the timed walk began, which it does at its own speed. That
			// is the catch-up dash seen in-client, and it appeared only on the FIRST body after a spawn: every later
			// re-issue fires at a turnaround, where the NPC is already standing on `ahead[0]` and the gap is zero.
			//
			// It also makes `seconds` exact rather than approximate: the duration is now the length of the path that is
			// actually sent, not of a path plus an approach leg the client was pacing separately.
			var path = new List<Vector3>(ahead.Count + 1) { npc.Position };
			path.AddRange(ahead);

			// State how long the walk takes, so the CLIENT is paced by time rather than by its own movement-speed
			// attribute - which for an NPC is its RUN speed, and is what made the beat get sprinted and then stood
			// through.
			var seconds = PatrolRoute.TotalLength(npc.Position, ahead) / npc.RouteSpeed;

			return MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.Position,
				MoveTaskNodes.PatrolTimed(path, npc.RouteSpeed, npc.Position, seconds, seconds)));
		}
	}
}
