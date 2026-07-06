using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy.Bank;
using Faolan.GameServer.Economy.Shop;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Faolan.GameServer
{
    public partial class GameServerListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>, IWorldOutbound, IEconomyOutbound
    {
        // The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
        // GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), NOT the 0x58 of WorldFraming.Sender that
        // normal world/NPC-combat broadcasts use. The client appears to gate the player's own dead-state transition on
        // the 0x59 endpoint - sending the full sequence from 0x58 left the avatar alive with the release UI shut (#214).
        private static readonly byte[] PlayerDeathSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59);

        // Client instances awaiting a TELEPORT/changemap re-entry: added in DoTeleport before the 0x2002 handoff and
        // consumed on the reconnect Hello. A direct GameServer->GameServer changemap reconnect carries NO 0x205F (the
        // retail changemap capture confirms it - the flag is normally armed by the client's 0x205F at full login), so
        // we arm HasPendingPost205FFollowUp ourselves here. Without it the world burst lands but 0x2024 ReadyForPlay-
        // Screen never fires and the client sits on the loading screen forever (the totem-recall hang, Gitea #28).
        // Static for the same reason as PendingCreateWorldEntry: the old + reconnect connections are different sessions
        // on the one listener instance. ConcurrentDictionary-as-set (see PendingCreateWorldEntry) for thread safety.
        private static readonly ConcurrentDictionary<uint, byte> PendingTeleportReentry = new ConcurrentDictionary<uint, byte>();

        // Personal bank storage (kind 105) delivered on-demand. The withdraw item-grant op is an injected seam.
        // See Economy/Bank/BankService.cs.
        private readonly BankService _bank;

        // Combat domain logic: melee/spell damage, NPC kill lifecycle, XP/leveling, kill-quest tracking + the combat
        // wire packets. The opcode dispatch (Handle0x2000) and GM .hurt/.heal call into it. See Combat/CombatService.cs.
        private readonly CombatService _combat;

        // NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
        // collaborator; uses the broadcaster for quest pushes + the Lua VM for branch conditions. See Dialogue/ConversationService.cs.
        private readonly ConversationService _conversation;

        // The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted off this partial into its
        // own collaborator (CreateFlow/CreateFlowService.cs). The inbound create-flow handlers forward to it; the
        // burst-less world entry it needs is injected as a Func seam. SendWorldHandoff stays here (enter-world handoff).
        private readonly CreateFlowService _createFlow;

        // Persists equip/unequip/inventory-move/drop/split actions to the DB (no packet sends). The Inbound
        // handlers call into it. Composed collaborator, like CombatService. See Inventory/EquipmentService.cs.
        private readonly EquipmentService _equipment;

        private readonly IAgentServerDispatcher _agentServerDispatcher;
        private readonly IGameServerDispatcher _gameServerDispatcher;

        // Dispatches a player using (right-clicking) a bag item by its kind (Totem of Origin -> recall, future
        // consumables...). The RightClickItem Inbound handler delegates to it. See Inventory/ItemUseService.cs.
        private readonly ItemUseService _itemUse;

        // Loot-window flow (loot-all/single-drag/cash/close): orchestrates the broadcaster's bag state + the
        // item-grant/wallet ports. The loot Inbound cases delegate to it. See World/LootService.cs.
        private readonly LootService _loot;

        // Connection keep-alive / latency replies (LatencyCheck 0x2085 -> Pong, Ping 0x2005 -> GameServerStats).
        // See World/KeepAliveService.cs.
        private readonly KeepAliveService _keepAlive;

        // The RDB Lua VM (conversation/quest scripting); stateless + reusable.
        private readonly LuaScriptVm _luaVm = new LuaScriptVm();

        // Player-state persistence (autosave loop + logout/teleport/.save flushes), composed off the listener (#200).
        private readonly PlayerPersistenceService _persistence;

        // The multi-stage quest-chain supersession engine (#261), shared by the kill/loot/talk triggers.
        private readonly QuestProgressionService _questProgression;

        // NPC vendor / shop sell-buy-undo flow (owns the open-shop/last-sale state). The wallet-update + item-grant
        // outbound ops are injected as seams (shared with other subsystems). See Economy/Shop/ShopService.cs.
        private readonly ShopService _shop;

        // The player action bar / hotbar persistence + login restore (#71). See World/ShortcutBarService.cs.
        private readonly ShortcutBarService _shortcutBar;

        private readonly SpellweaveService _spellweave;

        // Builds + sends the world-event packets (spawns/moves/despawns, NPC respawn/loot/mount renders, loot-window
        // and quest-journal packets). Reads the world model via the simulation; the listener delegates its IWorldOutbound
        // port + the loot/quest opcode handlers to it. See World/WorldBroadcaster.cs.
        private readonly WorldBroadcaster _broadcaster;

        // The live world simulation: owns the world model, the connected players' authoritative entities, and the
        // 10 Hz tick (NPC respawn/AI, vitals regen, player death/respawn, resurrection discovery). The listener is its
        // IWorldOutbound port. Built in the ctor (after the dispatchers are assigned). See World/WorldSimulation.cs.
        private readonly WorldSimulation _simulation;

        // Background loops (persistence + simulation each own their own loop) and their shared lifetime token. Held
        // (not fire-and-forget) so StopAsync can signal them and await a clean shutdown, and so any unobserved fault
        // surfaces instead of being swallowed.
        private CancellationTokenSource _loopCts;

        public GameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, ushort port, IAgentServerDispatcher agentServerDispatcher, IGameServerDispatcher gameServerDispatcher) : base(port,
            logger, configuration, database, new ConanOutgoingPacketLogger(logger, "GameServer", typeof(GameCharInterfaceDispatchOpcodes), typeof(GameServer0X2000Opcodes)))
        {
            _agentServerDispatcher = agentServerDispatcher;
            _gameServerDispatcher = gameServerDispatcher;
            _simulation = new WorldSimulation(this, database, agentServerDispatcher, logger);
            _createFlow = new CreateFlowService(logger, database, _simulation, SendBurstlessWorldEntry); // create-editor/cutscene/enter-world flow
            _persistence = new PlayerPersistenceService(database, logger, _simulation);
            _broadcaster = new WorldBroadcaster(_simulation, database, logger);
            _questProgression = new QuestProgressionService(database, _broadcaster, agentServerDispatcher, logger);
            _combat = new CombatService(_simulation, database, _questProgression, logger, (c, a) => SendAbilityGrant(c, a), SendLearnSpell); // announced level-up ability + spell unlocks (#209/#73/#74)
            _spellweave = new SpellweaveService(logger,
                (c, msg) => agentServerDispatcher.SendSystemMessage(c.Session.Account.ClientInstance, msg),
                ApplySpellweaveBuff, // trance + component buffs (correct 54-byte blob)
                // reactive element list + fire burst (docs/combos.md); SendWorldIir wants a [u32 len]-prefixed body,
                // the codec returns the bare record (PetIirCodec convention), so prefix it here.
                (c, record) => SendWorldIir(c, new FaolanStream().WriteArrayUInt32Length(record).ToArray()),
                SendBuffRemoval, // trance-buff end-twins on cancel/fire
                SendSprintBar, // DispatchUpdateSprintbar (0x200E) - the yellow energy bar
                EnsureEnergyLoop); // start the persistent energy tracking/regen loop
            _conversation = new ConversationService(database, logger, _broadcaster, _questProgression, _luaVm);
            _loot = new LootService(_broadcaster, this, database, _questProgression, logger);
            _keepAlive = new KeepAliveService();
            _shortcutBar = new ShortcutBarService(database, logger);
            _equipment = new EquipmentService(database, logger);
            _itemUse = new ItemUseService(database, RecallToBindCommand, logger);
            _shop = new ShopService(database, logger, this);
            _bank = new BankService(database, logger, this);

            InitGmCommands();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _gameServerDispatcher.Register(this);
            // Own lifetime token (linked to startup) so StopAsync can cancel the loops independently of any single
            // connection. Tasks are stored so they can be awaited on shutdown.
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _persistence.Start(_loopCts.Token); // periodic crash-safety flush of the live world state
            _simulation.Start(_loopCts.Token); // the simulation tick (regen, NPC AI, death/respawn, discovery)
            return base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _gameServerDispatcher.Unregister(this);

            // Signal the background loops and await their (clean) exit so shutdown doesn't race a mid-tick save. The
            // loops swallow their own OperationCanceledException, so WhenAll completes normally; bounded by the host's
            // shutdown token either way.
            if (_loopCts != null)
                await _loopCts.CancelAsync();
            await Task.WhenAll(_simulation.StopAsync(), _persistence.StopAsync());
            _loopCts?.Dispose();

            await base.StopAsync(cancellationToken);
        }

        protected override async Task ClientDisconnected(INetworkClient client)
        {
            var entity = _simulation.PlayerWorldEntity(client);
            if (entity != null)
            {
                // Tell everyone who could see this player that it left (n3ToClientQuit) BEFORE removing it from the
                // zone, so ResolveRemove still sees the leaver's viewers - now awaited (ClientDisconnected is a Task),
                // so the despawn deterministically lands before RemovePlayer instead of racing it.
                await _broadcaster.BroadcastPlayerDespawn(entity);
                _simulation.RemovePlayer(client, out _);
                // Persist the player's final live state - movement isn't otherwise saved, so without this a logout
                // loses where they were. EXCEPT on a teleport-out: DoTeleport already saved the destination, and this
                // connection's entity still holds the stale pre-teleport position which would clobber it (#28).
                if (!client.Session.IsTeleportingOut)
                    await _persistence.SavePlayerState(client, entity);
                else
                    Logger.LogInformation("Teleport-out: skipping stale-position save for {Name} (0x{Handle:X8}); destination already persisted.", entity.Name, entity.Handle);

                client.Session.IsTeleportingOut = false; // one-shot: only suppress this teleport's disconnect save
                Logger.LogInformation("World: player {Name} (0x{Handle:X8}) left map {Map}.", entity.Name, entity.Handle, entity.MapId);
            }

            await base.ClientDisconnected(client);
        }

        protected override Task ReceivedPacket(INetworkClient client, ConanPacket packet)
        {
            // This connection multiplexes several RPC classes whose 0x20xx method ids COLLIDE with ordinary opcodes,
            // so resolve the RPC CLASS (by receiver; for Tradepost also its sender) BEFORE the opcode switch - each
            // class route consumes the packet. See docs/protocol_gameserver_rpc_multiplexing.md + tradepost-protocol.md.
            // The two sub-0x2014 opcodes 0x2002/0x2003 are the ONLY ones that also arrive on GameCharAgent (there they
            // are RequestBindLocation / SetTutorialStats); the GameAgent case owns the opcode dispatch for the rest.
            return packet.Receiver.Type switch
            {
                // Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
                ConanEndpoints.TradepostAgentType => HandleTradepostPacket(client, packet),

                // GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll GATES the loading
                // screen (without the reply the client retries forever + never opens chat). Existing-character world login
                // only - the create-editor/create-world flows own this class during their own handshake and must not divert.
                ConanEndpoints.GameCharAgentProjectType => !client.Session.IsCreateEditorConnection && !client.Session.IsCreateWorldSession && client.Session.Character?.Name != null
                    ? HandleGameCharAgentProject(client, packet)
                    : Task.CompletedTask,

                // GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) PLUS every per-character method
                // (loot / shop / respawn / shortcut / latency / ...) - the loopback confirmed these all arrive here.
                ConanEndpoints.GameCharAgentType => HandleGameCharAgentPacket(client, packet),

                // GameAgent (the connection class): auth / chat / ping / server-stats (+ VanityToggle, GCAProject-ish).
                ConanEndpoints.GameAgentType => HandleGameAgentPacket(client, packet),

                _ => throw new Exception($"Unhandled packet receiver type: {packet.Receiver.Type:X8}")
            };
        }

        // GameCharAgent.RequestBindLocation (0x2002 on the GameCharAgent class): the client repeatedly polls its
        // bind/home playfield (the Totem of Origin destination, #64); body = the player's ID32. Retail answers each
        // with a 4-byte 0x2002 [u32 playfieldId] (verified 1:1 across 13 captures). Routed here by receiver class so
        // it never collides with GameAgent.ChatVicinity, which shares opcode 0x2002.
        // #197: after each recv batch drains, fire the create-editor post-morph trio deterministically (when the
        // batch that carried Finish + the SetMorphValue burst is fully received) instead of the debounce timer.
        // Guarded to the create-editor customization phase; a no-op for every other connection/batch.
        protected override Task ReceivedBatchComplete(INetworkClient client)
        {
            if (client.Session.IsCreateEditorConnection)
                return _createFlow.FlushPostMorphBurstOnBatchDrained(client);

            return Task.CompletedTask;
        }

        private async Task HandleRequestBindLocation(INetworkClient client, ConanPacket packet)
        {
            // body = [u32 ID32 type][u32 requesterId]; tolerate a SHORT/empty poll (the client sends a bare-header
            // variant) - the reply is derived from the character, not the body, and requesterId is only for the log.
            // Reading past a short body threw EndOfStreamException, which propagated out of ReceivedPacket and CLOSED
            // the connection (the client dropped at world entry).
            uint requesterId = 0;
            if (packet.Body.Length >= 8)
            {
                packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
                requesterId = packet.Body.ReadUInt32();
            }

            // The bind playfield is the map of the character's persisted bind RespawnPoint (set by .bind / a
            // wayfarer); an unbound character (or one whose point vanished) falls back to its current map.
            var character = client.Session.Character;
            var bindMapId = character?.MapId ?? Map.TortageBeachId;
            if (character?.BindRespawnPointId is uint bindPointId)
                bindMapId = (await Database.GetRespawnPoint(bindPointId))?.MapId ?? bindMapId;

            await new PacketStream().WriteHeader(packet.ReceiverBytes, packet.SenderBytes, GameCharInterfaceDispatchOpcodes.DispatchUpdateBindLocation).WriteUInt32(bindMapId).SendAsync(client);
            Logger.LogDebug("RequestBindLocation({RequesterId:X8}) -> bind playfield {MapId}.", requesterId, bindMapId);
        }

        // Periodically flushes the live world state to the DB so a crash loses at most one interval. Persistence is
        // otherwise event-driven (disconnect, teleport, .save) and needs no game loop; this is just a cheap timer
        // (docs/archive/server-architecture-roadmap.md). The simulation tick, when it lands, can absorb this.
        // The player's live world entity for this connection (the authoritative transform + vitals), or null if not yet
        // in the world. Thin delegate to the simulation, which owns the player registry; kept so the other listener
        // partials (broadcast, combat, GM) read it unchanged.
        private WorldEntity PlayerWorldEntity(INetworkClient client)
        {
            return _simulation.PlayerWorldEntity(client);
        }

        // The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
        // handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
        private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u)
        {
            var instance = client.Session.Account.ClientInstance;
            // Source the destination playfield token from the destination map's burst so the announce matches the token
            // the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
            var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
            foreach (var body in new[]
            {
                TeleportCodec.BuildBegin(instance, destMapId),
                TeleportCodec.BuildDestination(instance, destMapId, position, destToken, destDescriptor)
            })
                await new PacketStream().WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
        }

        private async Task SendWorldHandoff(INetworkClient client)
        {
            var realm = await Database.GetRealm(client.Session.Character.RealmId);
            var address = $"{realm.IpAddress}:{realm.Port}";

            await new PacketStream().WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchRedirectToServer).WriteByte(0x00).WriteStringByteLength(address).SendAsync(client);

            Logger.LogInformation("Create-editor: sent enter-world handoff (0x2002) advertising world server {Address}.", address);
        }

        // The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
        // the character's real id now, so the unnamed-character state alone identifies the editor flow.
        private static bool IsRetailCreateEditorFlow(INetworkClient client)
        {
            return client.Session.Character?.Name == null;
        }
    }
}
