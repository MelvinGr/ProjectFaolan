using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Economy;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.GameServer.World
{
    // Turns world-model events into wire packets: spawns/moves/despawns for interest management, NPC respawn/loot/mount
    // renders, loot-window and quest-journal packets (docs/archive/server-architecture-roadmap.md). Extracted out of the
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
        private const double PatrolDwellSeconds = 2.0;

        // #266 patrol cycle: estimated client WALK speed (world units/sec) used to time how long a patrol route takes to
        // traverse, plus a dwell pause at the end, before the route is re-issued so the NPC keeps looping. These are the
        // re-issue cadence only (the client drives the actual walk); tune against in-client pacing if a patrol pauses or
        // restarts mid-walk. NpcMoveSpeed (12) is the RUN speed; a walk is roughly half.
        private const float PatrolWalkSpeed = 6f;

        // LootBagFullUpdateIIR_c - the world-object sub-opcode a loot bag spawns as.
        private const uint LootBagSubOpcode = (uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR;

        // The combat-mode stat value the captured beach melee mobs send at aggro (strickenbla capture, #218): the tick
        // before each mob's first swing it pushes `CombatMode = 4`, which is what draws its weapon. (The player uses 1
        // and one non-swinging entity used 1 - the field is a stance/mode code that isn't fully decoded, but 4 is the
        // value every actual melee swinger sends.)
        private const uint NpcCombatModeValue = 4;

        // The combat-mode stat value a PLAYER sends when it draws its weapon (#218/#259): 1, vs the mob's 4 (the field is
        // a stance/mode code; 1 is the value the captured player carries on combat entry). 0 sheathes.
        private const uint PlayerCombatModeValue = 1;

        // (mapId, instanceId) zones whose NPCs have been loaded into the world model (once per instance, on first entry
        // to that copy - each instance gets its own NPC population) (#268).
        private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _npcLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

        // Cached loot-bag template (a captured "Bag of loot" world object) per map; null values cache loot-less maps.
        private readonly IDatabaseRepository _database;
        private readonly ILogger _logger;

        private readonly WorldSimulation _sim;

        // Quest-gated NPC name -> the quest ids that reveal it (#261). Cached once; an NPC in this map is streamed only
        // to players holding one of its quests active (see WorldEntity.GateQuestIds / IsVisibleTo).
        private Dictionary<string, uint[]> _questGatedNpcs;

        // Quest-giver NPC name -> granted quest id, cached once (small set).
        private Dictionary<string, uint> _questGivers;

        // Monotonic allocator for dropped-loot / summoned-entity handles. CRITICAL: the value must be in the SAME
        // LOW range retail assigns mid-session entity handles - the client only tracks/polls handles in that range,
        // and the render of a mid-session spawn is completed by the client polling 0x2020 for the entity's handle
        // (-> server SpawnCheck 0x2008). Captured retail mid-session spawns (the mount + interest NPCs) use
        // 0x000286xx; the burst NPCs use 0x000818xx; the player/char-id is ~0x0802xxxx. A high value (the old
        // 0x40000000, or even 0x0F000000) is FAR above any real handle, so the client never polls it and the entity
        // silently never renders. 0x00090000 sits just above the burst NPCs (0x000818xx) and well below the char-id
        // range (0x02000000+) - small, valid, collision-free. (Matches the RetailLoginReplay built-NPC test handle.)
        private int _nextLootHandle = 0x00090000;

        public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger)
        {
            _sim = sim;
            _database = database;
            _logger = logger;
        }

        // #267: drop the beach "invisible wall". The wall is the Casilda chain decor's COLLISION (the
        // `004709_Global__decor_chain_hangingwoman` SimpleDynel sent at world entry; its mesh doesn't render for us, so
        // it reads as an invisible barrier). Retail flips it off with a SimpleDynel visual-change (state 5) to that dynel
        // the instant Casilda is freed on the beach (Hoxxling gameplay idx 2053). Looked up by the chain world-object's
        // identity so the handle matches what the client was sent at entry (its CapturedHandle). No-op off-beach / if the
        // map has no such chain.
        public async Task DropBeachWall(INetworkClient client)
        {
            var player = _sim.PlayerWorldEntity(client);
            if (player == null)
                return;

            var chain = (await _database.GetWorldObjects(player.MapId)).FirstOrDefault(o => o.SubOpcode == SimpleDynelMessageCodec.SubOpcode && o.Name != null && o.Name.Contains("decor_chain_hangingwoman"));
            if (chain == null)
                return;

            await SendWorldIir(client, SimpleDynelMessageCodec.BuildVisualChange(chain.CapturedHandle, 0x05));
            _logger.LogInformation("#267: dropped beach chain-wall collision (handle 0x{Handle:X8}) for {Player}.", chain.CapturedHandle, player.Name);

            // She's freed now: clear the captive (chained) struggle pose so she stands/walks normally as she follows
            // (the live counterpart to the captive pose the interest stream sends while she's a prisoner).
            var casilda = _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == "Casilda");
            if (casilda != null)
                await SendCasildaPose(client, casilda.Handle, false);
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
                await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npcName));
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
        public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint state = WorldQuest.FullUpdateStateActive)
        {
            var refs = new[]
                {
                    QuestTextSlot.Title,
                    QuestTextSlot.Summary,
                    QuestTextSlot.AreaTitle,
                    QuestTextSlot.RegionName,
                    QuestTextSlot.MapName
                }
                .Select(s => WorldBroadcastBodies.Record(def, s))
                .ToArray();
            var body = WorldQuest.BuildQuestFullUpdate(client.Session.Account.ClientInstance,
                def.QuestId,
                state,
                def.AreaId,
                refs,
                def.GoalId,
                Statics.NowUnixTimeSeconds,
                def.MapId,
                def.MarkerX,
                def.MarkerY,
                WorldBroadcastBodies.Record(def, QuestTextSlot.Objective),
                WorldBroadcastBodies.Record(def, QuestTextSlot.ObjectiveLocalized));

            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
        }

        // Sends a QuestGoal (objective progress) to the player.
        public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count)
        {
            return new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
                .WriteArrayUInt32Length(s => s.WriteArray(WorldQuest.BuildQuestGoal(client.Session.Account.ClientInstance, questId, goalId, count)))
                .SendAsync(client);
        }

        // Sends a QuestIIR (add/update a quest in the player's journal) to the player.
        public Task SendQuestIir(INetworkClient client, uint questId, uint state)
        {
            return new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
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
                : 0;
            _logger.LogInformation("Follow: {Name} {State} player 0x{Player:X8} (by name).",
                npcName,
                follow
                    ? "now follows"
                    : "stopped following",
                playerHandle);
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
                .WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x58),
                    ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance),
                    GameCharInterfaceDispatchOpcodes.DispatchCloseLootBag)
                .WriteUInt32(WorldLoot.LootTypeTag)
                .WriteUInt32(bag.Handle)
                .SendAsync(client);
            await SendWorldIir(client, WorldLoot.BuildLooterResetBody(WorldLoot.NewCurrentLooterSubOpcode, bag.Handle));
            await SendWorldIir(client, WorldLoot.BuildLooterResetBody(WorldLoot.NewAllowedLooterSubOpcode, bag.Handle));
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

                await new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(viewer.Client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(viewer.Client);
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
            var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode,
                inCombat
                    ? NpcCombatModeValue
                    : 0u);
            var body = VitalsPacket.BuildStatBlockBody(npc.Handle, stats);

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

            var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode,
                inCombat
                    ? PlayerCombatModeValue
                    : 0u);
            var body = VitalsPacket.BuildStatBlockBody(player.Handle, stats);

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
            var npcEvents = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, true);
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
                _logger.LogInformation("Interest: {Player} at {Pos} -> streamed +{Spawns} / -{Despawns} entities (now knows {Known}).", entity.Name, entity.Position, spawns, despawns, entity.Known.Count);

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

        // On a kill, MAYBE turn the dead NPC's corpse into a lootable bag (LootResolver coin-flip + a map loot
        // template). The bag REUSES the NPC's own handle: the client already renders the corpse, so the
        // LootBagFullUpdateIIR (0xF096251E) is an UPDATE it accepts - introducing a brand-new world object
        // mid-session does NOT render (the client only takes world objects at playfield-load; confirmed against
        // captures where every loot bag is introduced in the world-entry burst, never mid-session). The entity
        // stays dead in the model and respawns off the tick (which clears the loot). Returns true if the corpse
        // became a bag, so the caller skips the despawn.
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
            var lootGoalItem = deadNpc.LastAttackerHandle != 0
                ? await ActiveLootGoalItemFor(deadNpc.LastAttackerHandle, deadNpc.Name)
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
                        : 0,
                    deadNpc.MapId); // 0-2 items, or none
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
                // #265: drop the bag at the KILLER's live position when we can resolve it, NOT the NPC's death spot. A mob
                // that chased the player dies wherever it walked to, and that server position can be off the client's
                // ground (the MoveTask Z drift, [[npc_spawn_movetask_position]]/#190) - the bag's mesh then clips into
                // terrain and only its spawn particle renders. The killer is provably on valid ground (they're standing
                // there, rendering), and that is exactly the proven-good `.spawnbag` path. Fall back to the death spot
                // when the attacker can't be resolved (expired session, environmental death).
                var dropPos = deadNpc.Position;
                var dropRot = deadNpc.Rotation;
                if (deadNpc.LastAttackerHandle != 0 && _sim.World.ZoneForEntity(deadNpc).TryGet(deadNpc.LastAttackerHandle, out var killer) && killer.IsPlayer)
                {
                    dropPos = killer.Position;
                    dropRot = killer.Rotation;
                }

                var bag = await SpawnLootBagAt(deadNpc.MapId, deadNpc.InstanceId, dropPos, dropRot, items, cash, deadNpc.LastAttackerHandle, deadNpc.Name);
                _logger.LogInformation("Loot: {Name} 0x{Handle:X8} dropped bag 0x{Bag:X8} ({Items} items, {Cash} tin, looter 0x{Looter:X8}, quest={Quest}) on map {Map} at {Drop} (death spot {Death}).",
                    deadNpc.Name,
                    deadNpc.Handle,
                    bag?.Handle ?? 0,
                    items.Count,
                    cash,
                    deadNpc.LastAttackerHandle,
                    questDrop,
                    deadNpc.MapId,
                    dropPos,
                    deadNpc.Position);
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

            // #266 patrol: captured routes keyed at their paired spawn's position (the importer correlated route->spawn
            // by handle), so link each route to its NPC by exact position below. The NPC then walks it client-side via a
            // patrol MoveTask issued at spawn (retail style).
            var patrolByPosition = (await _database.GetPatrolPaths(mapId)).GroupBy(p => PositionKey(p.PositionX, p.PositionY, p.PositionZ)).ToDictionary(g => g.Key, g => g.First());

            // #261 quest-gated spawns: NPC name -> the quest ids that reveal it (kill-target NPCs that aren't givers).
            _questGatedNpcs ??= await _database.GetQuestGatedNpcMap();

            // NpcSpawns accretes one row per capture observation, so the same physical NPC appears many times - the
            // same name at near-identical positions across sessions (e.g. Kalanthes captured 5×, all at his hideout).
            // Burst-as-truth lives in the IMPORTER now (#266, BuildBurstTruthSpawns): NpcSpawns holds only each map's
            // authoritative login-burst faction set + the named/unique NPCs, so the cross-session duplicate density (a
            // Crocodile logged at 50 spots -> 50 phantom copies) never reaches the DB. This load path just reads the
            // already-clean rows; the (name, coarse cell) dedup below is a cheap residual safety net.
            var seenCells = new HashSet<(string, int, int, int)>();
            foreach (var npc in npcs)
            {
                // The type identity (name/community) lives on the Npc type now (#244); a spawn with no type is a phantom
                // capture artifact (no real creature) - skip it.
                if (npc.Npc == null)
                    continue;

                var npcName = npc.Npc.Name;
                var community = npc.Npc.Community;

                // Faction mobs (Community > 0) are legitimately MANY of the same name spread across the map - dedup by
                // (name, coarse cell): same-spot cross-session re-captures collapse, distinct mobs in different cells
                // survive. Friendly/neutral named NPCs (Community 0: quest-givers like Casilda/Kalanthes + a few neutral
                // critters) are UNIQUE - but their cross-session captures land at genuinely DIFFERENT spots (Casilda was
                // captured at ~361 AND ~448, 87 units apart), so cell dedup keeps phantom duplicates that bloat the
                // spawn set and confuse the client. Collapse those by NAME alone -> one instance (the burst-handle row
                // wins, ordered first, so the kept entity coincides with the burst-rendered one).
                const float cell = 8f;
                var dedupKey = community == 0
                    ? (npcName ?? "", 0, 0, 0)
                    : (npcName ?? "", (int)(npc.PositionX / cell), (int)(npc.PositionY / cell), (int)(npc.PositionZ / cell));
                if (!seenCells.Add(dedupKey))
                    continue;

                // Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
                // importer), falling back to a level-scaled value when the spawn carried none.
                var maxHealth = npc.Health > 0
                    ? npc.Health
                    : CombatResolver.NpcMaxHealthForLevel(npc.Level);
                var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
                // World entry is burst-less, so every NPC streams in on the player's first move (interest) under its own
                // captured handle - there is no separate burst handle to reconcile with.
                var handle = npc.CapturedHandle;
                var entity = WorldEntity.ForNpc(handle, mapId, npc.Position, npc.Rotation, npcName ?? "NPC", vitals);
                entity.InstanceId = instanceId; // this NPC belongs to THIS copy of the map (#268)
                entity.Level = npc.Level;
                // #261: if this NPC's spawn is quest-gated, stamp the revealing quest ids so interest only streams it to
                // players holding one of them (e.g. Scavenger Tuut while you carry Casilda's kill quest).
                if (npcName != null && _questGatedNpcs.TryGetValue(npcName, out var gateQuests))
                    entity.GateQuestIds = gateQuests;

                // #266 patrol: attach this NPC's captured route (linked by exact spawn position). REPLAY the captured
                // MoveTask re-addressed to this entity (handle + from = its spawn) - the original form carries the real
                // speed/flags, so the client walks the route at the right pace (a rebuilt mode-1 path lost the speed and
                // run-crawled). The cycle timer (path time + dwell) re-issues it so it keeps looping.
                if (patrolByPosition.TryGetValue(PositionKey(npc.PositionX, npc.PositionY, npc.PositionZ), out var patrol) && patrol.Body is { Length: > 0 } && patrol.Waypoints.Count > 0)
                {
                    entity.PatrolWaypoints = patrol.Waypoints.OrderBy(w => w.Ordinal).Select(w => new Vector3(w.PositionX, w.PositionY, w.PositionZ)).ToList();
                    entity.PatrolSpeed = patrol.Speed;
                    entity.PatrolBody = WorldBroadcastBodies.ReaddressPatrol(patrol.Body, handle, entity.Position);
                    entity.PatrolCycleSeconds = MoveTaskCodec.TotalPathLength(entity.Position, entity.PatrolWaypoints) / PatrolWalkSpeed + PatrolDwellSeconds;
                    entity.ArmPatrolCycle();
                }

                // Hostile = a faction creature (Npc.Community > 0, lifted from NpcStats.community by the importer).
                // community 0 is the neutral/friendly community (quest-givers like Casilda/Kalanthes AND neutral critters
                // like Scorpion/Guard Dog); faction mobs (poachers 59, scavengers 60, picts 317, crocs 374, ...) are > 0.
                entity.Hostile = community > 0;

                // Keep the NPC's FA015807 spawn body (addressed to the chosen handle) so interest/respawn can re-render
                // it for nearby players - built from the row by NpcSpawnBuilder, the same path the burst-replacement uses.
                if (NpcSpawnBuilder.TryBuild(npc, handle, out var spawnBody, true))
                    entity.SpawnBody = spawnBody;

                // Caster NPCs (#232): the spells this NPC type casts, read from the #244 type FK (npc.Npc.Spells, loaded
                // with the spawn) - this replaces the fragile NpcName string join. Empty for non-casters.
                if (npc.Npc.Spells is { Count: > 0 } typeSpells)
                {
                    foreach (var s in typeSpells)
                        entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToBytes()));
                }

                zone.Add(entity);
            }

            _logger.LogInformation("World: loaded {Count} NPC(s) into map {Map} instance 0x{Instance:X8} ({Raw} rows, {Collapsed} cross-session re-captures collapsed).",
                zone.Count,
                mapId,
                instanceId,
                npcs.Length,
                npcs.Length - seenCells.Count);
        }

        // A player opened a world object (Interact UseObject). If it's a loot bag in their zone, OPEN ITS LOOT WINDOW
        // per the decoded flow (protocol_loot_interaction): assign the player as the current looter and send the
        // SimpleDynelVisualChange "Open" so the client shows the bag's contents. The bag STAYS in the world - it is
        // consumed on close (0x2044), not on open. Taking an item (0x2043 -> grant + LootRemoveItems) is the remaining,
        // in-client-gated piece. No-op for non-loot / unknown objects.
        public async Task OpenLootBag(INetworkClient client, uint objectHandle)
        {
            var player = _sim.PlayerWorldEntity(client);
            if (player == null || !_sim.World.ZoneForEntity(player).TryGet(objectHandle, out var bag) || !bag.IsLootable)
                return;

            // NewCurrentLooter rides the WORLD conversation (GameAgent -> GameInterface), as in retail (mount/gameplay
            // capture frame 1477).
            await new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
                .WriteArrayUInt32Length(s => s.WriteArray(WorldLoot.BuildNewCurrentLooterBody(bag.Handle, player.Handle)))
                .SendAsync(client);

            // The actual window-open is DispatchOpenLootBag (0x2037) on the GameCharAgent -> GameCharInterface
            // conversation (the same class-pair as the 0x201C spawn handshake / SpawnCheck), body [loot typetag][bag
            // handle] - NOT a SimpleDynelVisualChange on the world conversation (that only played the open animation, no
            // window). Decoded from the hoxxling gameplay capture (f1477): after this the client itself sends
            // TryLootCash (0x2045) then TryLootAll (0x2043), which we already handle.
            await new PacketStream()
                .WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x58),
                    ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance),
                    GameCharInterfaceDispatchOpcodes.DispatchOpenLootBag)
                .WriteUInt32(WorldLoot.LootTypeTag)
                .WriteUInt32(bag.Handle)
                .SendAsync(client);

            _logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
        }

        // The quest-marker state byte for a handle the client polled via 0x2020 (resolves the entity in the player's
        // zone). 0x00 when it isn't a quest-giver in view. Lets the poll reply (SendSpawnCheckResponse) carry the
        // correct, state-driven marker instead of a hardcoded value.
        public async Task<byte> QuestMarkerStateForHandle(INetworkClient client, uint handle)
        {
            var player = _sim.PlayerWorldEntity(client);
            if (player != null && _sim.World.ZoneForEntity(player).TryGet(handle, out var npc) && !npc.IsPlayer)
                return await ComputeGiverMarkerState(client, npc.Name);

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
        // so a wrong token makes Old Tarantia read as the no-mount Tortage beach). Falls back to the beach default when
        // a map has no captured token yet, preserving the prior single-token behaviour for un-sourced maps.
        public async Task<uint> ResolveMapPlayfieldToken(uint mapId)
        {
            var token = (await _database.GetMap(mapId))?.PlayfieldToken ?? 0;
            return token != 0
                ? token
                : WorldState.DefaultPlayfieldToken;
        }

        // On world entry: re-send the player's persisted active quests so their journal is restored.
        public async Task RestorePlayerQuests(INetworkClient client)
        {
            var character = client.Session?.Character;
            if (character == null)
                return;

            // Restore the LIVE journal only for active quests NOT carried by the world-entry snapshot. A
            // snapshot-eligible quest (Quest_c scalars + header text refs seeded - beach starter, Casilda 13479, ...)
            // already rides the snapshot's multi-quest region (#255), so re-sending it live would double-deliver.
            var active = (await _database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Active).ToList();
            var definitions = new List<(CharacterQuest Quest, QuestDefinition Def)>();
            foreach (var quest in active)
            {
                var def = await _database.GetQuestDefinition(quest.QuestId);
                if (FullCharSnapshotSubClass.IsSnapshotEligible(def))
                    continue; // delivered in the snapshot's quest region

                await SendQuestIir(client, quest.QuestId, WorldQuest.StateActive);
                if (quest.GoalRequired > 0)
                    await SendQuestGoal(client, quest.QuestId, 0, quest.GoalProgress);

                if (def != null)
                {
                    await SendQuestFullUpdate(client, def);
                    definitions.Add((quest, def));
                }
            }

            // One journal-list packet covers every quest that has a seeded definition (the J-panel title lines).
            if (definitions.Count > 0)
                await SendQuestJournal(client, definitions.Select(d => d.Def).ToList());
        }

        // #267: at world entry, drop the wall up-front for a character who has ALREADY freed Casilda (the talk stage is
        // completed), so a returning player isn't blocked by the chain collision the world-entry burst re-sends. No-op
        // off the beach or before she's freed.
        public async Task SendBeachGateOnEntry(INetworkClient client)
        {
            var character = client.Session?.Character;
            if (character == null || character.MapId != Map.TortageBeachId)
                return;

            if (await IsCasildaFreed(character.Id))
                await DropBeachWall(client);
        }

        // Sends the QuestJournal list (one title line per quest). The shared area header (region/map names) is taken
        // from the first definition. The trailing "tracked quest" pointer is the captured no-track block; pinning the
        // multi-quest tracked variant needs more decode (docs/quest-log-handover.md).
        public Task SendQuestJournal(INetworkClient client, IReadOnlyList<QuestDefinition> definitions)
        {
            var first = definitions[0];
            var areaHeader = new[]
            {
                WorldBroadcastBodies.Record(first, QuestTextSlot.AreaTitle),
                WorldBroadcastBodies.Record(first, QuestTextSlot.MapName),
                WorldBroadcastBodies.Record(first, QuestTextSlot.RegionName)
            };
            var entries = definitions.Select(d => new WorldQuest.JournalEntry(d.QuestId, WorldBroadcastBodies.Record(d, QuestTextSlot.Title), WorldBroadcastBodies.Record(d, QuestTextSlot.ListSubLine))).ToList();
            var trailing = WorldQuest.BuildTrackedQuestPointer();

            var body = WorldQuest.BuildQuestJournal(client.Session.Account.ClientInstance, first.AreaId, areaHeader, entries, trailing);
            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
        }

        // Sends the active-quest LIST (0x504B7924) that POPULATES the J-panel - WITHOUT it an accepted quest never
        // appears in the log even though the client got its QuestFullUpdate detail (decoded from the grant capture;
        // docs/quest-log-handover.md). trackedDef is the highlighted (just-accepted) quest; activeQuestIds are all the
        // player's active quests with the tracked one FIRST. The area-title slot comes from the tracked quest's def.
        public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
        {
            var body = WorldQuest.BuildQuestLogList(client.Session.Account.ClientInstance, trackedDef.AreaId, trackedDef.QuestId, activeQuestIds, WorldBroadcastBodies.Record(trackedDef, QuestTextSlot.AreaTitle));
            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
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
                : 0;
            _logger.LogInformation("Follow: {Name} 0x{Handle:X8} {State} {Player}.",
                npc.Name,
                npcHandle,
                follow
                    ? "now follows"
                    : "stopped following",
                player.Name);
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
            var handle = NextLootHandle(); // a fresh summoned-entity handle (high range, no player/NPC collision)
            if (npc == null || !NpcSpawnBuilder.TryBuild(npc, handle, out var body, true))
            {
                _logger.LogInformation("Spawn: no spawnable '{Npc}' template found.", npcName);
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

            var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npc.Npc?.Name ?? npcName, vitals);
            entity.Level = npc.Level;
            entity.Hostile = killable && npc.Npc?.Community > 0;
            entity.SpawnBody = body;
            entity.NoRespawn = true; // summoned test NPCs don't revive - they fade out after death (GM .spawnnpc QoL)

            // Load this NPC type's caster spells (same as the zone-load path) so a summoned caster actually casts
            // (#232) instead of only meleeing - the sim's cast branch lights up when entity.Spells is non-empty.
            if (entity.Name is { Length: > 0 } spellName)
            {
                foreach (var s in await _database.GetNpcSpellsByName(spellName))
                    entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToBytes()));
            }

            _sim.World.ZoneForEntity(player).Add(entity);

            foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
            {
                if (viewer.Client != null)
                    await SendWorldSpawn(viewer.Client, entity);
            }

            _logger.LogInformation("Spawn: spawned {Npc} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
            return entity;
        }

        // Spawns a creature TYPE at the player as a PET (#307/#244) - the RIGHT model: no NpcSpawns world placement,
        // the FA015807 is built from the type's lifted stat core (Npc.Data) via TryBuildFromType at the summoner's
        // position, then streamed to nearby viewers. The PetIIR owner-assign + follow are done by the caller
        // (SummonPetForPlayer). Returns null if the type carries no lifted Data (not yet imported/lifted).
        public async Task<WorldEntity> SpawnPetForPlayer(INetworkClient client, Npc npcType)
        {
            var player = _sim.PlayerWorldEntity(client);
            if (player == null || npcType?.Data == null)
                return null;

            var handle = NextLootHandle();
            if (!NpcSpawnBuilder.TryBuildFromType(npcType, handle, player.Position, player.Rotation, out var body))
            {
                _logger.LogInformation("Pet spawn: creature type '{Type}' has no buildable stat core.", npcType.Name);
                return null;
            }

            var maxHealth = npcType.Level > 0
                ? CombatResolver.NpcMaxHealthForLevel(npcType.Level)
                : 1;
            var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
            var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals);
            entity.Level = npcType.Level;
            entity.SpawnBody = body;
            entity.NoRespawn = true;

            foreach (var s in npcType.Spells)
                entity.Spells.Add(new NpcSpellCast(s.SpellId, new NpcSpellEffect(s.SpellId, s.Duration, s.Magnitude, s.Rank, s.FlagA, s.FlagB, s.FlagC).ToBytes()));

            _sim.World.ZoneForEntity(player).Add(entity);
            foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
            {
                if (viewer.Client != null)
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
                    return goal.QuestItemId;
            }

            return null;
        }

        // The quest-marker state byte (#241) for a viewer + giver NPC name, from the player's CharacterQuests:
        // no quest held -> '!' (0x02); goals complete -> '?' (0x04); active/in-progress -> cleared (0x00). Returns
        // 0x00 for a non-giver. Shared by the interest-spawn push AND the 0x2020-poll reply so they never disagree.
        private async Task<byte> ComputeGiverMarkerState(INetworkClient client, string npcName)
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

            var held = Array.Find(quests, q => q.QuestId == questId);
            // '!' when the quest isn't held yet; '?' once its objective is done but it's still ACTIVE (awaiting turn-in
            // at the giver - the quest is only marked Completed on turn-in, not on the kill, #47/#241); no marker while
            // in progress or after it's been turned in (Completed).
            return held == null ? QuestMarkerAvailable : held.State == QuestState.Active && held.GoalRequired > 0 && held.GoalProgress >= held.GoalRequired ? QuestMarkerTurnIn : QuestMarkerNone;
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
                        // Register a streamed-in NPC's handle -> name so clicking it routes to the right conversation
                        // (the same map the world-burst spawns use; players are not conversation targets).
                        if (!ev.Subject.IsPlayer && !ev.Subject.IsLoot && ev.Subject.Name != null)
                            recipient.Session.SpawnedNpcNamesByHandle[ev.Subject.Handle] = ev.Subject.Name;
                        await SendWorldSpawn(recipient, ev.Subject);
                        // #241: when a quest-giver NPC enters view, set its marker from THIS viewer's quest state
                        // (! available / ? ready-to-turn-in / none active). Recomputed per entry, so it transitions as
                        // the player accepts/completes (e.g. Casilda shows ! then ? after the quest is done).
                        if (!ev.Subject.IsPlayer && !ev.Subject.IsLoot)
                            await SendQuestMarkerIfGiver(recipient, ev.Subject);
                        // #266: a patrolling NPC walks its captured route - issue the patrol MoveTask now it's in view.
                        if (ev.Subject.Patrols)
                            await SendPatrol(recipient, ev.Subject);
                        // #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose); once the player has
                        // freed her she streams in normally (and follows). Per-viewer + per-instance (the beach is Solo).
                        if (!ev.Subject.IsPlayer && ev.Subject.Name == "Casilda" && recipient.Session?.Character != null && !await IsCasildaFreed(recipient.Session.Character.Id))
                            await SendCasildaPose(recipient, ev.Subject.Handle, true);
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

        // The loot-bag definition (#249): the baked canonical "Bag of loot" (columns + embedded KdTreeTail), built FROM
        // SCRATCH by WorldObjectBuilder.BuildFromScratch - map-independent, needs NO captured loot-bag WorldObject in
        // the world (deleting static world bags no longer breaks kill-loot). Null only if the geometry asset is missing.
        private WorldObject GetLootTemplate()
        {
            return LootBagTemplate.IsAvailable
                ? LootBagTemplate.Create()
                : null;
        }

        // Whether the character has freed Casilda - data-driven (no hard-coded quest ids): any COMPLETED quest whose
        // first goal is a TalkToNpc goal targeting Casilda (the freeing stage of the intro chain, #261).
        private async Task<bool> IsCasildaFreed(uint characterId)
        {
            foreach (var quest in await _database.GetCharacterQuests(characterId))
            {
                if (quest.State != QuestState.Completed)
                    continue;

                var def = await _database.GetQuestDefinition(quest.QuestId);
                var goal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
                if (goal?.GoalType == QuestGoalType.TalkToNpc && string.Equals(goal.TargetName, "Casilda", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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

        private uint NextLootHandle()
        {
            return (uint)Interlocked.Increment(ref _nextLootHandle);
        }

        // Resolves the rolled item templates to the ones the loot window can DISPLAY (in the item table AND
        // Hash != 0 - a 0-hash template has no inventory entry the client can render). Filtering here, before
        // the bag is built, keeps the bag's LootItems and the spliced display list in ONE shared order so the single-drag
        // take index matches the client's display Pos (#226). Preserves the rolled order; drops unknown/non-buildable.
        private async Task<List<(uint TemplateId, Item Item)>> ResolveBuildableLoot(IReadOnlyList<uint> itemTemplateIds)
        {
            var resolved = new List<(uint, Item)>();
            foreach (var templateId in itemTemplateIds ?? [])
            {
                var item = await _database.GetItem(templateId);
                // Keep any resolvable, NAMED item - the window can display it and (since LootItems + the splice both come
                // from this one list) the display order stays consistent (#226). The old `Hash == 0` skip also
                // dropped legitimate quest items (e.g. Scavenger's Key, hash 0) inserted as quest-goal keys (#261);
                // random loot is already hash-filtered at the source (GetRandomBuildableItems), so this only let the
                // quest key through.
                if (item == null || string.IsNullOrEmpty(item.Name))
                    continue;

                resolved.Add((templateId, item));
            }

            return resolved;
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

        // #267/#266: Casilda's captive (chained) vs freed pose, as a BehaviorControlCenterIIR. Both decoded byte-exact
        // from the Hoxxling beach gameplay capture's BCC (0x642CD3D6) records targeting her handle 0x00017ED0:
        //  - CAPTIVE = the prisoner struggle the destiny scene loops (slot 5, behaviorId 159428, field5 24, no tail;
        //    retail alternates 159428/159427 each phase but a single registration loops it).
        //  - FREED = her slot-9 locomotion behavior 42010 (field5 24) WITH the 20-byte bound tail (boundA 0x12), the
        //    record retail sends at the rescue (capture idx 2106) so she stops struggling and walks when following.
        private Task SendCasildaPose(INetworkClient client, uint casildaHandle, bool captive)
        {
            // BuildBehaviorControlCenterIir returns the record BODY ([type][handle][form][slot][protobuf]) WITHOUT the
            // 0x642CD3D6 sub-opcode (the create-burst Iir helper prepends it), so prepend it here - SendWorldIir only adds
            // the carrier + length, not the sub-op, and the client drops a BCC record that doesn't start with it.
            var record = captive
                ? CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(casildaHandle, 5, 159428, 1, 24, 0)
                : CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(casildaHandle, 9, 42010, 7, 24, 0, boundA: 0x12, boundB: 0);
            var body = new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR).WriteArray(record).ToArray();
            _logger.LogInformation("#267: sent Casilda {Pose} pose (handle 0x{Handle:X8}) to {Player}.",
                captive
                    ? "CAPTIVE"
                    : "freed",
                casildaHandle,
                client.Session?.Character?.Name);
            return SendWorldIir(client, body);
        }

        // Sends a prebuilt LootBagFullUpdateIIR body (handle + transform already baked by WorldObjectBuilder) on
        // the world conversation. Unlike SendWorldSpawn this does NOT re-patch the position - the body is already
        // placed at the corpse's spot.
        private Task SendLootBagUpdate(INetworkClient recipient, byte[] body)
        {
            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(body).SendAsync(recipient);
        }

        // #266 patrol: issue the NPC's captured patrol route as a MoveTask so the CLIENT walks it (retail drives idle
        // patrol client-side - the server sends the route once per spawn/respawn, not a per-tick step). The anchor /
        // current position is the NPC's live spawn position (the render-gate requires the MoveTask position to match
        // the spawn it was just sent); the waypoints are the absolute captured world coords. BuildPatrol emits a
        // self-contained record (it prepends its own [u32 len]), so it rides SendWorldRecord, not SendWorldIir.
        private Task SendPatrol(INetworkClient recipient, WorldEntity npc)
        {
            if (!npc.Patrols)
                return Task.CompletedTask;

            // Replay the captured MoveTask record (already re-addressed to this NPC's handle + spawn position). Encode
            // emits a bare 0x2000 body (no length prefix), so it rides SendWorldIir.
            return SendWorldIir(recipient, npc.PatrolBody);
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

            await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npc.Name));
        }

        // Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (WorldDespawn). Stamped with the
        // recipient's INSTANCE playfield token (#268; ZoneForEntity(recipient).PlayfieldToken at the call site): the
        // burst's value for a replayed playfield (what the client learned at entry), else WorldState's minted fallback.
        // The token-is-0 guard is just defensive (ZoneFor always mints one).
        private Task SendWorldDespawn(INetworkClient recipient, uint playfieldToken, uint subjectHandle)
        {
            var token = playfieldToken;
            if (token == 0)
            {
                _logger.LogDebug("World: skip despawn {Subject:X8} for a viewer (playfield token unknown).", subjectHandle);
                return Task.CompletedTask;
            }

            return new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob)
                .WriteArrayUInt32Length(s => s.WriteArray(WorldDespawn.BuildBody(subjectHandle, token)))
                .SendAsync(recipient);
        }

        // Frames a raw 0x2000 IIR body on the world conversation to one recipient (the generic single-recipient send the
        // dedicated builders share - e.g. the NPC MoveTask chase step).
        private Task SendWorldIir(INetworkClient recipient, byte[] body)
        {
            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(recipient);
        }

        // Forwards a player's Move to `recipient` (same body - the moving entity's handle + transform - only the
        // receiver differs), so the recipient sees the subject move.
        private Task SendWorldMove(INetworkClient recipient, MoveForward move)
        {
            return new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob)
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
        private Task SendWorldRecord(INetworkClient recipient, byte[] record)
        {
            return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(record).SendAsync(recipient);
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

            // Re-stamp the playfield-instance token to THIS session's live value. The spawn body carries the token from
            // whatever capture session the row was built in (e.g. a Crocodile row captured on the beach carries
            // 0x01EA985C), but the client only registers - and then polls (0x2020) - a spawn whose playfield instance
            // matches the playfield it actually loaded at world entry (docs/mid-session-spawn-blocker.md part 3: the
            // client looks the spawn's playfield up in its loaded-playfield map; a miss is dropped before it ever polls).
            // The login burst renders only because it was replayed from the same session whose token IS the live one;
            // mid-session/interest spawns reuse arbitrary rows, so without this they carry a stale token and never render.
            SpawnPlayfieldRewriter.TryRewrite(body, _sim.World.ZoneForEntity(subject).PlayfieldToken);

            await new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(body).SendAsync(recipient);
        }

        // Spawns a standalone IsLoot bag (its OWN handle) at a position, rendered for everyone in range - the decoupled
        // loot model: the bag is a real world object (no vitals, no respawn; tick-expired via TickExpiry), independent
        // of whatever dropped it. Used by .spawnbag AND by NPC death drops (#246) so the dead NPC's handle is free to
        // respawn at its spawn point and the bag survives that respawn. Returns the bag, or null if the map has no
        // loot template.
        private async Task<WorldEntity> SpawnLootBagAt(uint mapId, uint instanceId, Vector3 position, Quaternion rotation, IReadOnlyList<uint> itemTemplates, uint cashTin, uint allowedLooterHandle, string lootSourceName = null)
        {
            var template = GetLootTemplate();
            if (template == null)
                return null;

            var handle = NextLootHandle();

            // Resolve the rolled templates to the items the loot window can actually DISPLAY (Hash != 0, present in
            // the item table) ONCE, here - so the bag's LootItems and the spliced display list share ONE ordering. A
            // non-buildable template dropped only at splice-time (the old behaviour) shifted every later display slot,
            // so a single-drag (TakeBagItem indexes LootItems by the client's display Pos) removed/granted the WRONG
            // item and the window showed one fewer than it held (#226 / handoff Problems 2+3).
            var loot = await ResolveBuildableLoot(itemTemplates);

            // Identity (unit) rotation in the wire transform: a bag has no facing, and a non-unit quaternion scales the
            // model. Built FROM SCRATCH (#249): header/transform + identity-from-columns + KdTreeTail, stamped with the
            // live playfield token directly - no captured Data, no SpawnPlayfieldRewriter pass.
            if (!WorldObjectBuilder.BuildFromScratch(template, handle, position, Quaternion.Identity, LootBagTemplate.TypeTag, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken, out var body))
                return null;

            body = WorldBroadcastBodies.WithLootContents(body, handle, cashTin, [.. loot.Select((l, i) => ((uint)i, l.Item))]); // OUR items/cash, Pos 0..n-1

            var bag = WorldEntity.ForLoot(handle, mapId, position, rotation, template.Name ?? "Loot", body, LootBagExpirySeconds);
            bag.InstanceId = instanceId; // the bag lives in the same map COPY as whatever dropped it (#268)
            bag.CashTin = cashTin;
            bag.AllowedLooterHandle = allowedLooterHandle;
            bag.LootSourceName = lootSourceName; // the corpse's NPC name, for LootFromNpc quest-goal matching (#261)
            bag.LootItems.AddRange(loot.Select(l => l.TemplateId)); // exactly the displayed set, same order as the window
            _sim.World.ZoneFor(mapId, instanceId).Add(bag);

            // Log the contents by display Pos so an in-client single-drag test can confirm the client's source-Pos maps
            // to the item the player actually clicked (TryLootItem 0x2046 is not in any capture - the Pos mapping is
            // verified live, not from a golden).
            if (loot.Count > 0)
                _logger.LogInformation("Loot: bag 0x{Handle:X8} contents by Pos: {Items}", handle, string.Join(", ", loot.Select((l, i) => $"#{i}=0x{l.TemplateId:X}({l.Item.Name})")));

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
                await SendWorldIir(viewer.Client,
                    WorldLoot.BuildNewAllowedLooterBody(handle,
                        allowedLooterHandle != 0
                            ? allowedLooterHandle
                            : viewer.Handle));
            }

            return bag;
        }

        // Maps each FA015807 NPC the burst renders to (rounded position) -> the wire handle it renders under, by
        // rebuilding the burst record and reading the spawn body's transform. EnsureZoneNpcs uses this to give the
        // model NPC at that position the same handle the client already knows, so Known-seeding lines up.
        // Quantizes a world position to a stable integer key (×100, rounded) - the same scheme RetailLoginReplay uses
        // to match burst packets to NpcSpawns rows, so the two agree on which NPC sits at a given spot.
        private static (int, int, int) PositionKey(float x, float y, float z)
        {
            return ((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), (int)MathF.Round(z * 100f));
        }

        // DispatchSetQuestMarker body: [u32 0xC350][u32 npcHandle][u8 state], on the GameCharInterface dispatch
        // (same framing as the static create-editor marker burst).
        private static Task SendQuestMarker(INetworkClient client, uint npcHandle, byte state)
        {
            return new PacketStream()
                .WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59),
                    ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance),
                    GameCharInterfaceDispatchOpcodes.DispatchSetQuestMarker)
                .WriteUInt32(0x0000C350)
                .WriteUInt32(npcHandle)
                .WriteByte(state)
                .SendAsync(client);
        }

        // The components of a player's Move, forwarded verbatim to other viewers (only the receiver differs).
        private readonly record struct MoveForward(uint Subop, uint DataObject, uint DataClientInstance, ushort MovingType, Vector3 Position, Vector3 Rotation, byte[] Tail);
    }
}
