using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Turns world-model events into wire packets: spawns/moves/despawns for interest management, NPC respawn/loot/mount
	// renders, loot-window and quest-journal packets (docs/server-architecture-roadmap.md). Extracted out of the
	// GameServer listener so packet CONSTRUCTION for world events is its own collaborator; it reads the world model via
	// the WorldSimulation, loads templates from the DB, and frames every packet on the world conversation (WorldFraming:
	// sender = world-system 0x58, receiver = the recipient's GameInterfaceType endpoint). The FA015807 body carries the
	// SUBJECT's handle (unchanged), so the recipient renders the subject as a separate entity. NEEDS IN-CLIENT
	// VERIFICATION with two connected players. Single-player is a no-op (no interest events).
	public sealed class WorldBroadcaster : IQuestOutbound
	{
		// DispatchSetQuestMarker (0x2008) state byte (decoded #241): 0x02 = '!' (quest available), 0x04 = '?' (ready
		// to turn in), 0x00 = none.
		private const byte QuestMarkerNone = 0x00, QuestMarkerAvailable = 0x02, QuestMarkerTurnIn = 0x04;

		// How long a dropped loot bag lingers before the tick fades + removes it (the IsLoot expiry).
		private const double LootBagExpirySeconds = 120.0;

		// How far a loot bag may be ground-snapped from the NPC's death Y. The snap fixes death-moment Y drift (tall /
		// flying creatures, MoveTask Z-drift #190) - a few metres. Beyond that the heightmap is describing the OUTDOOR
		// SURFACE above an interior/cave kill, not the floor the creature stood on, so the snap is refused and the
		// death Y wins. Outdoor kills sit ~1m off; the cave kill that exposed this was 28m.
		private const float MaxLootGroundSnap = 5f;
		private const double PatrolDwellSeconds = 2.0;

		// #266 patrol cycle: estimated client WALK speed (world units/sec) used to time how long a patrol route takes to
		// traverse, plus a dwell pause at the end, before the route is re-issued so the NPC keeps looping. These are the
		// re-issue cadence only (the client drives the actual walk); tune against in-client pacing if a patrol pauses or
		// restarts mid-walk. NpcMoveSpeed (12) is the RUN speed; a walk is roughly half.
		private const float PatrolWalkSpeed = 6f;

		// LootBagFullUpdateIIR_c - the world-object sub-opcode a loot bag spawns as.

		// The combat-mode stat value the captured beach melee mobs send at aggro (strickenbla capture, #218): the tick
		// before each mob's first swing it pushes `CombatMode = 4`, which is what draws its weapon. (The player uses 1
		// and one non-swinging entity used 1 - the field is a stance/mode code that isn't fully decoded, but 4 is the
		// value every actual melee swinger sends.)
		private const uint NpcCombatModeValue = 4;

		// The combat-mode stat value a PLAYER sends when it draws its weapon (#218/#259): 1, vs the mob's 4 (the field is
		// a stance/mode code; 1 is the value the captured player carries on combat entry). 0 sheathes.
		private const uint PlayerCombatModeValue = 1;

		// Dev visualization (#363): when true, an NPC that resolves to an RDB spawn AREA has that area's label pushed as
		// its overhead nameplate (via SetNameIIR after spawn) so a manual creature-labeling pass sees which area it's in.
		// Off by default; toggled live per-session by the `.togglenpcgroup` GM command (ToggleAreaLabels) - no config.
		public static bool DebugAreaLabels;

		// (mapId, instanceId) zones whose NPCs have been loaded into the world model (once per instance, on first entry
		// to that copy - each instance gets its own NPC population) (#268).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _npcLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// #363 ambient spawn-group pools per loaded zone (built in LoadAmbientSpawns on the entry thread, iterated by
		// MaintainAmbientPools on the tick thread - concurrent so the tick doesn't throw mid-load).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>> _ambientPools = new();

		// Cached loot-bag template (a captured "Bag of loot" world object) per map; null values cache loot-less maps.
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		private readonly WorldSimulation _sim;

		// Quest-gated NPC name -> the quest ids that reveal it (#261). Cached once; an NPC in this map is streamed only
		// to players holding one of its quests active (see WorldEntity.GateQuestIds / IsVisibleTo).
		private Dictionary<string, uint[]> _questGatedNpcs;

		// Quest-giver NPC name -> granted quest id, cached once (small set).
		private Dictionary<string, uint> _questGivers;

		// Monotonic allocator for ALL runtime-spawned entity handles - dropped-loot bags, pets, summoned NPCs, GM
		// `.spawnnpc`, and the #363 ambient pool members (not loot-specific despite the range's origin). CRITICAL: the
		// value must be in the SAME LOW range retail assigns mid-session entity handles - the client only tracks/polls
		// handles in that range, and the render of a mid-session spawn is completed by the client polling 0x2020 for the
		// entity's handle (-> server SpawnCheck 0x2008). Captured retail mid-session spawns (the mount + interest NPCs)
		// use 0x000286xx; the burst NPCs use 0x000818xx; the player/char-id is ~0x0802xxxx. A high value (the old
		// 0x40000000, or even 0x0F000000) is FAR above any real handle, so the client never polls it and the entity
		// silently never renders. 0x00090000 sits just above the burst NPCs (0x000818xx) and well below the char-id
		// range (0x02000000+) - small, valid, collision-free. (Matches the RetailLoginReplay built-NPC test handle.)
		private int _nextRuntimeHandle = 0x00090000;

		// _questHandler is constructed before the broadcaster (it needs only the sim + DB), so the interest stream can
		// call into it (Casilda's captive pose on stream-in) without a construction cycle.
		private readonly QuestHandlerService _questHandler;

		public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger, QuestHandlerService questHandler)
		{
			_sim = sim;
			_database = database;
			_logger = logger;
			_questHandler = questHandler;
		}

		// #267: re-send the NAMED NPC's quest marker in-place (handles are session-volatile, so resolve by name in the
		// player's zone). Unlike RefreshQuestGiverMarker this does NOT gate on the quest-GIVER map, so it also drives the
		// '?' talk marker for an NPC the player must talk to next (e.g. Casilda after Tuut's key, which activates the
		// talk stage while she's out of the conversation/stream-in path that would otherwise refresh her marker).
		public async Task RefreshQuestMarkerByName(INetworkClient client, string npcName)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || string.IsNullOrEmpty(npcName))
				return;

			var npc = _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && string.Equals(e.Name, npcName, StringComparison.OrdinalIgnoreCase));
			if (npc != null)
				await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npcName, npc.MapId));
		}

		// #261: re-evaluate quest-gated NPC visibility for one player after their quest state changed (accept / advance /
		// complete). Reloads their active-quest set, then runs an interest pass so newly-qualified gated NPCs spawn and
		// no-longer-qualified ones despawn in-place - no player/NPC movement required.
		public async Task RefreshQuestVisibility(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			await LoadActiveQuestIds(entity);
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null);
		}

		// Sends a QuestFullUpdate (one quest's full detail: description, summary, objective + map marker) built from
		// its seeded definition. acceptTime is the per-character "accepted at" stamp (display/sort field).
		public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			// The per-quest level is a constant harvested into the definition (#352); fall back to the captured default
			// only when this quest had no full-update in the captures (Level == 0).
			var levelValue = level ?? (def.Level != 0 ? def.Level : WorldQuest.DefaultQuestLevel);
			var refs = new[] { QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.AreaTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName }
				.Select(s => WorldBroadcastBodies.Record(def, s))
				.ToArray();
			var body = WorldQuest.BuildQuestFullUpdate(client.Session.Account.ClientInstance, def.QuestId, levelValue, def.AreaId, refs, def.GoalId, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), def.MapId, def.MarkerX,
				def.MarkerY, WorldBroadcastBodies.Record(def, QuestTextSlot.Objective), WorldBroadcastBodies.Record(def, QuestTextSlot.ObjectiveLocalized));

			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(client);
		}

		// Sends a QuestGoal (objective progress) to the player.
		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldQuest.BuildQuestGoal(client.Session.Account.ClientInstance, questId, goalId, count)))
				.SendAsync(client);
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldQuest.BuildQuestIir(client.Session.Account.ClientInstance, questId, state)))
				.SendAsync(client);
		}

		// Make the named NPC in `mapId`'s zone follow (or stop following) the given player handle - the quest-driven
		// entry point (e.g. Casilda after her rescue stage), keyed by NAME since handles are session-volatile. No-op
		// (returns false) if no such NPC is loaded in the zone.
		public bool SetNpcFollowByName(uint mapId, string npcName, uint playerHandle, bool follow)
		{
			// Resolve the PLAYER's instance, not the default one: the beach is Solo-instanced (#268), so the player and
			// their own Casilda live in a private playfield copy (e.g. 0x00A00001), not the shared instance 0. Find the
			// map's live zone that actually contains this player, then look up Casilda in THAT copy - a TryGetZone(mapId)
			// default-instance lookup misses her entirely and the follow silently no-ops.
			var zone = _sim.World.ZonesForMap(mapId).FirstOrDefault(z => z.TryGet(playerHandle, out _));
			if (zone == null)
				return false;

			var npc = zone.Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == npcName);
			if (npc == null)
				return false;

			npc.FollowTargetHandle = follow
				? playerHandle
				: null;
			_logger.LogInformation("Follow: {Name} {State} player 0x{Player:X8} (by name).", npcName, follow
				? "now follows"
				: "stopped following", playerHandle);
			return true;
		}

		// Called after items are taken from a bag (loot-all OR a single drag): confirm the take to the client with
		// LootRemoveItems, and tear the bag down ONLY once it is FULLY emptied (no items AND no coins). A partial loot
		// leaves the bag in the world with its remaining contents; it still fades on its own expiry (TickExpiry).
		// Retail loot-all finish decoded from the hoxxling gameplay capture (frame 4470): LootRemoveItems ->
		// DispatchCloseLootBag (0x2038) -> NewCurrentLooter/NewAllowedLooter reset -> n3ToClientFade -> UpdateSimpleDynel.
		public async Task AfterLootTake(INetworkClient client, uint bagHandle, uint removedPos = 0)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag))
				return;

			// Per-take ACK: tell the client which display slot to remove from the open window - the looted item's Pos
			// (the srcPos it dragged), echoed in the trailing u32 (decoded from loot-drag-1538). The client removes that
			// slot and compacts; a partial take leaves the bag open with its remaining items - NO re-render (re-sending
			// the full object stacked a second window). Loot-all passes removedPos 0 (matches the captured loot-all ACK).
			await SendWorldIir(client, WorldLoot.BuildLootRemoveItemsBody(bag.Handle, removedPos));

			if (bag.LootItems.Any(i => i != 0) || bag.CashTin > 0)
				return; // items/coins remain (0 = a drag-looted slot) - keep the bag open; client drives close (0x2044)

			// Fully emptied -> the captured completion sequence: DispatchCloseLootBag -> looter resets -> fade ->
			// consumed visual. (LootRemoveItems with remaining=0 was already sent above.)
			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchCloseLootBag)
				.WriteUInt32(WorldLoot.LootTypeTag)
				.WriteUInt32(bag.Handle)
				.SendAsync(client);
			await SendWorldIir(client, WorldLoot.BuildLooterResetBody((uint)GameServer0X2000Opcodes.NewCurrentLooterIIR, bag.Handle));
			await SendWorldIir(client, WorldLoot.BuildLooterResetBody((uint)GameServer0X2000Opcodes.NewAllowedLooterIIR, bag.Handle));
			await SendWorldIir(client, WorldLoot.BuildFadeBody(WorldLoot.LootTypeTag, bag.Handle, WorldLoot.DefaultFadeSeconds));
			await SendWorldIir(client, WorldLoot.BuildBagConsumedVisualBody(bag.Handle));

			_sim.World.ZoneForEntity(bag).Remove(bag.Handle);
			_logger.LogInformation("Loot: bag 0x{Handle:X8} fully looted; consumed + faded.", bag.Handle);
		}

		// The SOURCE NPC name of a loot bag (a corpse bag REUSES the dead NPC's WorldEntity + handle, so its Name is the
		// NPC's, e.g. "Scavenger Tuut") - the match key for a LootFromNpc quest goal (#261), since the quest item itself
		// is RDB content we don't materialise. Null for a non-bag handle or a bag with no source name (.spawnbag).
		public string BagSourceName(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) && bag.IsLootable)
				return bag.LootSourceName; // the dead NPC's name (e.g. "Scavenger Tuut"), NOT the bag's "Bag of loot"

			return null;
		}

		// Relays a player's BEHAVIOR (the BehaviorControlCenterIIR swing/cast animation) to every other player who can
		// already see them, so the attack/cast renders on the other clients - the combat analogue of BroadcastPlayerMove
		// (#48). `body` is the same BCC echo the actor's own client receives (it already carries the actor's handle as
		// instance); retail relays that exact body to nearby players, only the frame receiver differs (the
		// other-players-visible capture shows s2c BCC for other-player handles 0x1316xxx). Gated on the viewer's Known
		// set so a still-loading client isn't sent a behavior for a handle it hasn't built. No-op solo / when unseen.
		public async Task BroadcastBehaviorToViewers(INetworkClient actor, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the actor (their client has the handle)

				await SendWorldIir(viewer.Client, body);
			}
		}

		// Relays a player's ApplySpell (the MagicBuffIIR spell EFFECT - a buff applied, or an offensive cast) to every
		// other player who can already see the caster, so the effect renders on their clients too. This is the EFFECT
		// analogue of BroadcastBehaviorToViewers, which relays only the cast GESTURE (the BCC animation): retail streams
		// BOTH to nearby observers. `body` is the same ApplySpell carrier the caster's own client receives - it already
		// carries the acting player's handle as applyFrom (and, for a self-buff, applyTo); retail relays that exact body
		// to nearby players, only the frame receiver differs. Ground truth: the other-players-visible capture
		// (Hoxxling ...other_playersvisible) shows s2c ApplySpell (0x6B87E316) for other-player handles 0x1316xxx framed
		// to our endpoint - both self-buffs (e.g. player 0x01316e68 at frame 3809) and offensive casts (player
		// 0x01316cee on NPC targets). Gated on the viewer's Known set (#245: never relay for a handle the viewer's
		// client hasn't built). No-op solo / when the caster is unseen.
		public async Task BroadcastSpellToViewers(INetworkClient caster, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(caster);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the caster (their client has the handle)

				await SendWorldIir(viewer.Client, body);
			}
		}

		// Despawn a world-owned entity (a killed NPC, an expired loot bag) for every nearby player THAT ACTUALLY HAS IT
		// in their Known set. The Known guard is the #245 invariant: an n3ToClientQuit for a handle the recipient was
		// never sent crashes the client (`pcDynel == null`) - a still-loading player in range of an entity it never
		// received must not get a despawn for it. Interest is the sole NPC spawn path now, so Known is the authoritative
		// "was this entity sent to this client" record; the interest path (ResolveMove) already only despawns Known
		// handles, but this range-based world-tick path did not. Removes the handle as it despawns. Uses the same
		// n3ToClientQuit packet as player despawns.
		public async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Remove(entity.Handle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entity.Handle);
			}
		}

		// Fade an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot bags
		// (consumed or expired). Loot bags carry the 0xC87A typetag.
		public async Task BroadcastEntityFade(WorldEntity entity)
		{
			var body = WorldLoot.BuildFadeBody(WorldLoot.LootTypeTag, entity.Handle, WorldLoot.DefaultFadeSeconds);
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, viewer.Client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
					.WriteArrayUInt32Length(s => s.WriteArray(body))
					.SendAsync(viewer.Client);
			}
		}

		// An NPC engaged a target: push its aggro to every viewer in range that sees it - a HateListUpdate (the NPC now
		// hates the target) plus a ThreatLevel, so the threat/aggro is visible the tick combat starts (#216). Additive
		// to the combat-mode weapon-draw; gated with the rest of the combat sim by the caller.
		public async Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			if (target == null)
				return;

			var hate = HateListCodec.Build(npc.Handle, target.Handle);
			var threat = ThreatLevelCodec.Build(npc.Handle, 1, 1);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
				{
					await SendWorldRecord(viewer.Client, hate);
					await SendWorldRecord(viewer.Client, threat);
				}
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
			var body = StatUpdateCodec.BuildBody(npc.Handle, stats);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await SendWorldIir(viewer.Client, body);
			}
		}

		// An NPC moved a retail-sized segment (chasing a target, or returning home): send its MoveTask to every player in
		// range that already sees it. The body is the 61-byte transform form for the SEGMENT (MoveFrom -> Position, facing
		// the travel direction); the throttle in WorldState.Tick (#272) only queues this once the NPC has covered
		// MoveBroadcastSegment (~6u, retail's chase step) since the last one, so the client interpolates a real segment
		// over time instead of getting a 1.2u snap every tick (the jitter/teleport). Only viewers who know the handle get
		// it (else they'd get a move for an unknown entity).
		public async Task BroadcastNpcMove(WorldEntity npc)
		{
			// #272: send the mode-1 PathMoveTask WALK form (the client interpolates the segment over time) rather than the
			// mode-2 RotateMoveTask transform, which is a position-SET the client SNAPS to. The segment is MoveFrom ->
			// Position (the ~6u the throttle accumulated), so the client renders the NPC where the server model has it -
			// walking that segment - rather than lagging behind a far-goal path while the model jumps into melee (which
			// desyncs: the player takes hits from an NPC the client still shows mid-walk). The earlier path attempt reverted
			// because its trailer anchored at the START (walk then back = net-zero); BuildWalkBody anchors it at the segment
			// end (Position). Residual: consecutive segments still re-spline (a small snap) - the remaining #272 polish.
			var body = MoveTaskCodec.BuildWalkBody(npc.Handle, npc.MoveFrom, npc.Position);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await SendWorldIir(viewer.Client, body);
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
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				// Re-add to Known: the corpse despawn (ConvertCorpseToLoot, #246) removed the handle, so the player
				// no longer has it. Known.Add gates the spawn to viewers who actually need it + restores the #245
				// invariant (Known == "was sent"), so later move/combat broadcasts (which check Known) reach it.
				if (viewer.Client != null && viewer.Known.Add(npc.Handle))
					await SendWorldSpawn(viewer.Client, npc); // #266: no route replay - the tick drives a server-side wander
			}
		}

		// An NPC arrived home and stopped moving: send a StopIIR (final position + facing) to every viewer that sees it,
		// closing the MoveTask stream it was emitting while it chased/returned (#190). Faces the direction it travelled.
		public async Task BroadcastNpcHeartbeat(WorldEntity npc)
		{
			// The short-form BehaviorControlCenterIIR idle pulse (#225), carrying the NPC's monotonic per-entity tick. The
			// codec output is the bare IIR record (no length prefix), so it rides SendWorldIir - the same framing as the
			// MoveTask above - which adds the carrier's [u32 length]. Gated on CONFIRMED (not Known) so a still-loading
			// viewer - which has the handle in Known but not Confirmed - is never sent a pulse for an unbuilt dynel.
			var record = BehaviorHeartbeatCodec.Build(npc.Handle, ++npc.HeartbeatTick);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Confirmed.Contains(npc.Handle))
					await SendWorldIir(viewer.Client, record);
			}
		}

		public async Task BroadcastNpcStop(WorldEntity npc)
		{
			var facing = npc.MoveFrom.FaceTowards(npc.Position);
			var record = StopCodec.Build(npc.Handle, npc.Position, facing.X, facing.Y, facing.Z, facing.W);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await SendWorldRecord(viewer.Client, record);
			}
		}

		// #259 toggle-combat: push the PLAYER's combat-mode stat (draw / sheathe their weapon) to themselves AND nearby
		// viewers who see them, mirroring BroadcastNpcCombatMode. The local client needs the stat to draw its own avatar's
		// weapon, so unlike the NPC path this also addresses the player's own connection. The captured player value is 1
		// (vs the mob's 4); 0 sheathes.
		public async Task BroadcastPlayerCombatMode(INetworkClient client, bool inCombat)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, inCombat
				? PlayerCombatModeValue
				: 0u);
			var body = StatUpdateCodec.BuildBody(player.Handle, stats);

			await SendWorldIir(client, body); // the player's own view draws/sheathes its weapon
			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Client != client && viewer.Known.Contains(player.Handle))
					await SendWorldIir(viewer.Client, body);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		public Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_sim.World.ZoneForEntity(leaver), leaver);
			return DispatchInterest(events, null);
		}

		// After world entry (spawn body now captured): stream the in-range NPCs to the player via interest (the burst
		// no longer carries NPCs) and spawn the player to nearby players. Interest is the sole NPC spawn path now that
		// #23 made mid-session/interest spawns render in-client.
		public async Task BroadcastPlayerEntry(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity?.SpawnBody == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);

			// #261: seed the player's active-quest set before the first interest pass so quest-gated NPCs (e.g. Scavenger
			// Tuut) are correctly included/excluded from the very start, not only after a quest change.
			await LoadActiveQuestIds(entity);

			// Interest is the SOLE NPC spawn path (since #23 made mid-session/interest spawns render; in-client verified
			// 2026-06-16): NPCs are dropped from the world-entry burst (ReplayMapWorldBurst), so stream every in-range
			// NPC here at entry (includeNonPlayers), each as its own FA015807 + name registration via DispatchInterest.
			// No burst NPCs means no Known-seeding and no double-spawn. KNOWN: streams the cross-session model union
			// (~4x retail density) - cross-session mob dedup / interest tuning is a follow-up.
			var npcEvents = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, true, isEntry: true);
			await DispatchInterest(npcEvents, null);
		}

		// After a player's Move: update interest and broadcast the resulting spawns/moves to nearby players.
		public async Task BroadcastPlayerMove(INetworkClient client, uint moveSubop, uint dataObject, uint dataClientInstance, ushort movingType, Vector3 position, Vector3 rotation, byte[] tail)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			// Advance the player's model position to where they actually are BEFORE computing interest - otherwise it
			// stays frozen at the entry point and only entity ever within range of the wash-ashore spot streams (the
			// "most NPCs missing" bug). This is also what makes other players see this one at its live position.
			entity.Position = position;

			// includeNonPlayers: stream the zone's NPCs to this player as they move (the burst only rendered the NPCs
			// near the entry point; this fills in the rest of the map - e.g. Casilda, who is outside the captured
			// burst). Burst NPCs are already in the player's Known set (seeded at entry) so they are not re-spawned.
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			var spawns = 0;
			var despawns = 0;
			foreach (var ev in events)
			{
				if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Spawn)
					spawns++;
				else if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Despawn)
					despawns++;
			}

			if (spawns > 0 || despawns > 0)
			{
				_logger.LogInformation("Interest: {Player} at {Pos} -> streamed +{Spawns} / -{Despawns} entities (now knows {Known}).", entity.Name, entity.Position, spawns, despawns,
					entity.Known.Count);
			}

			await DispatchInterest(events, new MoveForward(moveSubop, dataObject, dataClientInstance, movingType, position, rotation, tail));
		}

		// Stashes the player's own FA015807 body (the bytes after the RPC header) on their world entity, so the
		// broadcaster can re-frame it to spawn this player on other clients.
		public void CaptureOwnSpawnBody(INetworkClient client, byte[] spawnPacket)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null || spawnPacket.Length < 12)
				return;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(spawnPacket.AsSpan(8, 4));
			if (bodyStart <= spawnPacket.Length)
				entity.SpawnBody = spawnPacket[bodyStart..];
		}

		// A player closed the loot window (top-level 0x2044): consume the bag - fade it out for nearby players and drop
		// it from the model.
		public async Task CloseLootBag(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return;

			await BroadcastEntityFade(bag);
			_sim.World.Remove(bag);
			_logger.LogInformation("Loot: {Name} closed loot bag 0x{Handle:X8}; faded + removed.", player.Name, bag.Handle);
		}

		// A dead NPC's death-animation linger elapsed (driven by the world tick): turn its corpse into a lootable bag
		// IN PLACE. Every kill leaves a lootable corpse - the drop ROLL decides only whether it carries items/cash, not
		// whether it's lootable (retail leaves a lootable corpse on every kill; empty corpses are fine). Falls back to a
		// plain despawn only when no loot-bag world object can be built for the map.
		public async Task ConvertCorpseToLoot(WorldEntity deadNpc)
		{
			if (!deadNpc.IsDead)
				return; // revived (or never died) before the linger fired - nothing to convert

			// A quest mob the killer needs to LOOT (an active LootFromNpc goal targeting this NPC, #261) ALWAYS leaves a
			// bag - the "key" must be lootable, not a 50/50 roll. The goal may also name a specific key ITEM (QuestItemId).
			var lootGoalItem = deadNpc.LastAttackerHandle is { } looterHandle
				? await ActiveLootGoalItemFor(looterHandle, deadNpc.Name)
				: null;
			var questDrop = lootGoalItem.HasValue;

			List<uint> items;
			uint cash;
			if (questDrop)
			{
				// Quest loot mob: the bag holds ONLY the quest key (no random gear/cash), so the objective is unambiguous
				// - e.g. Scavenger Tuut drops just the Scavenger's Key. An empty key id still drops an (empty) bag whose
				// SOURCE NPC completes the goal on loot-all.
				items = lootGoalItem is > 0
					? [lootGoalItem.Value]
					: [];
				cash = 0;
			}
			else
			{
				var dropsContents = LootResolver.RollsDrop(Random.Shared.NextDouble());
				items = await RollLootItems(dropsContents
					? Random.Shared.Next(0, 3)
					: 0, deadNpc.MapId); // 0-2 items, or none
				cash = dropsContents
					? LootResolver.RollCash(Random.Shared.NextDouble(), deadNpc.Level)
					: 0u;
			}

			// DECOUPLED loot (#246): the bag is a SEPARATE IsLoot entity (its OWN handle) at the death spot, NOT the
			// NPC's handle reused. Reusing it pinned the NPC's respawn to where it died (the client can't move a bag
			// handle via the respawn's CharFullUpdate) and made the loot vanish when the NPC respawned. Spawn the bag,
			// then despawn the corpse so the NPC handle respawns clean at its spawn point (BroadcastNpcRespawn re-adds
			// it to Known); the bag lives/loots/expires on its own (tick-managed via TickExpiry). Empty corpses just
			// despawn (matching retail - not every kill leaves a bag).
			// Spawn a bag when there's loot, OR unconditionally for a quest mob (the bag may be empty - looting it still
			// completes the LootFromNpc goal, which matches on the bag's source NPC, not its contents).
			if (questDrop || items.Count > 0 || cash > 0)
			{
				// Bag at the NPC's death spot HORIZONTALLY (X/Z - where the player expects it, not at the killer's feet
				// on a ranged/AoE kill), snapped to the terrain GROUND Y so a static bag mesh sits on the floor instead
				// of at the creature's death-moment Y (which varies by creature: tall / flying / MoveTask Z-drift #190 ->
				// bags float or sink). #284: the ground height is the RDB heightmap sample at the death X/Z.
				//
				// The snap only applies when the heightmap AGREES the NPC was standing on the outdoor surface. The
				// heightmap is that surface and nothing else: an NPC killed in a cave, indoors, or on an upper floor
				// sits far below (or above) it, and snapping there flings the bag to the open air - Kier-Dheed died at
				// Y=153 under a surface of Y=181 and his bag spawned 28m overhead, unreachable. Outdoors the delta is
				// ~1m, which is why this only ever showed up on interior kills. Past the tolerance the death Y is the
				// better answer: the creature was demonstrably standing there.
				var dropPos = deadNpc.Position;
				if (_sim.Terrain?.GroundHeight(deadNpc.MapId, deadNpc.Position.X, deadNpc.Position.Z) is { } groundY
					&& MathF.Abs(groundY - deadNpc.Position.Y) <= MaxLootGroundSnap)
					dropPos = new Vector3(deadNpc.Position.X, groundY, deadNpc.Position.Z);

				var bag = await SpawnLootBagAt(deadNpc.MapId, deadNpc.InstanceId, dropPos, deadNpc.Rotation, items, cash, deadNpc.LastAttackerHandle ?? 0, deadNpc.Name, deadNpc.LootContainer);
				_logger.LogInformation(
					"Loot: {Name} 0x{Handle:X8} dropped bag 0x{Bag:X8} ({Items} items, {Cash} tin, looter 0x{Looter:X8}, quest={Quest}) on map {Map} at {Drop} (death spot {Death}).", deadNpc.Name,
					deadNpc.Handle, bag?.Handle ?? 0, items.Count, cash, deadNpc.LastAttackerHandle, questDrop, deadNpc.MapId, dropPos, deadNpc.Position);
			}

			await BroadcastEntityDespawn(deadNpc); // remove the corpse render; the NPC revives via TickRespawn (or is removed if NoRespawn)
		}

		// Despawns a non-player entity by handle in the calling player's zone: the decoded n3ToClientQuit removal
		// to every player in range + drop from the model (the cleanup complement to SpawnNpcForPlayer; also works
		// on burst NPCs and loot bags). Returns the removed entity, or null when the handle is unknown or a player.
		public async Task<WorldEntity> DespawnEntityForPlayer(INetworkClient client, uint entityHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(entityHandle, out var entity) || entity.IsPlayer)
				return null;

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				// #245 invariant: only despawn for a viewer that actually has this handle (a despawn for a handle the
				// recipient was never sent crashes it). Removes it from Known as it goes.
				if (viewer.Client != null && viewer.Known.Remove(entityHandle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entityHandle);
			}

			_sim.World.Remove(entity);
			_logger.LogInformation("Despawn: removed {Name} 0x{Handle:X8} (by {Player}).", entity.Name, entityHandle, player.Name);
			return entity;
		}

		// GM .delete of a static WORLD OBJECT (chest/door/dynel): these aren't in the live entity model (they stream
		// once at world entry), so a handle that isn't a live entity may still be one. Despawns it live for nearby
		// players AND deletes its DB row so it's gone on relog too. Returns the deleted row, or null if the handle isn't
		// a known world object on the player's map.
		public async Task<WorldObject> DeleteWorldObjectForPlayer(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var obj = await _database.GetWorldObject(player.MapId, handle);
			if (obj == null)
				return null;

			// World objects aren't tracked in Known, so we can't use that #245 gate; instead only despawn for viewers
			// that have ENTERED the world (they received the world-object burst, so the handle is safe to quit for them).
			// A world object is removed by its OWN class (SimpleDynel / LootBag / ZoneLabel / ...), not Character - a
			// Character-classed despawn silently misses it (#366). Derive the class from its spawn IIR.
			var classWord = WorldObjectBuilder.TypeTagForClass(obj.SubOpcode);
			var zone = _sim.World.ZoneForEntity(player);
			foreach (var viewer in zone.PlayersInRange(obj.Position, WorldSimulation.PlayerInterestRange))
				if (viewer.Client?.Session?.WorldEntered == true)
					await SendWorldDespawn(viewer.Client, zone.PlayfieldToken, handle, classWord);

			var deleted = await _database.DeleteWorldObject(player.MapId, handle);
			_logger.LogInformation("Delete: removed world object {Name} 0x{Handle:X8} ({Rows} row(s), by {Player}).", obj.DisplayName ?? obj.Name, handle, deleted, player.Name);
			return obj;
		}

		// Loads a map's NPCs from NpcSpawns into the world model ONCE (on first entry), so the server has an
		// authoritative shared view of the map's NPCs - the model fix at the heart of the roadmap (NPCs are otherwise
		// only per-player burst replays the server doesn't track). The tick/AI/combat will drive these; clients still
		// render them from the burst for now, so this is model bookkeeping (no wire change).
		public async Task EnsureZoneNpcs(uint mapId, uint instanceId)
		{
			if (!_npcLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var npcs = await _database.GetNpcSpawns(mapId);
			var zone = _sim.World.ZoneFor(mapId, instanceId);

			// #363 dev labels: the map's RDB spawn areas, so each NPC carries its area label ready for the live
			// `.togglenpcgroup` toggle. Computed at load (a cheap query, stored as a string) so the toggle is instant and
			// also catches NPCs that stream in while it's on - the toggle only gates whether the label is SENT.
			var spawnAreas = await _database.GetNpcSpawnAreas(mapId);

			// #261 quest-gated spawns: NPC name -> the quest ids that reveal it (kill-target NPCs that aren't givers).
			_questGatedNpcs ??= await _database.GetQuestGatedNpcMap();

			// NpcSpawns is the SOURCE OF TRUTH: the importer's burst-truth extraction (#266, BuildBurstTruthSpawns)
			// already produced one clean row per intended placement (keyed by MapId + exact XYZ), so this load path
			// renders EVERY row verbatim - NO runtime dedup. The old name/cell collapse silently dropped legitimately
			// distinct NPCs: neutral animals of the same name (5 Hens, 4 Scorpions), and faction guards standing 1-4
			// units apart (the Tarantia Noble District line and the Castle of King Conan door pairs). Any genuine
			// over-density is a data bug fixed in the importer, never hidden here.
			//
			// CapturedHandle is session-volatile and NOT guaranteed unique across the merged DB (a handful collide from
			// different captures); since the zone is handle-keyed, a colliding row would silently overwrite another, so
			// a collision gets a fresh handle below - the last real dedup this loop still needs, for wire correctness.
			var usedHandles = new HashSet<uint>();
			var nextFreeHandle = npcs.Length == 0 ? 1u : npcs.Max(n => n.CapturedHandle) + 1u;
			var labelledCount = 0; // #363 diagnostic: how many captured spawns got a non-null area label this load
			string labelSample = null;
			foreach (var npc in npcs)
			{
				// The type identity (name/community) lives on the NpcTemplate type now (#244); a spawn with no type is a phantom
				// capture artifact (no real creature) - skip it.
				if (npc.NpcTemplate == null)
					continue;

				var npcName = npc.NpcTemplate.Name;

				// Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
				// importer), falling back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
				// World entry is burst-less: every NPC streams in on the player's first move (interest) under its own
				// captured handle. Reassign only on a cross-capture handle collision so both rows still render.
				var handle = npc.CapturedHandle;
				if (!usedHandles.Add(handle))
				{
					while (!usedHandles.Add(nextFreeHandle))
						nextFreeHandle++;
					handle = nextFreeHandle;
				}
				var entity = WorldEntity.ForNpc(handle, mapId, npc.Position, npc.Rotation, npcName ?? "NPC", vitals, npc.NpcTemplateId);
				entity.InstanceId = instanceId; // this NPC belongs to THIS copy of the map (#268)
				entity.Level = npc.Level;
				// #261: if this NPC's spawn is quest-gated, stamp the revealing quest ids so interest only streams it to
				// players holding one of them (e.g. Scavenger Tuut while you carry Casilda's kill quest).
				if (npcName != null && _questGatedNpcs.TryGetValue(npcName, out var gateQuests))
				{
					entity.GateQuestIds = gateQuests;
					// Quest-gated NPCs are one-time KILL TARGETS (the gate map is exactly the kill-goal targets that
					// aren't quest givers). A story kill target must NOT respawn ~15s later - killing Scavenger Tuut
					// for Casilda's quest completes the goal and he should stay dead (retail solo-instances him). So a
					// gated NPC despawns after death instead of reviving; its loot bag (the quest key) is a separate
					// entity and still drops.
					entity.NoRespawn = true;
				}

				// #266 patrol: attach this NPC's captured route (linked by exact spawn position). REPLAY the captured
				// MoveTask re-addressed to this entity (handle + from = its spawn) - the original form carries the real
				// speed/flags, so the client walks the route at the right pace (a rebuilt mode-1 path lost the speed and
				// run-crawled). The cycle timer (path time + dwell) re-issues it so it keeps looping.
				if (npc.PatrolPath is { Nodes.Count: > 0 } patrol)
				{
					var waypoints = MoveTaskCodec.PatrolWaypoints(patrol);
					if (waypoints.Count > 0)
					{
						entity.PatrolWaypoints = waypoints;
						entity.PatrolBody = MoveTaskCodec.BuildPatrol(handle, entity.Position, patrol);
						entity.PatrolCycleSeconds = MoveTaskCodec.TotalPathLength(entity.Position, entity.PatrolWaypoints) / PatrolWalkSpeed + PatrolDwellSeconds;
						entity.ArmPatrolCycle();
					}
				}

				// Disposition drives BOTH the overhead name colour and whether this NPC aggros. It is the captured
				// retail cCategory (NpcStatCore.Disposition, #256/aggro) - the SOLE correct hostility source. Community
				// is faction MEMBERSHIP, NOT hostility: community 1 is the civilian/default faction shared by Citizens,
				// vendors and guards, so the old `community > 0` test wrongly aggroed every townsperson. NULL disposition
				// -> friendly (the client's green default for an un-told-about NPC). Only cCategory 1 (hostile) aggros.
				entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
				entity.Hostile = entity.Disposition == WorldEntity.DispositionHostile;

				// The creature's death animation (#362): use the type's harvested death behaviorId, else the humanoid
				// default. Sent in the death BccIIR by CombatService.OnNpcKilled so demons/beasts play their own death.
				if (npc.NpcTemplate.DeathBehaviorId is { } deathBehavior)
					entity.DeathBehaviorId = deathBehavior;

				// The loot container this type drops on death (Bag by default, a bigger chest for bosses) - picked by
				// the kill-drop path (SpawnLootBagAt) via its LootContainerTemplate variant.
				entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId;

				// Keep the NPC's FA015807 spawn body (addressed to the chosen handle) so interest/respawn can re-render
				// it for nearby players (#244): built WHOLE from typed columns (the captured Data blob was retired) with
				// this zone's load-time playfield token baked in. SendWorldSpawn re-stamps the entering player's LIVE
				// token per-send (SpawnPlayfieldRewriter), since that token isn't known until the player enters.
				// #363 dev label: the client ignores the spawn-body free `Name` for an NPC's floating nameplate (it uses
				// the template_id LDB name), so we DON'T bake the label into the spawn body - we push the AREA LABEL via a
				// SetNameIIR after the spawn (SendWorldSpawn), gated on the .togglenpcgroup toggle. The client hard-caps a
				// SetNameIIR name at 31 bytes, so we show the label alone (group | tier xN, ~24 chars) - the group name is
				// what a labeling pass needs. Stashed here unconditionally so the toggle is instant.
				if (AreaLabelFor(spawnAreas, npc) is { } areaLabel)
				{
					entity.OverheadNameOverride = areaLabel;
					labelledCount++;
					labelSample ??= areaLabel;
				}
				if (NpcSpawnBuilder.TryBuildLive(npc, handle, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken, out var spawnBody))
					entity.SpawnBody = spawnBody;

				// Caster NPCs (#232): the spells this NPC type casts, read from the #244 type FK (npc.NpcTemplate.Spells, loaded
				// with the spawn) - this replaces the fragile NpcName string join. Empty for non-casters.
				if (npc.NpcTemplate.Spells is { Count: > 0 } typeSpells)
				{
					foreach (var s in typeSpells)
						entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToArray()));
				}

				// Ambient chatter lines for this NPC type (per-NPC; empty for NPCs without a captured group -> silent).
				if (npc.NpcTemplate.Chatter is { Count: > 0 } chatter)
					entity.ChatterLines = chatter.Select(c => c.RemoteFormat).Where(x => !string.IsNullOrEmpty(x)).ToArray();

				// #363: a captured spawn LINKED to an ambient area is a member of that area's POOL (the softer alternative
				// to graduating/deleting it). It renders with its captured appearance but the POOL owns its respawn (fades
				// on death, replaced by the pool), and it counts toward MaxCount so the pool only tops up the DEFICIT -
				// no captured/ambient overlap.
				if (npc.NpcSpawnAreaId is { } linkedAreaId)
				{
					entity.SpawnAreaId = linkedAreaId;
					entity.NoRespawn = true;
				}

				zone.Add(entity);
			}

			await LoadAmbientSpawns(mapId, instanceId, zone);

			_logger.LogInformation("World: loaded {Count} NPC(s) into map {Map} instance 0x{Instance:X8} ({Raw} DB rows, no runtime dedup).", zone.Count, mapId, instanceId, npcs.Length);
			_logger.LogDebug("World: #363 {Labelled}/{Total} captured spawns have a spawn-area label on map {Map} (.togglenpcgroup to show; sample: {Sample}).",
				labelledCount, npcs.Length, mapId, labelSample ?? "<none>");
		}

		// #363 live ambient spawner: the capture-INDEPENDENT open-world
		// population, managed as spawn-group POOLS. Each RESOLVED NpcSpawnArea becomes an AmbientPool that keeps ~MaxCount
		// of its creature alive within the group's area: the initial fill spawns one per RDB point, each member roams the
		// group's points (its PatrolWaypoints -> the #266 wander) and fades on death (NoRespawn); MaintainAmbientPools
		// then respawns the deficit at a free point. Built WHOLE from the creature TYPE (TryBuildFromType, the pet path).
		// Worked example - Tortage beach (4010): the four T_Crocodile-* areas become four Crocodile pools (8/7/8/11)
		// that each hold that many crocs roaming their stretch of shore, refilled as the player kills them.
		private async Task LoadAmbientSpawns(uint mapId, uint instanceId, Zone zone)
		{
			var areas = await _database.GetResolvedSpawnAreas(mapId);
			var pools = new List<AmbientPool>();
			var spawned = 0;
			foreach (var area in areas)
			{
				var type = area.NpcTemplate;
				if (type?.StatCore?.IirHeader == null)
					continue; // the resolved creature has no buildable stat core - skip (can't construct its body)

				var points = area.Points.Select(p => p.Position).ToArray();
				if (points.Length == 0)
					continue;

				var level = type.StatCore.Level ?? 0;
				var pool = new AmbientPool(area.Id, area.MaxCount, points, type, level,
					level > 0 ? CombatResolver.NpcMaxHealthForLevel(level) : 1u,
					type.StatCore.Disposition ?? WorldEntity.DispositionFriendly, FormatAreaLabel(area));
				pools.Add(pool);

				// The captured spawns LINKED to this area (tagged in the load loop above) are already in the zone as pool
				// members - the "obsolete npcspawns" that fill the pool first; only spawn the DEFICIT up to MaxCount, at
				// points not already occupied. So a resolved area with captured members doesn't double-populate.
				var members = zone.Entities.Where(e => e.SpawnAreaId == area.Id).ToList();
				for (var i = members.Count; i < pool.MaxCount; i++)
				{
					var member = SpawnAmbientMember(pool, FreeAmbientPoint(pool, members), zone);
					if (member == null)
						break;

					members.Add(member);
					spawned++;
				}
			}

			_ambientPools[(mapId, instanceId)] = pools;
			if (spawned > 0)
				_logger.LogInformation("World: +{Count} ambient NPC(s) across {Pools} spawn-area pool(s) on map {Map}.", spawned, pools.Count, mapId);
		}

		// Builds one member of an ambient pool at `pos` and adds it to the zone (no wire send - the interest stream, or
		// the maintenance caller, does that). The member roams the pool's points and fades-not-revives on death so the
		// POOL owns its respawn. Returns the entity, or null if the type won't build.
		private WorldEntity SpawnAmbientMember(AmbientPool pool, Vector3 pos, Zone zone)
		{
			var handle = NextRuntimeHandle();
			if (!NpcSpawnBuilder.TryBuildFromType(pool.Type, handle, pos, Quaternion.Identity, out var body))
				return null;

			var entity = WorldEntity.ForNpc(handle, zone.MapId, pos, Quaternion.Identity, pool.Type.Name, EntityVitals.FromMax(pool.MaxHealth, 0, pool.MaxHealth), pool.Type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = pool.Level;
			entity.Disposition = pool.Disposition;
			entity.Hostile = pool.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = pool.Type.LootContainerTemplateId; // drop this type's container on death
			entity.SpawnBody = body;
			entity.SpawnAreaId = pool.AreaId;
			entity.OverheadNameOverride = pool.Label; // #363 dev nameplate label, sent via SetNameIIR when .togglenpcgroup is on
			entity.NoRespawn = true;              // corpse fades on death; the POOL respawns a replacement (below)
			entity.PatrolWaypoints = pool.Points; // roam the group's area via the #266 wander (no new movement code)
			entity.ArmPatrolCycle();              // stagger the first wander so they don't all step at once
			foreach (var s in pool.Type.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToArray()));

			zone.Add(entity);
			return entity;
		}

		// #363 pool maintenance (called on a slow cadence from the sim tick): keep each loaded zone's ambient pools at
		// MaxCount. A killed member fades + is removed (LootExpired), dropping the pool's live count; here we respawn ONE
		// replacement per under-strength pool per pass (a gentle trickle - the cadence IS the respawn delay) at a free
		// point, streamed to nearby players.
		public async Task MaintainAmbientPools()
		{
			foreach (var ((mapId, instanceId), pools) in _ambientPools)
			{
				var zone = _sim.World.ZoneFor(mapId, instanceId);
				foreach (var pool in pools)
				{
					var live = zone.Entities.Where(e => e.SpawnAreaId == pool.AreaId && !e.IsDead).ToList();
					if (live.Count >= pool.MaxCount)
						continue;

					var pos = FreeAmbientPoint(pool, live);
					var entity = SpawnAmbientMember(pool, pos, zone);
					if (entity == null)
						continue;

					foreach (var viewer in zone.PlayersInRange(pos, WorldSimulation.PlayerInterestRange))
						if (viewer.Client != null && viewer.Known.Add(entity.Handle))
							await SendWorldSpawn(viewer.Client, entity);
				}
			}
		}

		// A pool point with no live member sitting on it (so a refill lands on an empty spot, not on top of a survivor);
		// falls back to a random point when they're all occupied.
		private static Vector3 FreeAmbientPoint(AmbientPool pool, IReadOnlyList<WorldEntity> live)
		{
			foreach (var pt in pool.Points)
			{
				var occupied = false;
				foreach (var e in live)
				{
					var dx = e.Position.X - pt.X;
					var dz = e.Position.Z - pt.Z;
					if (dx * dx + dz * dz < 4f)
					{
						occupied = true;
						break;
					}
				}

				if (!occupied)
					return pt;
			}

			return pool.Points[Random.Shared.Next(pool.Points.Length)];
		}

		// The runtime state of one resolved NpcSpawnArea: the creature TYPE + spawn points + the population it maintains.
		// Label is the dev nameplate tag (FormatAreaLabel), applied to members when DebugAreaLabels is on.
		private sealed record AmbientPool(uint AreaId, int MaxCount, Vector3[] Points, NpcTemplate Type, uint Level, uint MaxHealth, uint Disposition, string Label);

		// #363 dev label for a captured spawn's nameplate: LINK-first - the area it's a confirmed MEMBER of
		// (NpcSpawnAreaId), even if it sits outside the bounds (a hand-linked stray); else the area its POSITION lands in
		// (so an unlinked NPC still previews which area it's in). Null when neither. See FormatAreaLabel.
		private static string AreaLabelFor(IReadOnlyList<NpcSpawnArea> areas, NpcSpawn npc)
		{
			NpcSpawnArea area = null;
			if (npc.NpcSpawnAreaId is { } id)
				area = areas.FirstOrDefault(a => a.Id == id);

			area ??= AreaContaining(areas, npc.Position);
			return area == null
				? null
				: FormatAreaLabel(area);
		}

		// The nameplate label for an area: "<group> | <tier> x<count>" (e.g. "Ape2 | id-prop x8"). ASCII-only on
		// purpose (#363): the client's nameplate font doesn't render the "·"/"×" glyphs, and our own PlayerSpawnReader
		// rejects a non-ASCII overhead name - both broke the dev label silently, so keep it 7-bit.
		private static string FormatAreaLabel(NpcSpawnArea a)
		{
			return $"{a.GroupName} | {ResolvedTag(a.ResolvedBy)} x{a.MaxCount}";
		}

		// The spawn area a position lands in (nearest by centroid when several overlap; bounds padded), or null.
		private static NpcSpawnArea AreaContaining(IReadOnlyList<NpcSpawnArea> areas, Vector3 pos)
		{
			const float pad = 5f;
			NpcSpawnArea best = null;
			var bestDistSq = float.MaxValue;
			foreach (var a in areas)
			{
				if (pos.X < a.BoundsMinX - pad || pos.X > a.BoundsMaxX + pad || pos.Z < a.BoundsMinZ - pad || pos.Z > a.BoundsMaxZ + pad)
					continue;

				var cx = (a.BoundsMinX + a.BoundsMaxX) * 0.5f;
				var cz = (a.BoundsMinZ + a.BoundsMaxZ) * 0.5f;
				var distSq = (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz);
				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					best = a;
				}
			}

			return best;
		}

		// Short nameplate-friendly tag for a spawn area's creature-resolution provenance (#363).
		private static string ResolvedTag(SpawnResolutionSource resolvedBy) => resolvedBy switch
		{
			SpawnResolutionSource.Name => "name",
			SpawnResolutionSource.Containment => "contain",
			SpawnResolutionSource.IdPropagation => "id-prop",
			SpawnResolutionSource.Manual => "manual",
			_ => "unresolved"
		};

		// A player opened a world object (Interact UseObject). If it's a loot bag in their zone, OPEN ITS LOOT WINDOW
		// per the decoded flow (protocol_loot_interaction): assign the player as the current looter and send the
		// SimpleDynelVisualChange "Open" so the client shows the bag's contents. The bag STAYS in the world - it is
		// consumed on close (0x2044), not on open. Taking an item (0x2043 -> grant + LootRemoveItems) is the remaining,
		// in-client-gated piece. Returns false for non-loot / unknown objects so the caller can surface the gap.
		public async Task<bool> OpenLootBag(INetworkClient client, uint objectHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(objectHandle, out var bag) || !bag.IsLootable)
				return false;

			// NewCurrentLooter rides the WORLD conversation (GameAgent -> GameInterface), as in retail (mount/gameplay
			// capture frame 1477).
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldLoot.BuildNewCurrentLooterBody(bag.Handle, player.Handle)))
				.SendAsync(client);

			// The actual window-open is DispatchOpenLootBag (0x2037) on the GameCharAgent -> GameCharInterface
			// conversation (the same class-pair as the 0x201C spawn handshake / SpawnCheck), body [loot typetag][bag
			// handle] - NOT a SimpleDynelVisualChange on the world conversation (that only played the open animation, no
			// window). Decoded from the hoxxling gameplay capture (f1477): after this the client itself sends
			// TryLootCash (0x2045) then TryLootAll (0x2043), which we already handle.
			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchOpenLootBag)
				.WriteUInt32(WorldLoot.LootTypeTag)
				.WriteUInt32(bag.Handle)
				.SendAsync(client);

			_logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
			return true;
		}

		// A player used a STATIC world object (Interact UseObject) that isn't a loot bag - a DOOR/GATE (e.g. the
		// "King Conan's Throne Room" gate). Reply with the SimpleDynelVisualChange so the client plays the open
		// animation + drops the collision: numeric state 5 then the "object_open" state string, matching the
		// conans-castle capture (rec 1697 -> 1698). The object is looked up by its CapturedHandle - the handle it was
		// streamed with (WorldObjectBuilder.TryBuild uses row.CapturedHandle) - on the player's map. Returns false if
		// no such object exists, so the caller can fall through to the loot-bag path.
		public async Task<bool> OpenWorldObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return false;

			var obj = (await _database.GetWorldObjects(player.MapId)).FirstOrDefault(o => o.CapturedHandle == handle);
			if (obj == null)
				return false;

			await SendWorldIir(client, SimpleDynelMessageCodec.BuildVisualChange(handle, 0x05));
			await SendWorldIir(client, WorldLoot.BuildOpenWindowBody(SimpleDynelMessageCodec.VisualChangeTypeTag, handle, "object_open"));
			_logger.LogInformation("Object: {Player} opened '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name ?? obj.DisplayName ?? "object", handle);
			return true;
		}

		// The object class the sit remoteformat names (capture: startobjectaction(33)|turnon_execute(4)|TavernBenchSimpleDynel).
		private const string BenchObjectClass = "TavernBenchSimpleDynel";

		// Seat the player on a BENCH (the sit interaction - tools/capture/decode_sit.py). Interacting with a bench
		// plays the actor's "chair_sit_calm" animation (a server-originated BehaviorControlCenterIIR) and marks the
		// session seated; standing back up is triggered by the next move (StandUpIfSeated). Recognized by the object
		// NAME ("Bench"), the same stable key as shop vendors / the auctioneer. Returns false when the used object is
		// NOT a bench, so the caller falls through to the normal open-object / loot path. Renders for the actor and
		// every nearby player who already sees them.
		public async Task<bool> TrySitOnObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return false;

			var obj = await _database.GetWorldObject(player.MapId, handle);
			if (obj == null || !string.Equals(obj.Name, "Bench", StringComparison.OrdinalIgnoreCase))
				return false;

			// The sit is a TWO-phase handshake (#355, capture sit-bench frames 6805-6813). The client sends the bench
			// UseObject TWICE: request #1 BEFORE it has the pose, request #2 AFTER it receives the animation. Retail
			// answers EACH - request #1 with the hold-echo (0x12 seq 0x0004) + the sit animation (0x2A), request #2 with
			// the hold-assert (0x12 seq 0x000F) that COMMITS the pose. Answering only request #1 (our old behavior: send
			// all three up-front, then silently absorb the duplicate) leaves the client's second confirm unanswered, so
			// the sit-down plays and then reverts after the ~2.6s chair_sit_calm blend. Split it: seat + echo + animate
			// on the first, assert once on the second, absorb any further duplicates.
			if (client.Session.SeatedOnHandle == handle)
			{
				// Phase 2 - the client's confirming re-send. Commit the pose with the hold-assert, exactly once.
				if (!client.Session.SeatedHoldConfirmed)
				{
					var assert = BehaviorAnimationCodec.BenchSitState(player.Handle, BehaviorAnimationCodec.SeqObjectHold, handle);
					await SendWorldIir(client, assert);
					await BroadcastBehaviorToViewers(client, assert);
					client.Session.SeatedHoldConfirmed = true;
					_logger.LogInformation("Sit: {Player} hold-confirmed on 0x{Handle:X8}.", player.Name, handle);
				}

				return true;
			}

			// Phase 1 - the initial UseObject. Retail brackets the sit ANIMATION with the object-action hold STATE
			// (behaviorId 0x12): the hold-echo BEFORE the animation, then the animation itself. The hold-assert is NOT
			// sent yet - it answers the client's phase-2 re-send above.
			var sit = BehaviorAnimationCodec.PlayerSitOnObject(player.Handle, handle, BehaviorAnimationCodec.SitRemoteformat(BenchObjectClass));
			var holdEcho = BehaviorAnimationCodec.BenchSitState(player.Handle, BehaviorAnimationCodec.SeqBegin, handle);

			foreach (var body in new[] { holdEcho, sit })
			{
				await SendWorldIir(client, body);
				await BroadcastBehaviorToViewers(client, body);
			}

			client.Session.SeatedOnHandle = handle;
			client.Session.SeatedPosition = player.Position;
			client.Session.SeatedHoldConfirmed = false;
			_logger.LogInformation("Sit: {Player} sat on '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name, handle);
			return true;
		}

		// How far a seated player must move from their seat before they stand up. While seated the client keeps
		// streaming position syncs at (roughly) the seat position - plus a one-time seat-snap of up to ~1 unit - and
		// those must NOT be read as "left the bench" (that stood the player straight back up). Only a real walk exceeds
		// this. Capture: the seated position syncs drift < 1 unit; the player is well past this by the time standup fires.
		private const float SeatLeaveDistance = 2.0f;

		// Stand the player back up when they've MOVED away from their seat (called from the move handler: standup is
		// triggered by movement, per the capture - not a second click). No-op when not seated or still within
		// SeatLeaveDistance of the seat. Broadcasts the "standup" animation to the actor + nearby viewers and clears
		// the seated state.
		public async Task StandUpIfMoved(INetworkClient client, Vector3 newPosition)
		{
			var handle = client.Session.SeatedOnHandle;
			if (handle == 0 || Vector3.Distance(newPosition, client.Session.SeatedPosition) < SeatLeaveDistance)
				return;

			client.Session.SeatedOnHandle = 0;
			client.Session.SeatedHoldConfirmed = false;
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var body = BehaviorAnimationCodec.PlayerStandUp(player.Handle, handle, BehaviorAnimationCodec.StandRemoteformat());
			await SendWorldIir(client, body);
			await BroadcastBehaviorToViewers(client, body);
			_logger.LogInformation("Sit: {Player} stood up from 0x{Handle:X8}.", player.Name, handle);
		}

		// The resolved destination of a zone-line teleporter: the map to hand the player off to, and where to place
		// them on it. DestMapId == 0 means the object IS a teleporter but its destination hasn't been captured yet -
		// the caller surfaces a message instead of silently dropping the player somewhere (no fallbacks).
		public readonly record struct ZoneLineDestination(uint DestMapId, Vector3? ArrivalPosition, string DestinationName);

		// The column-driven interaction of a USED world object: its InteractAction (what the object does) plus, for a
		// teleporter, the resolved Teleport destination. Action is None when the handle isn't a DB world object at all
		// (e.g. a loot bag, a zone entity) - the caller falls through to the open-animation / loot path. Lets the
		// UseObject handler switch on the action rather than special-casing one kind.
		public readonly record struct UsedObjectInteraction(InteractAction Action, ZoneLineDestination Teleport);

		// Resolve a used world object's interaction, DATA-DRIVEN from its InteractAction + Destination* columns
		// (populated by the world-teleports importer from captured UseObject->N3Teleport sequences). A teleporter
		// WITHOUT a captured destination yields Teleport.DestMapId == 0 so the caller can surface a message. (Whether
		// it's a same-playfield Teleport or a cross-playfield ProxyTeleport, the handoff path is the same - DoTeleport
		// change-maps only when the destination map differs.)
		public async Task<UsedObjectInteraction> ResolveUseObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			var obj = player == null
				? null
				: await _database.GetWorldObject(player.MapId, handle);
			if (obj == null)
				return new UsedObjectInteraction(InteractAction.None, default);

			var name = obj.Name ?? obj.DisplayName ?? "?";
			var teleport = obj.DestinationMapId is { } destMap
				? new ZoneLineDestination(destMap, obj.DestinationPosition, name)
				: new ZoneLineDestination(0, null, name);
			return new UsedObjectInteraction(obj.InteractAction, teleport);
		}

		// The quest-marker state byte for a handle the client polled via 0x2020 (resolves the entity in the player's
		// zone). 0x00 when it isn't a quest-giver in view. Lets the poll reply (SendSpawnCheckResponse) carry the
		// correct, state-driven marker instead of a hardcoded value.
		public async Task<byte> QuestMarkerStateForHandle(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(handle, out var npc) && !npc.IsPlayer)
				return await ComputeGiverMarkerState(client, npc.Name, npc.MapId);

			return QuestMarkerNone;
		}

		// #241: re-send a quest-giver's marker to one viewer after their quest state changed (accept/complete) while
		// the giver is ALREADY in view. Retail updates it in-place, not only on stream-in (capture frame 698: Casilda
		// -> 0x00 the instant the quest is accepted). Resolves the entity in the player's zone, then recomputes.
		public async Task RefreshQuestGiverMarker(INetworkClient client, uint npcHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			if (_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) && !npc.IsPlayer)
				await SendQuestMarkerIfGiver(client, npc);
		}

		// Resolves the playfield token a map's world-entry will stamp for a cross-map teleport's destination announce.
		// The map's real playfield-instance token (#305), threaded through map-change/spawn/world-objects/snapshot so
		// they agree by construction. Sourced per-map from Map.PlayfieldToken (the value the client scopes the
		// character to when it loads that playfield; the client keys per-area behaviour on it - e.g. mount permission,
		// so a wrong token makes Old Tarantia read as the no-mount Tortage beach). A missing MAP is a hard error and
		// throws; a found map with an un-captured token (0) falls back to the standard playfield token - that 0 is a
		// documented "no per-map override" state (most maps), not a lookup failure, so it is a real default rather than
		// a fallback masking a missing thing.
		public async Task<uint> ResolveMapPlayfieldToken(uint mapId)
		{
			var map = await _database.GetMap(mapId)
				?? throw new InvalidOperationException($"ResolveMapPlayfieldToken: map {mapId} not found");
			return map.PlayfieldToken != 0
				? map.PlayfieldToken
				: WorldState.DefaultPlayfieldToken;
		}

		// #262 companion-follow: make the NPC `npcHandle` follow `client`'s player (or stop, when follow=false). The
		// world tick (WorldState) then steps it toward the player and broadcasts a MoveTask each step. Returns the NPC,
		// or null if the handle isn't a non-player entity in the caller's zone. Used by the .npcfollow GM command and by
		// the Casilda rescue trigger.
		public WorldEntity SetNpcFollow(INetworkClient client, uint npcHandle, bool follow)
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
		public async Task<WorldEntity> SpawnNpcForPlayer(INetworkClient client, string npcName, bool killable = true)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var npc = await _database.GetNpcSpawnByName(npcName);
			var handle = NextRuntimeHandle(); // a fresh summoned-entity handle (high range, no player/NPC collision)
											  // Build wholly from columns (#243/#244); SendWorldSpawn re-stamps the position + token per recipient, so the
											  // baked ones are just placeholders (the player's zone token here).
			if (npc == null || !NpcSpawnBuilder.TryBuildLive(npc, handle, _sim.World.ZoneForEntity(player).PlayfieldToken, out var body))
			{
				_logger.LogInformation("Spawn: no spawnable '{NpcTemplate}' template found.", npcName);
				return null;
			}

			EntityVitals vitals = null;
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
			entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
			entity.Hostile = killable && entity.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId; // drop this type's container on death
			entity.SpawnBody = body;
			entity.NoRespawn = true; // summoned test NPCs don't revive - they fade out after death (GM .spawnnpc QoL)

			// Load this NPC type's caster spells (same as the zone-load path) so a summoned caster actually casts
			// (#232) instead of only meleeing - the sim's cast branch lights up when entity.Spells is non-empty.
			if (entity.Name is { Length: > 0 } spellName)
			{
				foreach (var s in await _database.GetNpcSpellsByName(spellName))
					entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToArray()));
			}

			_sim.World.ZoneForEntity(player).Add(entity);

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Spawn: spawned {NpcTemplate} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
			return entity;
		}

		// Spawns a creature TYPE at the player as a PET (#307/#244) - the RIGHT model: no NpcSpawns world placement,
		// the FA015807 is assembled from the type's columns (StatCore + Equipment + Morphs) via TryBuildFromType at the
		// summoner's position, then streamed to nearby viewers. The PetIIR owner-assign + follow are done by the caller
		// (SummonPetForPlayer). Returns null if the type carries no lifted stat core (not yet imported/lifted).
		public async Task<WorldEntity> SpawnPetForPlayer(INetworkClient client, NpcTemplate npcType)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || npcType?.StatCore?.IirHeader == null)
				return null;

			var handle = NextRuntimeHandle();
			if (!NpcSpawnBuilder.TryBuildFromType(npcType, handle, player.Position, player.Rotation, out var body))
			{
				_logger.LogInformation("Pet spawn: creature type '{Type}' has no buildable stat core.", npcType.Name);
				return null;
			}

			var level = npcType.StatCore?.Level ?? 0;
			var maxHealth = level > 0
				? CombatResolver.NpcMaxHealthForLevel(level)
				: 1;
			var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals, npcType.TemplateId);
			entity.InstanceId = player.InstanceId; // #268: the pet belongs to the SUMMONER's map INSTANCE, not the default
												   // (instance 0). Without this ZoneForEntity(pet) resolves a different zone/token
												   // than the player's, so SendWorldSpawn re-stamps the wrong playfield token and the
												   // client silently drops the spawn - no pet bar, no model. (Broke when #268 instanced.)
			entity.Level = level;
			entity.SpawnBody = body;
			entity.NoRespawn = true;

			foreach (var s in npcType.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToArray()));

			_sim.World.ZoneForEntity(player).Add(entity);
			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Pet: spawned creature type {Type} 0x{Handle:X8} for {Player}.", npcType.Name, handle, player.Name);
			return entity;
		}

		// Take ONE item from a bag by its display POSITION - TryLootItem (0x2046), the single drag-out. The client does
		// NOT compact its loot window after a removal: it removes the looted slot, leaves a GAP, and keeps every survivor
		// at its ORIGINAL Pos - proven live (a 3-item bag dragged srcPos 1, then 2, then 0: the 2 could only arrive if
		// the survivors kept their original positions). So the take must be position-STABLE: null the slot (template 0 =
		// looted) in place rather than RemoveAt - shifting desynced the next drag's Pos and dropped the take. Returns the
		// template id removed, or null if the bag/Pos is invalid or already looted. (Loot-all uses TakeBagItems.)
		public uint? TakeBagItem(INetworkClient client, uint bagHandle, int index)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return null;
			if (index < 0 || index >= bag.LootItems.Count || bag.LootItems[index] == 0)
				return null;

			var item = bag.LootItems[index];
			bag.LootItems[index] = 0; // looted-slot sentinel: survivors keep their stable Pos for the client's next drag
			_logger.LogInformation("Loot: {Player} loots item #{Index} (0x{Item:X}) from bag 0x{Handle:X8} ({Left} left).", player.Name, index, item, bagHandle, bag.LootItems.Count(i => i != 0));
			return item;
		}

		// A player clicked "loot all" (top-level 0x2043): hand the caller the bag's item templates to grant (via
		// DispatchAddItem) and clear them from the bag so each is granted exactly once. Empty when the bag is
		// unknown / not a loot bag / already emptied. Coins are a separate path (TryLootCash -> TakeLootCash).
		public IReadOnlyList<uint> TakeBagItems(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable || !bag.LootItems.Any(i => i != 0))
				return [];

			// Grant the still-present items (skip already-drag-looted slots, now 0); loot-all empties + closes the bag.
			var taken = bag.LootItems.Where(i => i != 0).ToArray();
			bag.LootItems.Clear();
			_logger.LogInformation("Loot: {Player} loots {Count} item(s) from bag 0x{Handle:X8}.", player.Name, taken.Length, bagHandle);
			return taken;
		}

		// TryLootCash (0x2045): takes the bag's coins exactly once - returns the amount and zeroes it on the bag
		// (the caller credits the wallet + pushes the 0x2005 balance update). 0 = no bag / no coins / already taken.
		public uint TakeLootCash(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return 0;

			var cash = bag.CashTin;
			bag.CashTin = 0;
			return cash;
		}

		// #259 toggle-combat: flip the player's manual combat stance and return the new state. The player entity's
		// InCombat flag is unused by the sim tick (players are skipped there), so it's the natural place to hold the
		// toggle. No-op-safe: returns false if the player isn't in the world yet.
		public bool TogglePlayerCombatStance(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return false;

			player.InCombat = !player.InCombat;
			return player.InCombat;
		}

		// True if the character (by handle == character id) has an ACTIVE LootFromNpc quest goal targeting `npcName` -
		// i.e. they need to loot this NPC's corpse for a quest (#261). Drives the guaranteed quest drop above.
		// The killer's active LootFromNpc goal targeting `npcName`, if any (#261): returns its QuestItemId (the key item
		// to put in the bag; may be 0 = no specific item), or null when there is no such goal. A non-null result both
		// forces the bag drop (the key must be lootable, not a 50/50 roll) and names the key item to include.
		private async Task<uint?> ActiveLootGoalItemFor(uint characterId, string npcName)
		{
			if (string.IsNullOrEmpty(npcName))
				return null;

			foreach (var quest in await _database.GetCharacterQuests(characterId))
			{
				if (quest.State != QuestState.Active)
					continue;

				var def = await _database.GetQuestDefinition(quest.QuestId);
				var goal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
				if (goal is { GoalType: QuestGoalType.LootFromNpc } && string.Equals(goal.TargetName, npcName, StringComparison.OrdinalIgnoreCase))
					return goal.QuestItemId ?? 0u; // 0 = goal present but no specific key item (distinct from null = no goal)
			}

			return null;
		}

		// The quest-marker state byte (#241) for a viewer + giver NPC name, from the player's CharacterQuests:
		// no quest held -> '!' (0x02); goals complete -> '?' (0x04); active/in-progress -> cleared (0x00). Returns
		// 0x00 for a non-giver. Shared by the interest-spawn push AND the 0x2020-poll reply so they never disagree.
		private async Task<byte> ComputeGiverMarkerState(INetworkClient client, string npcName, uint npcMapId)
		{
			var character = client.Session.Character;
			if (npcName == null || character == null)
				return QuestMarkerNone;

			var quests = await _database.GetCharacterQuests(character.Id);

			// '?' (0x04) when the player has an ACTIVE quest whose current goal is to TALK to this NPC - she's the
			// next step (e.g. the Casilda chain's talk-to-free stage 13480, active after looting Tuut's key). This is a
			// SEPARATE source from the giver logic below: the giver quest (13479) is already Completed/superseded by
			// then, so without this Casilda showed no '?'. Decoded from the captive-quest capture: retail sends 0x2008
			// state 0x04 to her handle at the loot->talk transition (Blablatest fr1455).
			foreach (var q in quests)
			{
				if (q.State != QuestState.Active)
					continue;

				var qDef = await _database.GetQuestDefinition(q.QuestId);
				var qGoal = qDef?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
				if (qGoal?.GoalType == QuestGoalType.TalkToNpc && string.Equals(qGoal.TargetName, npcName, StringComparison.OrdinalIgnoreCase))
					return QuestMarkerTurnIn;
			}

			_questGivers ??= await _database.GetQuestGiverMap();
			if (!_questGivers.TryGetValue(npcName, out var questId))
				return QuestMarkerNone;

			// A giver's '!'/'?' belongs ONLY to the NPC on the grant-quest's own map. NPC INTERNAL names are often a
			// shared placeholder - "Halen van Dummy" is the internal name of 25 unrelated templates (the castle Child,
			// dummies, ...) - so a name match alone paints the marker on every same-named NPC world-wide (e.g. the '!'
			// on the castle children for a Conarch-Village quest). Gate on the grant quest's MapId (its QuestDefinition,
			// #244): only the giver's own map shows it.
			var giverQuest = await _database.GetQuestDefinition(questId);
			if (giverQuest != null && giverQuest.MapId != npcMapId)
				return QuestMarkerNone;

			var held = Array.Find(quests, q => q.QuestId == questId);
			// '!' when the quest isn't held yet; '?' once its objective is done but it's still ACTIVE (awaiting turn-in
			// at the giver - the quest is only marked Completed on turn-in, not on the kill, #47/#241); no marker while
			// in progress or after it's been turned in (Completed).
			return held == null
				? QuestMarkerAvailable
				: held.State == QuestState.Active && held.GoalRequired > 0 && held.GoalProgress >= held.GoalRequired
					? QuestMarkerTurnIn
					: QuestMarkerNone;
		}

		// Dispatches interest events. `move` (the mover's Move components) is required to forward Move events and is
		// null on entry (entry produces only Spawn/Despawn).
		private async Task DispatchInterest(IReadOnlyList<InterestEvent> events, MoveForward? move)
		{
			foreach (var ev in events)
			{
				var recipient = ev.Recipient.Client;
				if (recipient == null)
					continue;

				switch (ev.Type)
				{
					case InterestEventType.Spawn when ev.Subject.SpawnBody != null:
					{
						// Register a streamed-in NPC's handle -> name + template so clicking it routes to the right
						// conversation/shop by the stable template key (the same map the world-burst spawns use; players
						// are not conversation targets).
						if (!ev.Subject.IsPlayer && !ev.Subject.IsLoot && ev.Subject.Name != null)
						{
							recipient.Session.SpawnedNpcNamesByHandle[ev.Subject.Handle] = ev.Subject.Name;
							recipient.Session.SpawnedNpcTemplatesByHandle[ev.Subject.Handle] = ev.Subject.NpcTemplateId;
						}
						await SendWorldSpawn(recipient, ev.Subject);
						// #241: when a quest-giver NPC enters view, set its marker from THIS viewer's quest state
						// (! available / ? ready-to-turn-in / none active). Recomputed per entry, so it transitions as
						// the player accepts/completes (e.g. Casilda shows ! then ? after the quest is done).
						if (!ev.Subject.IsPlayer && !ev.Subject.IsLoot)
							await SendQuestMarkerIfGiver(recipient, ev.Subject);
						// NOTE: we deliberately do NOT replay the captured patrol route (SendPatrol) on spawn. That route is
						// a CLIENT-only walk the SERVER can't track - it left entity.Position pinned at spawn while the client
						// walked the NPC around its loop, so the server placed the NPC's death-pose, loot bag, and aggro at
						// SPAWN, not where the player saw it (a killed patroller teleported its corpse back to spawn). The NPC
						// still moves via the server-driven wander (TickPatrolCycle, #266), which updates entity.Position so
						// death/loot/aggro land where the client actually has it.
						// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose); once the player has
						// freed her she streams in normally (and follows). Per-viewer + per-instance (the beach is Solo).
						if (!ev.Subject.IsPlayer && ev.Subject.Name == "Casilda" && recipient.Session?.Character != null && !await _questHandler.IsCasildaFreed(recipient.Session.Character.Id))
							await _questHandler.SendCasildaPose(recipient, ev.Subject.Handle, true);
						break;
					}

					case InterestEventType.Move when move.HasValue:
					{
						await SendWorldMove(recipient, move.Value);
						break;
					}

					case InterestEventType.Despawn:
					{
						await SendWorldDespawn(recipient, _sim.World.ZoneForEntity(ev.Recipient).PlayfieldToken, ev.Subject.Handle);
						break;
					}
				}
			}
		}

		// #261: reload the player entity's ActiveQuestIds from the DB (the set interest gating tests gated NPCs against).
		private async Task LoadActiveQuestIds(WorldEntity entity)
		{
			var characterId = entity?.Client?.Session.Character?.Id;
			if (characterId == null)
				return;

			var quests = await _database.GetCharacterQuests(characterId.Value);
			entity.ActiveQuestIds.Clear();
			foreach (var quest in quests)
			{
				if (quest.State == QuestState.Active)
					entity.ActiveQuestIds.Add(quest.QuestId);
			}
		}

		private uint NextRuntimeHandle()
		{
			return (uint)Interlocked.Increment(ref _nextRuntimeHandle);
		}

		// A few random snapshot-buildable item templates (Hash != 0) for a loot bag. Drops from the MAP's loot pool
		// (#273) - the items that map's captured bags actually carried - falling back to the all-items random sample when
		// the map has no captured pool (so a map we have no loot data for still drops something). Empty when count <= 0.
		private async Task<List<uint>> RollLootItems(int count, uint mapId)
		{
			if (count <= 0)
				return [];

			var items = await _database.GetMapLootSample(mapId, count);
			if (items.Length == 0)
				items = await _database.GetRandomBuildableItems(count); // map has no captured loot pool - fall back

			return items.Select(i => i.Id).ToList();
		}

		// #241: drive a quest-giver NPC's marker from the viewer's quest state when it enters view. Recomputed each
		// time the NPC is streamed in, so the '!' transitions to '?' (and clears) as the player accepts/completes the
		// quest - e.g. Casilda shows '!' before, nothing while held, '?' once the kill goal is done (return to turn in).
		private async Task SendQuestMarkerIfGiver(INetworkClient client, WorldEntity npc)
		{
			if (npc.Name == null || client.Session.Character == null)
				return;

			_questGivers ??= await _database.GetQuestGiverMap();
			if (!_questGivers.ContainsKey(npc.Name))
				return; // not a quest-giver -> no marker

			await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npc.Name, npc.MapId));
		}

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (WorldDespawn). Stamped with the
		// recipient's INSTANCE playfield token (#268; ZoneForEntity(recipient).PlayfieldToken at the call site): the
		// burst's value for a replayed playfield (what the client learned at entry), else WorldState's minted fallback.
		// The token-is-0 guard is just defensive (ZoneFor always mints one).
		private Task SendWorldDespawn(INetworkClient recipient, uint playfieldToken, uint subjectHandle, uint classWord = EntityClass.Character)
		{
			var token = playfieldToken;
			if (token == 0)
			{
				_logger.LogDebug("World: skip despawn {Subject:X8} for a viewer (playfield token unknown).", subjectHandle);
				return Task.CompletedTask;
			}

			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDespawn.BuildBody(subjectHandle, token, classWord)))
				.SendAsync(recipient);
		}

		// Sends the subject's FA015807 spawn to `recipient`, re-framed to the recipient and patched to the subject's
		// CURRENT position (its captured spawn body holds its entry position, which may be stale).
		private async Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject)
		{
			var body = (byte[])subject.SpawnBody.Clone();
			if (body.Length >= SpawnPacketBuilder.PosZOffset + 4)
			{
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosXOffset, 4), subject.Position.X);
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosYOffset, 4), subject.Position.Y);
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosZOffset, 4), subject.Position.Z);
			}

			// Re-stamp the playfield-instance token to the LIVE value at send time. The token baked into SpawnBody at
			// map-load (NpcSpawnBuilder.TryBuildLive) is the zone's DEFAULT token - but the client only registers, and
			// then polls (0x2020), a spawn whose playfield matches the one IT loaded at world entry, and that live token
			// isn't known until the player actually enters (docs/subsystems/mid-session-spawn-blocker.md part 3). Baking the
			// load-time token shipped every NPC with a stale playfield, so the client silently dropped them all and
			// never polled -> world entry never completed. Rewrite here, once the entering player's live token exists.
			SpawnPlayfieldRewriter.TryRewrite(body, _sim.World.ZoneForEntity(subject).PlayfieldToken);

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArray(body)
				.SendAsync(recipient);

			// #363 dev nameplate: the client draws an NPC's floating name from its template_id LDB name and ignores the
			// spawn-body `Name`, so overriding the visible name needs a SetNameIIR (0xADCE0CDA). Sent right AFTER the
			// spawn (in TCP order) so the entity + its OverheadText exist before the name lands. Dev-only, gated on the
			// live `.togglenpcgroup` toggle, so it never touches retail parity.
			if (DebugAreaLabels && subject.OverheadNameOverride is { } overrideName)
				await SendSetName(recipient, subject.Handle, overrideName);
		}

		// #363 pushes a SetNameIIR (0xADCE0CDA) to `recipient` overriding entity `handle`'s floating nameplate with
		// `name` - the in-world grammar recovered in CreateEditorRecordBuilder.BuildWorldSetNameIir (nameType 3 applies
		// the name AND refreshes the already-built OverheadText). Used by the spawn path and the .togglenpcgroup toggle.
		private static Task SendSetName(INetworkClient recipient, uint handle, string name)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteUInt32((uint)GameServer0X2000Opcodes.SetNameIIR).WriteArray(CreateEditorRecordBuilder.BuildWorldSetNameIir(handle, name)))
				.SendAsync(recipient);
		}

		// #363 `.togglenpcgroup`: flip the dev spawn-area nameplate labels for the WHOLE server, then immediately
		// re-apply to every labelled NPC the caller already knows in their zone - pushing the area label when turning ON,
		// or restoring the plain creature name (which matches the client's LDB name) when turning OFF - so the toggle is
		// visible without a re-stream. NPCs that stream in later pick it up in SendWorldSpawn. Returns (nowOn, affected).
		public async Task<(bool On, int Affected)> ToggleAreaLabels(INetworkClient client)
		{
			DebugAreaLabels = !DebugAreaLabels;
			var player = _sim.PlayerWorldEntity(client);
			var affected = 0;
			if (player != null)
				foreach (var npc in _sim.World.ZoneForEntity(player).Entities)
				{
					if (npc.OverheadNameOverride is not { } label || !player.Known.Contains(npc.Handle))
						continue;

					await SendSetName(client, npc.Handle, DebugAreaLabels ? label : npc.Name ?? "");
					affected++;
				}

			return (DebugAreaLabels, affected);
		}

		// Spawns a standalone IsLoot bag (its OWN handle) at a position, rendered for everyone in range - the decoupled
		// loot model: the bag is a real world object (no vitals, no respawn; tick-expired via TickExpiry), independent
		// of whatever dropped it. Used by .spawnbag AND by NPC death drops (#246) so the dead NPC's handle is free to
		// respawn at its spawn point and the bag survives that respawn. Returns the bag, or null if the map has no
		// loot template.
		private async Task<WorldEntity> SpawnLootBagAt(uint mapId, uint instanceId, Vector3 position, Quaternion rotation, IReadOnlyList<uint> itemTemplates, uint cashTin, uint allowedLooterHandle,
			string lootSourceName = null, LootContainerTemplateId variant = LootContainerTemplateId.Bag)
		{
			// #363 container variant: default is the generic bag; a boss/raid drop passes a bigger chest (LargeChest, …).
			var template = GetLootTemplate(variant);
			if (template == null)
				return null;

			var handle = NextRuntimeHandle();

			// Resolve the rolled templates to the items the loot window can actually DISPLAY (Hash != 0, present in
			// the item table) ONCE, here - so the bag's LootItems and the spliced display list share ONE ordering. A
			// non-buildable template dropped only at splice-time (the old behaviour) shifted every later display slot,
			// so a single-drag (TakeBagItem indexes LootItems by the client's display Pos) removed/granted the WRONG
			// item and the window showed one fewer than it held (#226 / handoff Problems 2+3).
			var loot = await _database.GetItems(itemTemplates);

			// Identity (unit) rotation in the wire transform: a bag has no facing, and a non-unit quaternion scales the
			// model. Built FROM SCRATCH (#249): header/transform + identity-from-columns + KdTreeTail, stamped with the
			// live playfield token directly - no captured Data, no SpawnPlayfieldRewriter pass.
			if (!WorldObjectBuilder.BuildBody(template, handle, position, Quaternion.Identity, LootContainerTemplate.TypeTag, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken, out var body))
				return null;

			body = WorldBroadcastBodies.WithLootContents(body, handle, cashTin, [.. loot.Select((l, i) => ((uint)i, l))]); // OUR items/cash, Pos 0..n-1

			var bag = WorldEntity.ForLoot(handle, mapId, position, rotation, template.Name ?? "Loot", body, LootBagExpirySeconds);
			bag.InstanceId = instanceId; // the bag lives in the same map COPY as whatever dropped it (#268)
			bag.CashTin = cashTin;
			bag.AllowedLooterHandle = allowedLooterHandle;
			bag.LootSourceName = lootSourceName; // the corpse's NPC name, for LootFromNpc quest-goal matching (#261)
			bag.LootItems.AddRange(loot.Select(l => l.Id)); // exactly the displayed set, same order as the window
			_sim.World.ZoneFor(mapId, instanceId).Add(bag);

			// Log the contents by display Pos so an in-client single-drag test can confirm the client's source-Pos maps
			// to the item the player actually clicked (TryLootItem 0x2046 is not in any capture - the Pos mapping is
			// verified live, not from a golden).
			if (loot.Length > 0)
				_logger.LogInformation("Loot: bag 0x{Handle:X8} contents by Pos: {Items}", handle, string.Join(", ", loot.Select((l, i) => $"#{i}=0x{l.Id:X}({l.Name})")));

			foreach (var viewer in _sim.World.ZoneFor(mapId, instanceId).PlayersInRange(position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await SendLootBagUpdate(viewer.Client, body);
				// #263: the LootBagFullUpdateIIR alone only renders a NEW world object on a playfield (re)load - the
				// client otherwise treats it as an update to an object it doesn't have yet (verified: our bags only
				// appeared after a relog). Retail follows the FullUpdate with a NewAllowedLooterIIR at the bag's
				// mid-session spawn (capture fr1358), which is what makes the client INSTANTIATE + render it live. This
				// packet is a loot-PERMISSION grant, so it names the bag's OWNER (the killer / spawning player), not the
				// recipient - a drop belongs to whoever earned it. Fall back to the viewer when there's no owner (e.g. a
				// legacy .spawnbag) so the bag still renders.
				await SendWorldIir(viewer.Client, WorldLoot.BuildNewAllowedLooterBody(handle, allowedLooterHandle != 0
					? allowedLooterHandle
					: viewer.Handle));
			}

			return bag;
		}

		// Sends the QuestJournal list (one title line per quest). The shared area header (region/map names) is taken
		// from the first definition. The trailing "tracked quest" pointer is the captured no-track block; pinning the
		// multi-quest tracked variant needs more decode.
		// ⚠️ CURRENTLY UNCALLED, and deliberately kept. Retail bundles a QuestJournal with QuestIIR x2 +
		// QuestFullUpdate in a SINGLE frame when a quest is accepted in-session; our accept path does not emit it yet.
		// Its former caller (RestorePlayerQuests) was a fabricated login burst and is gone (#389/#390).
		//
		// ⚠️ Before reusing it: it takes the area header from `definitions[0]` and files EVERY entry under that one
		// area. That is only correct for a single-area call - it is what rendered ~28 quests from 20 areas as one
		// collapsed group in the Completed tab.
		public static Task SendQuestJournal(INetworkClient client, IReadOnlyList<QuestDefinition> definitions)
		{
			var first = definitions[0];
			var areaHeader = new[]
			{
				WorldBroadcastBodies.Record(first, QuestTextSlot.AreaTitle), WorldBroadcastBodies.Record(first, QuestTextSlot.MapName), WorldBroadcastBodies.Record(first, QuestTextSlot.RegionName)
			};
			var entries = definitions
				.Select(d => new WorldQuest.JournalEntry(d.QuestId, WorldBroadcastBodies.Record(d, QuestTextSlot.Title), WorldBroadcastBodies.Record(d, QuestTextSlot.ListSubLine)))
				.ToList();
			var trailing = WorldQuest.BuildTrackedQuestPointer();

			var body = WorldQuest.BuildQuestJournal(client.Session.Account.ClientInstance, first.AreaId, areaHeader, entries, trailing);
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(client);
		}

		// Sends the active-quest LIST (0x504B7924) that POPULATES the J-panel - WITHOUT it an accepted quest never
		// appears in the log even though the client got its QuestFullUpdate detail (decoded from the grant capture). trackedDef is the highlighted (just-accepted) quest; activeQuestIds are all the
		// player's active quests with the tracked one FIRST. The area-title slot comes from the tracked quest's def.
		public static Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			var body = WorldQuest.BuildQuestLogList(client.Session.Account.ClientInstance, trackedDef.AreaId, trackedDef.QuestId, activeQuestIds,
				WorldBroadcastBodies.Record(trackedDef, QuestTextSlot.AreaTitle));
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(client);
		}

		// The loot-bag definition (#249): the baked canonical "Bag of loot" (columns + embedded KdTreeTail), built FROM
		// SCRATCH by WorldObjectBuilder.BuildBody - map-independent, needs NO captured loot-bag WorldObject in
		// the world (deleting static world bags no longer breaks kill-loot). Null only if the geometry asset is missing.
		private static WorldObject GetLootTemplate(LootContainerTemplateId variant = LootContainerTemplateId.Bag)
		{
			return LootContainerTemplate.Create(variant);
		}

		// Sends a prebuilt LootBagFullUpdateIIR body (handle + transform already baked by WorldObjectBuilder) on
		// the world conversation. Unlike SendWorldSpawn this does NOT re-patch the position - the body is already
		// placed at the corpse's spot.
		private static Task SendLootBagUpdate(INetworkClient recipient, byte[] body)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArray(body)
				.SendAsync(recipient);
		}

		// #266 patrol: issue the NPC's captured patrol route as a MoveTask so the CLIENT walks it (retail drives idle
		// patrol client-side - the server sends the route once per spawn/respawn, not a per-tick step). The anchor /
		// current position is the NPC's live spawn position (the render-gate requires the MoveTask position to match
		// the spawn it was just sent); the waypoints are the absolute captured world coords. BuildPatrol emits a
		// self-contained record (it prepends its own [u32 len]), so it rides SendWorldRecord, not SendWorldIir.
		private static Task SendPatrol(INetworkClient recipient, WorldEntity npc)
		{
			if (!npc.Patrols)
				return Task.CompletedTask;

			// Replay the captured MoveTask record (already re-addressed to this NPC's handle + spawn position). Encode
			// emits a bare 0x2000 body (no length prefix), so it rides SendWorldIir.
			return SendWorldIir(recipient, npc.PatrolBody);
		}

		// DispatchSetQuestMarker body: [u32 0xC350][u32 npcHandle][u8 state], on the GameCharInterface dispatch
		// (same framing as the static create-editor marker burst).
		private static Task SendQuestMarker(INetworkClient client, uint npcHandle, byte state)
		{
			return new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x59), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchSetQuestMarker)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(npcHandle)
				.WriteByte(state)
				.SendAsync(client);
		}

		// Frames a raw 0x2000 IIR body on the world conversation to one recipient (the generic single-recipient send the
		// dedicated builders share - e.g. the NPC MoveTask chase step).
		private static Task SendWorldIir(INetworkClient recipient, byte[] body)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(recipient);
		}

		// Forwards a player's Move to `recipient` (same body - the moving entity's handle + transform - only the
		// receiver differs), so the recipient sees the subject move.
		private static Task SendWorldMove(INetworkClient recipient, MoveForward move)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(move.Subop)
					.WriteUInt32(move.DataObject)
					.WriteUInt32(move.DataClientInstance)
					.WriteUInt16(move.MovingType)
					.WriteVector3(move.Position)
					.WriteVector3(move.Rotation)
					.WriteArray(move.Tail))
				.SendAsync(recipient);
		}

		// Sends a SELF-CONTAINED IIR record (one whose builder already prepends the [u32 len] prefix, e.g. the
		// ChatText/Knubot/dead-reckoning codecs) - written verbatim, NOT re-wrapped with another length (which would
		// double-prefix it). Mirrors ChatTextSender. Use SendWorldIir for bare bodies (MoveTask/loot/vitals).
		private static Task SendWorldRecord(INetworkClient recipient, byte[] record)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArray(record)
				.SendAsync(recipient);
		}

		// The components of a player's Move, forwarded verbatim to other viewers (only the receiver differs).
		private readonly record struct MoveForward(uint Subop, uint DataObject, uint DataClientInstance, ushort MovingType, Vector3 Position, Vector3 Rotation, byte[] Tail);
	}
}
