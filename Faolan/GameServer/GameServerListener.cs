using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy.Bank;
using Faolan.GameServer.Economy.Shop;
using Faolan.GameServer.Economy.Tradepost;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Outbound;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>, IWorldOutbound, IEconomyOutbound
	{
		// The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
		// GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), NOT the 0x58 of WorldFraming.Sender that
		// normal world/NPC-combat broadcasts use. The client appears to gate the player's own dead-state transition on
		// the 0x59 endpoint - sending the full sequence from 0x58 left the avatar alive with the release UI shut (#214).
		private static readonly byte[] PlayerDeathSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59);

		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). The id is added
		// when the name is committed and removed (consumed) on the world Hello, which is how that connection is
		// recognised as the create-world entry (and given the intro/cutscene) rather than a normal login. Static
		// because the editor and world connections are different sessions but hit the same listener instance.
		// ConcurrentDictionary-as-set: these statics are mutated from different connection threads (add on one
		// connection's handler, remove on the reconnect's), and other clients can teleport/log in concurrently, so a
		// plain HashSet would race and corrupt. The byte value is unused.
		private static readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

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
		private readonly SpellweaveService _spellweave;

		// NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
		// collaborator; uses the broadcaster for quest pushes + the Lua VM for branch conditions. See Dialogue/ConversationService.cs.
		private readonly ConversationService _conversation;

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

		// The RDB Lua VM (conversation/quest scripting); stateless + reusable.
		private readonly LuaScriptVm _luaVm = new LuaScriptVm();

		// Player-state persistence (autosave loop + logout/teleport/.save flushes), composed off the listener (#200).
		private readonly PlayerPersistenceService _persistence;

		// The multi-stage quest-chain supersession engine (#261), shared by the kill/loot/talk triggers.
		private readonly QuestProgressionService _questProgression;

		// NPC vendor / shop sell-buy-undo flow (owns the open-shop/last-sale state). The wallet-update + item-grant
		// outbound ops are injected as seams (shared with other subsystems). See Economy/Shop/ShopService.cs.
		private readonly ShopService _shop;

		// Auction-house buy->mail + mailbox ops + sell/list flow. Wallet/item-grant injected as seams. See Economy/Tradepost/TradepostService.cs.
		private readonly TradepostService _tradepost;

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
			logger,
			configuration,
			database,
			new ConanOutgoingPacketLogger(logger, "GameServer", typeof(GameCharInterfaceDispatchOpcodes), typeof(GameServer0X2000RespondsOpcodes)))
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			_simulation = new WorldSimulation(this, database, agentServerDispatcher, logger);
			_persistence = new PlayerPersistenceService(database, logger, _simulation);
			_broadcaster = new WorldBroadcaster(_simulation, database, logger);
			_questProgression = new QuestProgressionService(database, _broadcaster, agentServerDispatcher, logger);
			_combat = new CombatService(_simulation, database, _questProgression, logger, (c, a) => SendAbilityGrant(c, a), (c, id) => SendLearnSpell(c, id)); // announced level-up ability + spell unlocks (#209/#73/#74)
			_spellweave = new SpellweaveService(logger,
				(c, msg) => agentServerDispatcher.SendSystemMessage(c.Session.Account.ClientInstance, msg),
				(c, to, from, spellId) => ApplySpellweaveBuff(c, to, from, spellId), // trance + component buffs (correct 54-byte blob)
				// reactive element list + fire burst (docs/combos.md); SendWorldIir wants a [u32 len]-prefixed body,
				// the codec returns the bare record (PetIirCodec convention), so prefix it here.
				(c, record) => SendWorldIir(c, new FaolanStream().WriteUInt32((uint)record.Length).WriteArray(record).ToArray()));
			_conversation = new ConversationService(database, logger, _broadcaster, _questProgression, _luaVm);
			_loot = new LootService(_broadcaster, this, database, _questProgression, logger);
			_equipment = new EquipmentService(database, logger);
			_itemUse = new ItemUseService(database, RecallToBindCommand, logger);
			_shop = new ShopService(database, logger, this);
			_bank = new BankService(database, logger, this);
			_tradepost = new TradepostService(database, logger, this);

			InitGmCommands();
		}

		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count)
		{
			return GrantItemToBackpack(client, itemTemplateId, count);
		}

		Task IEconomyOutbound.SendAddItem(INetworkClient client, uint containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback)
		{
			return SendAddItem(client, containerType, itemPos, entry, feedback);
		}

		// --- IEconomyOutbound: the wallet + item-grant ops the economy/inventory services reach back to (forwarding
		// to the private Outbound builders, which the listener also calls directly from loot/GM paths) ---------------
		Task IEconomyOutbound.SendMoneyUpdate(INetworkClient client, ulong balanceTin, MoneyUpdateReason reason)
		{
			return SendMoneyUpdate(client, balanceTin, reason);
		}

		Task IWorldOutbound.BroadcastEntityFade(WorldEntity entity)
		{
			return _broadcaster.BroadcastEntityFade(entity);
		}

		Task IWorldOutbound.BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			return _broadcaster.BroadcastNpcAggro(npc, target);
		}

		Task IWorldOutbound.BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			return _broadcaster.BroadcastNpcCombatMode(npc, inCombat);
		}

		Task IWorldOutbound.BroadcastNpcMove(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcMove(npc);
		}

		Task IWorldOutbound.BroadcastNpcPatrol(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcPatrol(npc);
		}

		// --- IWorldOutbound: the wire side of the simulation tick (broadcaster builds packets; listener owns vitals
		// send + the world-entry replay) -----------------------------------------------------------------------------
		Task IWorldOutbound.BroadcastNpcRespawn(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcRespawn(npc);
		}

		Task IWorldOutbound.BroadcastNpcStop(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcStop(npc);
		}

		Task IWorldOutbound.ConvertCorpseToLoot(WorldEntity npc)
		{
			return _broadcaster.ConvertCorpseToLoot(npc);
		}

		Task IWorldOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}

		Task IWorldOutbound.ReenterWorld(INetworkClient client)
		{
			return SendWorldEntry(client);
		}

		// The buff-removal end-twin (#274/#253): the same ApplySpell carrier as the apply, rank byte -> 1, empty
		// payload. Routes the private SendBuffRemoval used by .cancelbuff so the world-tick expiry and the GM cancel
		// share one builder.
		Task IWorldOutbound.SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			return SendBuffRemoval(client, applyTo, applyFrom, appliedSpellId);
		}

		Task IWorldOutbound.SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return _combat.SendDamageNumber(client, targetHandle, attackerHandle, damage);
		}

		Task IWorldOutbound.SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathBcc(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathEffect(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathEffect(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathPose(dyingHandle, deathPosition)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildKilledBy(dyingHandle, killerEndpoint)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter)
		{
			return _combat.SendNpcAttackAnimation(client, npcHandle, damage, swingCounter);
		}

		Task IWorldOutbound.SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return _combat.SendNpcSpellCast(client, npcHandle, spell);
		}

		Task IWorldOutbound.SendResurrectionScreen(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return SendValidResurrectionSpots(client, deathPosition, spots);
		}

		Task IWorldOutbound.SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			// Same GameAgent 0x59 sender / receiver framing as the death sequence (retail beach #1382 uses it).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildRevive(handle, respawnPointId)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, float facingX, float facingZ)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveDestination(handle, mapId, handle, position, facingX, facingZ)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveTeleport(handle, mapId, handle, playfieldToken)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return _combat.SendStatDamage(client, targetHandle, statValue, delta, type, casterHandle, spellBaseId);
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

		protected override void ClientDisconnected(INetworkClient client)
		{
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity != null)
			{
				// Tell everyone who could see this player that it left (n3ToClientQuit) BEFORE removing it from the
				// zone, so ResolveRemove still sees the leaver's viewers. Fire-and-forget: the connection is going away.
				_ = _broadcaster.BroadcastPlayerDespawn(entity);
				_simulation.RemovePlayer(client, out _);
				// Persist the player's final live state - movement isn't otherwise saved, so without this a logout
				// loses where they were. Fire-and-forget: the connection is going away so we can't await; logged.
				// EXCEPT on a teleport-out: DoTeleport already saved the destination, and this connection's entity
				// still holds the stale pre-teleport position which would clobber it (#28).
				if (!client.Session.IsTeleportingOut)
					_ = _persistence.SavePlayerState(client, entity);
				else
					Logger.LogInformation("Teleport-out: skipping stale-position save for {Name} (0x{Handle:X8}); destination already persisted.", entity.Name, entity.Handle);
				client.Session.IsTeleportingOut = false; // one-shot: only suppress this teleport's disconnect save
				Logger.LogInformation("World: player {Name} (0x{Handle:X8}) left map {Map}.", entity.Name, entity.Handle, entity.MapId);
			}

			base.ClientDisconnected(client);
		}

		protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			var opcode = (GameServerOpcodes)packet.Opcode;

			// Tradepost (auction house) + mail ride the GameServer RPC layer; their 0x20xx methods COLLIDE with
			// ordinary opcodes, so route them by RPC class id before the opcode switch (docs/tradepost-protocol.md).
			if (TradepostProtocol.IsInbound(packet))
			{
				await TradepostHandler.Handle(_tradepost, client, packet, Logger, Database);
				return;
			}

			// GameCharAgentProject (0x5B9756FC, the "post-ready family") rides this same RPC layer and its method
			// ids COLLIDE with ordinary 0x2000 opcodes, so route it by class id BEFORE the opcode switch (like
			// Tradepost). The in-world RequestAggroStatus poll lives here and GATES the loading screen finishing:
			// without the reply the client retries it forever and never opens the chat (CommunityServer) connection.
			// Gated to existing-character world login - the create-editor / create-world flows have their own
			// elaborate handshake on this class and must not be diverted. See HandleGameCharAgentProject.
			if (packet.Receiver.Type == ConanEndpoints.GameCharAgentProjectType && !client.Session.IsCreateEditorConnection && !client.Session.IsCreateWorldSession && client.Session.Character?.Name != null)
			{
				await HandleGameCharAgentProject(client, packet);
				return;
			}

			if (opcode is not (GameServerOpcodes.Ping or GameServerOpcodes.LatencyCheck))
				LogPacketReceived(packet, opcode);

			// Class-first routing for the sub-0x2014 opcodes that COLLIDE between GameAgent (the connection class:
			// auth/chat/ping/PassBlob) and GameCharAgent (per-character). These two are the only opcodes that arrive
			// on BOTH classes (confirmed across every capture), so resolve them by receiver class HERE - the same
			// pattern as the Tradepost / GameCharAgentProject routes above - and the opcode switch below then only
			// ever sees the GameAgent form:
			//   0x2002  GameAgent.ChatVicinity          vs  GameCharAgent.RequestBindLocation
			//   0x2003  GameAgent.Authenticate2 (Hello) vs  GameCharAgent.SetTutorialStats
			// (Everything else in the switch is either GameAgent-only - 0x2000/0x2005 - or unambiguously GameCharAgent
			// at opcode >= 0x2014; 0x200B/0x200C are GameCharAgentProject-only and routed out above.)
			if (packet.Receiver.Type == ConanEndpoints.GameCharAgentType)
			{
				switch (opcode)
				{
					case GameServerOpcodes.ChatCommand: // 0x2002 = GameCharAgent.RequestBindLocation
						await HandleRequestBindLocation(client, packet);
						return;

					case GameServerOpcodes.Hello: // 0x2003 = GameCharAgent.SetTutorialStats (Tortage tutorial tracking)
					{
						var tutorialStat = packet.Body.Length - packet.Body.Position >= sizeof(uint)
							? packet.Body.ReadUInt32()
							: 0;
						Logger.LogDebug("GameCharAgent.SetTutorialStats(0x{Value:X8}); no reply.", tutorialStat);
						return;
					}
				}
			}

			switch (opcode)
			{
				case GameServerOpcodes.Hello:
				{
					// 0x2003 = GameAgent.Authenticate2 (the connection Hello: clientInstance + accountId + version).
					// The GameCharAgent.SetTutorialStats collision on this opcode is resolved by the class-first route
					// above, so here the body is always the >= 12-byte Authenticate2. Guard defensively anyway: a short
					// body would over-read and drop the client.
					if (packet.Body.Length - packet.Body.Position < 12)
					{
						Logger.LogWarning("GameAgent.Authenticate2 (0x2003) with a short {Bytes}-byte body; ignoring.", packet.Body.Length - packet.Body.Position);
						break;
					}

					var clientInstance = packet.Body.ReadUInt32(); // 0x0802e5d4 0x1101b4f5
					var accountId = packet.Body.ReadUInt32(); // 0x310cec57 0x2b68ea25=728295973
					var clientVersion = packet.Body.ReadString();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					var resolvedCharacter = await Database.GetCharacterForAccount(accountId, clientInstance);
					client.Session.Account.ClientInstance = clientInstance;
					client.Session.Character = resolvedCharacter;
					if (resolvedCharacter?.Id != clientInstance)
					{
						Logger.LogInformation(
							"GameServer Hello: resolved temporary client instance {ClientInstance:X8} to pending character {CharacterId:X8} for account {AccountId:X8} while preserving the retail temporary instance for create-flow traffic.",
							clientInstance,
							resolvedCharacter?.Id ?? 0,
							accountId);
					}

					Logger.LogInformation("GameServer hello: clientInstance={ClientInstance:X8}, clientVersion={ClientVersion}", clientInstance, clientVersion);

					/*if (clientVersion != "v4.00.NoTS@369764")
					{
					    Logger.Info("Incompatible client connects to the Server");
					    //break;
					}*/

					// Character creation has two phases on this GameServer connection:
					//  1) CREATOR: the client connects on the temp instance 0x021662C8 and we send the
					//     verbatim editor burst (RetailCreateEditorInitialBurst) so the creator UI appears.
					//     Auth replies use the editor conversation family so the client associates the burst.
					//  2) ENTER-WORLD: after the player customises + names, the world entry is sent (handled
					//     out of the post-0x205F / ReadyForPlayScreen path), not here.
					// Existing-character login uses the legacy conversation family + the world entry.
					var isNewCharacter = client.Session.Character.Name == null;

					// Post-creation WORLD connection: the client reconnects with the same (now named) character
					// instance, but this is the SECOND GameServer connection - the one opened after the 0x2002
					// enter-world handoff. It is recognised by the character id being in PendingCreateWorldEntry
					// (added when the name was committed on the editor connection). Consume it here so a later
					// normal login of the same character does not re-trigger the intro. This connection needs the
					// full retail creation world entry (the verbatim burst with its own 0x200B/0x200C/0x2001
					// auth + the intro/cutscene), so skip the helper auth for it.
					var isCreateWorldEntry = !isNewCharacter && PendingCreateWorldEntry.TryRemove(clientInstance, out _);
					client.Session.IsCreateWorldSession = isCreateWorldEntry;
					client.Session.IsCreateEditorConnection = isNewCharacter;

					if (!isCreateWorldEntry)
					{
						// New (unnamed) characters take the create-editor path; existing characters log in normally.
						var authSender = isNewCharacter
							? RetailCreatePost205FSender
							: null;
						var authReceiver = isNewCharacter
							? CreateEditorReceiver(client)
							: null;

						await ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
						await ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
						await AckAuthenticate(client, 1, authSender, authReceiver);
					}

					client.Session.LastGameServer2020RequestId = null;
					client.Session.HasPendingPost205FFollowUp = false;
					client.Session.HasPendingCreateEditorReadyFollowUp = false;
					client.Session.HasFlushedCreateWorldEntryBurst = false;
					client.Session.HasFlushedCreateEditorPostReady = false;
					client.Session.CreateEditorTempInstanceCount = 0;
					client.Session.CreateEditorObservedRequestIds.Clear();
					client.Session.CreateEditorReadyRequestIds.Clear();
					client.Session.MorphAckCounter = 0;
					client.Session.MorphActivitySequence = 0;
					client.Session.PostMorphBurstSent = false;
					client.Session.AwaitingEnterWorld = false;
					client.Session.CreateWorldChunkIndex = 0;
					// IsCreateEditorConnection / IsCreateWorldSession were set above from this connection's role.

					if (isCreateWorldEntry) // Post-creation enter-world -> full retail creation world entry (-> scene)
					{
						// Force the Tortage intro spawn onto the character (covers resumed characters that
						// never went through CreateCharacter), so the live SpawnPlayer uses the right position.
						var spawnRows = await Database.SetCreationSpawn(client.Session.Character);
						Logger.LogInformation(
							"Post-creation world entry (clientInstance={ClientInstance:X8}, character={CharacterId:X8} '{Name}'): set creation spawn (rows={Rows}, pos=({X},{Y},{Z}), map={Map}); sending the creation world SCENE burst.",
							clientInstance,
							client.Session.Character.Id,
							client.Session.Character.Name,
							spawnRows,
							client.Session.Character.PositionX,
							client.Session.Character.PositionY,
							client.Session.Character.PositionZ,
							client.Session.Character.MapId);
						// #234 reconstruct (docs/handoff-2026-06-22-create-flow-reconstruct.md): the verbatim
						// SendCreateWorldSceneBurst delivers the captured `hoxxling` world (wrong char) -> after the
						// cutscene the client drops the connection and the reconnect hangs. Send the PROVEN burst-less
						// live-char world + the destiny cutscene's Scry-script overlay instead (SendCreateWorldWithCutscene):
						// the created char lands on the beach as ITSELF and the cutscene drives to IntroDone. Cutscene
						// actors stay invisible until #185; SendCreateWorldSceneBurst stays as the golden reference.
						await SendCreateWorldWithCutscene(client);
					}
					else if (isNewCharacter) // New character -> show the creator
					{
						Logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
						await SendRetailCreateEditorInitialBurst(client, Database);
					}
					else
					{
						// Existing-character login: the DB-driven, burst-less world entry - the character's current-map
						// world content is CONSTRUCTED from the DB (WorldObject/NpcSpawn tables) and addressed to this
						// character, so the client builds the playfield and renders the NPCs. The 0x201c handshake comes
						// from us; the player's own position/name/inventory are rebuilt from the DB too. (See SendWorldEntry.)
						await SendWorldEntry(client);
						// Abilities (0x2052) are granted AFTER 0x2024 ReadyForPlayScreen (in FlushPendingPost205FFollowUp),
						// matching the retail login order (handshake -> 0x2024 -> 0x2052). Granting them eagerly here -
						// before the client polled/was ready - is a divergence from the Strie login dump.

						// Teleport/changemap re-entry: a full login arms the post-0x205F follow-up off the client's 0x205F,
						// but a direct GameServer->GameServer changemap reconnect sends no 0x205F (retail confirms). Arm it
						// here so the next 0x2020 poll flushes 0x2024 ReadyForPlayScreen (+ abilities) and the loading screen
						// dismisses; otherwise the burst lands but the client loads forever (#28 / totem recall).
						if (PendingTeleportReentry.TryRemove(clientInstance, out _))
						{
							client.Session.HasPendingPost205FFollowUp = true;
							Logger.LogInformation(
								"Teleport re-entry (clientInstance={ClientInstance:X8}): armed the post-0x205F follow-up so 0x2024 ReadyForPlayScreen flushes on the next 0x2020 (no client 0x205F on a changemap reconnect).",
								clientInstance);
						}
					}

					break;
				}

				case GameServerOpcodes.PassBlob:
				{
					await Handle0X2000(client, packet);

					break;
				}

				case GameServerOpcodes.SetHideInspectOption:
				{
					if (packet.Body != null)
					{
						LogPacketBodyDebug("GameServer 0x207E body", packet.Body.ToArray());
						if (packet.Body.Length > 0)
							Logger.LogInformation("0x207E decoded: value0={Value0:X2}", packet.Body.ToArray()[0]);
					}

					break;
				}

				case GameServerOpcodes.CinematicTransition:
				{
					var unk0 = packet.Body.ReadUInt32(); // 0xc350
					var characterId = packet.Body.ReadUInt32();

					if (IsRetailCreateEditorFlow(client))
					{
						if (client.Session.HasFlushedCreateWorldEntryBurst)
						{
							Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Retail create-editor post-0x205F burst was already flushed; ignoring later 0x205F.", unk0, characterId);
							break;
						}

						client.Session.HasPendingPost205FFollowUp = true;
						client.Session.CreateEditorTempInstanceCount = 0;
						Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Armed the retail create-editor post-0x205F follow-up.", unk0, characterId);
						break;
					}

					// The new-character create flow only performs the one-shot world-entry download
					// (AccessWindow + SetDayTime + 0xF98E10B3 + SpawnCheck burst) on the FIRST 0x205F. In the
					// retail create capture the client sends a second 0x205F after ReadyForPlayScreen, but
					// retail sends NO download in response to it - it just continues receiving the client's
					// normal in-world 0x02165EFF stream. Re-arming the handoff here re-replayed the 8KB
					// world-entry burst mid-world, which broke the client. So only arm the create handoff once.
					if (client.Session.HasFlushedCreateWorldEntryBurst)
					{
						Logger.LogInformation(
							"0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Create world-entry burst was already flushed; not re-arming the post-0x205F handoff (retail sends no download on the second 0x205F).",
							unk0,
							characterId);
						break;
					}

					client.Session.HasPendingPost205FFollowUp = true;
					Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}, expectedCharacterId={ExpectedCharacterId:X8}. Marked post-0x205F follow-up as pending.", unk0, characterId, characterId);

					break;
				}

				case GameServerOpcodes.UpdateQuestMarkerNPC:
				{
					if (packet.Body?.Length >= sizeof(uint) * 4)
					{
						var ack0 = packet.Body.ReadUInt32();
						var requestId = packet.Body.ReadUInt32();
						var ack1 = packet.Body.ReadUInt32();
						var characterId = packet.Body.ReadUInt32();
						client.Session.LastGameServer2020RequestId = requestId;

						// #245: the poll's requestId is a handle the client is CONFIRMING it built - record it so interest
						// only ever despawns confirmed handles (a despawn for an unbuilt handle crashes a still-loading
						// client: pcDynel == null). The self-handle poll is harmless (the player never despawns itself).
						_simulation.PlayerWorldEntity(client)?.Confirmed.Add(requestId);

						// 0x2020 is the client polling a spawned entity (requestId = the entity handle). Retail
						// answers each with a 0x2008 SpawnCheck echoing that handle (verified: dikketest world conn
						// polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). This is what completes/renders
						// each entity. Both existing-character login (burst-less world entry) AND post-creation world
						// entry need it: the create-world flow's old hand-built batch answered the wrong, hardcoded
						// capture handles (0x818xx) on Strie's instance, so its entities (incl. Kalanthes) were never
						// confirmed and the intro cinematic ("invalid camera path Kalanthes") failed.
						if (client.Session.WorldEntered || client.Session.IsCreateWorldSession)
							await SendSpawnCheckResponse(client, requestId);

						Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
						Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}",
							ack0,
							requestId,
							ack1,
							characterId,
							client.Session.HasPendingPost205FFollowUp);
						await FlushPendingPost205FFollowUp(client);
						packet.Body.Position = 0;
					}
					else
						LogPacketBodyDebug("GameServer 0x2020 short body", packet.Body?.ToArray());

					break;
				}

				case GameServerOpcodes.DoSkillSellback:
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x205C body", packet.Body.ToArray());

					break;
				}

				case GameServerOpcodes.PlayStockphraseAsSubtitle:
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x205E body", packet.Body.ToArray());

					break;
				}

				case GameServerOpcodes.RequestShortcutBar: // client asks for its saved hotbar at login; reply with the restored bar (#71)
				{
					if (client.Session.Character != null)
					{
						var shortcuts = await Database.GetCharacterShortcuts(client.Session.Character.Id);
						// Send from the GameCharAgent world conversation (LoginWorld201CSender 0x58 / the client
						// instance), exactly like the ability/spell/item grants - NOT echoed from the request bytes.
						// The 0x206D request's receiver carries no instance, so echoing it produced a sender with an
						// empty instance and the client silently dropped the whole reply (#71: bar stayed empty in
						// every slot in-client, though the body was byte-exact). Retail sends 0x2076 from this same
						// GameCharAgent endpoint as the 0x2052 grants (confirmed: login capture pkt 182 vs 237).
						var ps = new PacketStream();
						ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchShortcutBarReply);
						ps.WriteArray(ShortcutBarBuilder.BuildBody(shortcuts));
						await ps.SendAsync(client);
						Logger.LogInformation("ShortcutBarReply: restored {Count} hotbar shortcut(s) (#71).", shortcuts.Length);
					}

					break;
				}

				case GameServerOpcodes.SellToShop:
				{
					await _shop.HandleSellItemToShop(client, packet);

					break;
				}

				case GameServerOpcodes.UndoLastSoldItem:
				{
					await _shop.HandleUndoLastSoldItem(client);

					break;
				}

				case GameServerOpcodes.BuyFromShop:
				{
					packet.Body.ReadUInt32(); // shop ID32 type (0xC75B)
					var shopId = packet.Body.ReadUInt32();
					var pos = packet.Body.ReadUInt32();

					// Resolve the clicked slot against the open shop, debit the wallet and grant the item live
					// (DispatchAddItem). The item-grant wall that gated this is gone (protocol_containeradditem).
					await _shop.HandleBuyItemFromShop(client, shopId, pos);

					break;
				}

				case GameServerOpcodes.CloseShop:
				{
					Logger.LogInformation("Shop closed by {Character}.", client.Session.Character?.Name);

					break;
				}

				case GameServerOpcodes.TryLootAll: // the loot-all click on an open loot window; body = [typetag][bag handle]
				{
					await _loot.HandleLootAll(client, packet);

					break;
				}

				case GameServerOpcodes.TryLootItem: // single drag of one item out of the loot window
				{
					await _loot.HandleLootItem(client, packet);

					break;
				}

				case GameServerOpcodes.CloseLootBag: // client closed the loot window; body = [typetag][bag handle]
				{
					await _loot.HandleCloseLootBag(client, packet);

					break;
				}

				case GameServerOpcodes.TryLootCash: // loot the bag's coins; fires on every loot alongside TryLootAll
				{
					await _loot.HandleLootCash(client, packet);

					break;
				}

				case GameServerOpcodes.RespawnMe: // the player's release/resurrect click; body = [u32 token]
				{
					var token = packet.Body?.Length >= sizeof(uint)
						? packet.Body.ReadUInt32()
						: 0;

					// Retail respawn is click-driven (vitals restore + tombstone + teleport + re-render at the
					// resurrection point); our simulation already implements that flow on a fallback timer - the
					// click just triggers it immediately.
					if (await _simulation.TryRespawnNow(client))
						Logger.LogInformation("RespawnMe (token 0x{Token:X8}): player released; respawned.", token);
					else
						Logger.LogWarning("RespawnMe (token 0x{Token:X8}) from a player that is not dead; ignored.", token);

					break;
				}

				case GameServerOpcodes.CancelBuff: // right-click-cancel a buff; body = [u32 buffId][u32 charId]
				{
					var buffId = packet.Body.ReadUInt32();
					var charId = packet.Body.ReadUInt32();

					// The client removes its own icon (retail replies only a FormatFeedback text, no removal
					// IIR); removing the entry from the server-side registry is what makes the cancel real.
					var entity = _simulation.PlayerWorldEntity(client);
					if (entity != null && entity.ActiveBuffs.TryRemove(buffId, out _))
						Logger.LogInformation("CancelBuff: buff 0x{Buff:X8} removed from {CharId:X8}.", buffId, charId);
					else
						Logger.LogDebug("CancelBuff: buff 0x{Buff:X8} was not tracked for {CharId:X8} (cast before the registry, or an aura).", buffId, charId);

					break;
				}

				case GameServerOpcodes.LatencyCheck: // latency check (client 0x2085 -> server 0x2082)
				{
					var counter = packet.Body.ReadUInt32();

					// Retail reply body = [counter echoed][0x00000012][server tick]. The tick is a monotonically
					// increasing microsecond timer (gameplay dumps: ~5,000,000 per ~5s latency interval, ms
					// precision). Sent on the GameCharAgent conversation sub-id 0x08 with headerData 0x01.
					var tick = unchecked((uint)(Environment.TickCount64 * 1000L));

					await new PacketStream()
						.WriteHeader(LatencyResponseSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchPong, [0x01])
						.WriteUInt32(counter)
						.WriteUInt32(0x00000012)
						.WriteUInt32(tick)
						.SendAsync(client);

					break;
				}

				case GameServerOpcodes.MoveTradeItemInternal: // player-to-player trade window item move; not implemented
				{
					/*byte[] sender = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14 };
					        byte[] receiver = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08 };

					        PacketBuffer aBuffer = new PacketBuffer();
					        aBuffer.WriteHeader(sender, receiver, null, 0x2009, true);
					        aBuffer.WriteUInt32(0x00000067);
					        aBuffer.WriteUInt32(GameServer0X2000RespondsOpcodes.N3TeleportIIR);
					        aBuffer.WriteUInt32(0x0000c350);
					        aBuffer.WriteUInt32(client.client.Account.nClientInst);
					        aBuffer.WriteByte(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt32(0x3f800000);
					        aBuffer.WriteByte(0x62);
					        aBuffer.WriteUInt32(0x0000c79c);
					        aBuffer.WriteUInt32(0x00000faa); //map
					        aBuffer.WriteUInt32(client.client.Account.nClientInst);
					        aBuffer.WriteUInt32(0x0000000a);
					        aBuffer.WriteUInt32(0x00009c50);
					        aBuffer.WriteUInt32(0x00030bde); //instance
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt32(0x00000014);
					        aBuffer.WriteUInt32(0x00018704);
					        aBuffer.WriteUInt32(0x43a14000);
					        aBuffer.WriteUInt32(0x43160000);
					        aBuffer.WriteUInt32(0x4430399a);
					        aBuffer.WriteUInt32(0x00000032);
					        aBuffer.WriteByte(0);
					        //Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
					        aBuffer.Send(client);

					        aBuffer = new PacketBuffer();
					        aBuffer.WriteHeader(sender, receiver, null, 0x2002, true);
					        //aBuffer.WriteUInt32(htonl(inet_addr(realm->worldServerIPAddress)));
					        //aBuffer.WriteUInt16(realm->worldServerPort);
					        aBuffer.WriteUInt32(Native.Network.htonl(Native.Network.inet_addr("127.0.0.1")));
					        aBuffer.WriteUInt16(Program.GameServerPort);
					        aBuffer.Send(client);*/

					//var packetData = packet.Data?.ToArray().ToHexString();

					break;
				}

				case GameServerOpcodes.GetInstanceListForCurrentPlayfield: // When the 'Select instance' button is clicked
				{
					LogPacketBodyDebug("GameServer 0x207B body", packet.Body?.ToArray());

					//Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

					break;
				}

				case GameServerOpcodes.PerformSpecialAction:
				{
					// The spellweave activate/deactivate TOGGLE (docs/combos.md): first press opens the trance, second
					// fires the accumulated weave. Body word is the toggle param (0xfc in the capture).
					var counter = packet.Body.ReadUInt32();

					Logger.LogInformation("0x205A (PerformSpecialAction) param={Counter:X4}", counter);
					await _spellweave.ToggleAsync(client, counter);

					break;
				}

				case GameServerOpcodes.Ping: // client 0x2005 (empty) -> server Pong 0x2009
				{
					// Reply on the GameAgent conversation sub-id 0x08. Body = [float][0][0]; retail's leading
					// float is a varying server-load/fps metric (~80-83 in dumps) - we have no equivalent, so a
					// fixed plausible value (0x42B32A07 = ~89.6) is sent. The client just treats it as a readout.
					await new PacketStream()
						.WriteHeader(PingResponseSender, LoginWorldReceiver(client), GameInterfaceDispatchOpcodes.DispatchGameServerStats)
						.WriteUInt32(0x42B32A07)
						.WriteUInt32(0)
						.WriteUInt32(0)
						.SendAsync(client);

					break;
				}

				case GameServerOpcodes.StackInventoryItemIfPossible: // (the old C++ docs called this "SpawnCheck", p.160-161)
				{
					LogPacketBodyDebug("GameServer StackInventoryItemIfPossible body", packet.Body?.ToArray());

					/*var part1 = packet.Data.ReadUInt32();
					var spawnId = packet.Data.ReadUInt32();
					var unk0 = packet.Data.ReadUInt32();
					var nClientInst = packet.Data.ReadUInt32();

					new PacketStream()
					    .WriteHeader(Sender9, Receiver9, null, GameCharInterfaceDispatchOpcodes.DispatchSetQuestMarker)
					    .WriteUInt32(part1)
					    .WriteUInt32(spawnId)
					    .WriteByte(0)
					    .Send(client);*/

					break;
				}

				case GameServerOpcodes.SplitStack:
				{
					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var sourceSlot = packet.Body.ReadUInt32();
					var targetStackSize = packet.Body.ReadUInt32();
					var targetSlot = packet.Body.ReadUInt32();

					// 0x00, 0x00, 0x00, 0x68,
					// 0x11, 0x01, 0xB4, 0xF5,
					// 0x00, 0x00, 0x00, 0x09, // source slot
					// 0x00, 0x00, 0x00, 0x29, // 41 (target stack size)
					// 0x00, 0x00, 0x00, 0x13  // target Slot

					// 0x00, 0x00, 0x00, 0x68,
					// 0x11, 0x01, 0xB4, 0xF5,
					// 0x00, 0x00, 0x00, 0x03, // source slot
					// 0x00, 0x00, 0x00, 0x1C, // 28 (target stack size)
					// 0x00, 0x00, 0x00, 0x04  // target Slot

					// Persist the split so both stacks survive logout (bag container; the packet carries no kind).
					await _equipment.PersistSplitStack(client, (int)sourceSlot, (int)targetSlot, targetStackSize);

					break;
				}

				case GameServerOpcodes.ChatCommand:
				{
					// 0x2002 = GameAgent.ChatVicinity (typed chat, carries GM commands). The GameCharAgent.RequestBindLocation
					// collision on this opcode is resolved by the class-first route above (HandleRequestBindLocation).
					LogPacketBodyDebug("GameServer ChatCommand body", packet.Body?.ToArray());

					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var unk2 = packet.Body.ReadUInt32();
					var unk3 = packet.Body.ReadUInt32();
					var text = packet.Body.ReadString();

					if (text.Length > 2 && text[0] == '.' && text[1] != '.' && client.Session.Account.Type >= AccountType.GameMaster)
						await HandleGmCommand(client, text);
					else
						// "say" is proximity chat - only same-map players within range hear it (the sender always does).
						// The GameServer owns the live positions, so it picks the recipients here.
						await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text, _simulation.SayRecipients(client));

					break;
				}

				case GameServerOpcodes.VanityToggle:
				{
					LogPacketBodyDebug("GameServer VanityToggle body", packet.Body?.ToArray());

					break;
				}

				case GameServerOpcodes.RemoveShortcut: // body = [u32 slot]
				{
					var slot = packet.Body.ReadUInt32();
					if (client.Session.Character != null)
						await Database.RemoveCharacterShortcut(client.Session.Character.Id, slot);

					Logger.LogInformation("RemoveShortcut slot 0x{Slot:X4}.", slot);

					break;
				}

				case GameServerOpcodes.UpdateShortcut: // body = [u32 slot][u32 flag][u32 container]
				{
					// Partial decode (#71, necrol80 randomActions/changemap captures): the slot is in the bar-PAGE
					// range (0x4E20.., not the 0x64.. item range), `b` is always 0x68 = the backpack container kind,
					// and `a` is a 0/1 flag. Reads as a bar-page item-slot toggle referencing container 0x68. Not
					// persisted: the bar's restorable contents ride AddShortcut (persisted into CharacterShortcut);
					// the login RESTORE is now built from those rows as ShortcutBarReply 0x2076 (see RequestShortcutBar).
					// This op's exact semantics are still fuzzy, so it stays log-only.
					var slot = packet.Body.ReadUInt32();
					var flag = packet.Body.ReadUInt32();
					var container = packet.Body.ReadUInt32();

					Logger.LogInformation("UpdateShortcut bar-page slot 0x{Slot:X4} flag={Flag} container=0x{Container:X} (#71; log-only).", slot, flag, container);

					break;
				}

				case GameServerOpcodes.AddShortcut:
				{
					// 0x206E collides: in-game it's MoveItemToBar, but on the character-creator connection it's
					// the "enter world" signal. The client sends it several times; answer the first with the
					// 0x2002 world handoff, ignore the rest.
					// TODO: THIS IS FALSE?? 0x206E is just MoveItemToBar, this is also needed for new characters to put there starting spells etc on the bar
					if (client.Session.IsCreateEditorConnection)
					{
						if (client.Session.AwaitingEnterWorld)
						{
							client.Session.AwaitingEnterWorld = false;
							// #234: wash the new char ashore - retail's create flow sends a changemap-prep
							// (IntendedTeleport 0x10D27BC0 + n3Teleport 0x5A32F0D7) to the Tortage Beach spawn on the
							// EDITOR connection right before the handoff (Blablabla create capture f444/449: dest map
							// 0x0FAA, pos 322.5,150,704.9, descriptor 0x32). Without it the player stays at the drowning
							// cutscene's underwater end position. Mirrors GMCommands.DoTeleport (prep -> handoff).
							await SendChangeMapPrep(client, Map.TortageBeachId, Map.NewPlayerSpawnSpawnPoint.Position, 0x32);
							await SendWorldHandoff(client);
						}

						break;
					}

					var type = packet.Body.ReadUInt32(); // observed 4 = ability (the ids match the 0x2052 grants), 2/3 = other
					var slotId = packet.Body.ReadUInt32(); // client slot id (ranges 0x64.. and 0x4E20.. = bar pages)
					var sourceId = packet.Body.ReadUInt32(); // ability id or item ref
					var unk = packet.Body.ReadUInt32();

					// Persist the slot (upsert - re-dragging overwrites). The login RESTORE is now built from these
					// rows as ShortcutBarReply 0x2076 (#71, see RequestShortcutBar) - DECODED, no longer snapshot-gated.
					if (client.Session.Character != null)
						await Database.SetCharacterShortcut(client.Session.Character.Id, slotId, type, sourceId);

					Logger.LogInformation("AddShortcut: type {Type}, slot 0x{Slot:X4}, target 0x{Target:X8} (unk {Unk:X8}).", type, slotId, sourceId, unk);

					break;
				}

				default:
				{
					// Resolve the method name from the (receiver class, opcode) pair (the GameServer multiplexes
					// several RPC classes, so the opcode alone is ambiguous) - logs the name, not bare hex.
					LogUnknownPacket(packet, packet.Opcode, GameServerMethodNames.Resolve(packet.Receiver.Type, packet.Opcode));
					break;
				}
			}
		}

		// GameCharAgent.RequestBindLocation (0x2002 on the GameCharAgent class): the client repeatedly polls its
		// bind/home playfield (the Totem of Origin destination, #64); body = the player's ID32. Retail answers each
		// with a 4-byte 0x2002 [u32 playfieldId] (verified 1:1 across 13 captures). Routed here by receiver class so
		// it never collides with GameAgent.ChatVicinity, which shares opcode 0x2002.
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

		// The create-editor 0x2000 receiver: the player object (GameInterfaceType) addressed to this session's
		// client instance. Built as a typed ProtoEndpoint so the type+instance can't drift (the old hardcoded
		// receiver baked in 0x021662C8; see ConanEndpoints / ProtoEndpointGoldenTests).
		private static byte[] CreateEditorReceiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, client.Session.Account.ClientInstance);
		}

		// The post-creation WORLD connection: the second (world) connection opened after the 0x2002 enter-world
		// handoff, flagged in the Hello handler. Distinguishes it from the editor connection and from a normal
		// existing-character login so the intro/cutscene bursts only fire there.
		private static bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session.IsCreateWorldSession && client.Session.Character?.Name != null;
		}

		// The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
		// the character's real id now, so the unnamed-character state alone identifies the editor flow.
		private static bool IsRetailCreateEditorFlow(INetworkClient client)
		{
			return client.Session.Character?.Name == null;
		}
	}
}
