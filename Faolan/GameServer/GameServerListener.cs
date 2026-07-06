using System;
using System.Collections.Concurrent;
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
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
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
			_createFlow = new CreateFlowService(logger, database, _simulation, SendBurstlessWorldEntry); // create-editor/cutscene/enter-world flow
			_persistence = new PlayerPersistenceService(database, logger, _simulation);
			_broadcaster = new WorldBroadcaster(_simulation, database, logger);
			_questProgression = new QuestProgressionService(database, _broadcaster, agentServerDispatcher, logger);
			_combat = new CombatService(_simulation, database, _questProgression, logger, (c, a) => SendAbilityGrant(c, a), (c, id) => SendLearnSpell(c, id)); // announced level-up ability + spell unlocks (#209/#73/#74)
			_spellweave = new SpellweaveService(logger,
				(c, msg) => agentServerDispatcher.SendSystemMessage(c.Session.Account.ClientInstance, msg),
				(c, to, from, spellId) => ApplySpellweaveBuff(c, to, from, spellId), // trance + component buffs (correct 54-byte blob)
				// reactive element list + fire burst (docs/combos.md); SendWorldIir wants a [u32 len]-prefixed body,
				// the codec returns the bare record (PetIirCodec convention), so prefix it here.
				(c, record) => SendWorldIir(c, new FaolanStream().WriteUInt32(record.Length).WriteArray(record).ToArray()),
				(c, to, from, spellId) => SendBuffRemoval(c, to, from, spellId), // trance-buff end-twins on cancel/fire
				(c, energy) => SendSprintBar(c, energy), // DispatchUpdateSprintbar (0x200E) - the yellow energy bar
				c => EnsureEnergyLoop(c)); // start the persistent energy tracking/regen loop
			_conversation = new ConversationService(database, logger, _broadcaster, _questProgression, _luaVm);
			_loot = new LootService(_broadcaster, this, database, _questProgression, logger);
			_keepAlive = new KeepAliveService();
			_shortcutBar = new ShortcutBarService(database, logger);
			_equipment = new EquipmentService(database, logger);
			_itemUse = new ItemUseService(database, RecallToBindCommand, logger);
			_shop = new ShopService(database, logger, this);
			_bank = new BankService(database, logger, this);
			_tradepost = new TradepostService(database, logger, this);

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

			// This connection multiplexes several RPC classes whose 0x20xx method ids COLLIDE with ordinary opcodes,
			// so resolve the RPC CLASS (by receiver; for Tradepost also its sender) BEFORE the opcode switch - each
			// class route consumes the packet. See docs/protocol_gameserver_rpc_multiplexing.md + tradepost-protocol.md.
			// The two sub-0x2014 opcodes 0x2002/0x2003 are the ONLY ones that also arrive on GameCharAgent (there they
			// are RequestBindLocation / SetTutorialStats); the GameAgent case owns the opcode dispatch for the rest.
			switch (packet.Receiver.Type)
			{
				// Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
				case TradepostProtocol.AgentType:
					await TradepostHandler.Handle(_tradepost, client, packet, Logger, Database);
					return;

				// GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll GATES the loading
				// screen (without the reply the client retries forever + never opens chat). Existing-character world login
				// only - the create-editor/create-world flows own this class during their own handshake and must not divert.
				case ConanEndpoints.GameCharAgentProjectType
					when !client.Session.IsCreateEditorConnection && !client.Session.IsCreateWorldSession && client.Session.Character?.Name != null:
					await HandleGameCharAgentProject(client, packet);
					return;

				// GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) PLUS every per-character method
				// (loot / shop / respawn / shortcut / latency / ...) - the loopback confirmed these all arrive here.
				case ConanEndpoints.GameCharAgentType:
					var charOp = (GameCharAgentOpcodes)packet.Opcode;
					if (charOp is not GameCharAgentOpcodes.Ping)
						LogPacketReceived(packet, charOp);

					switch (charOp)
					{
						case GameCharAgentOpcodes.RequestBindLocation: // 0x2002 = RequestBindLocation
							await HandleRequestBindLocation(client, packet);
							return;

						case GameCharAgentOpcodes.SetTutorialStats: // 0x2003 = SetTutorialStats (Tortage tutorial tracking)
							var tutorialStat = packet.Body.Length - packet.Body.Position >= sizeof(uint)
								? packet.Body.ReadUInt32()
								: 0;
							Logger.LogDebug("GameCharAgent.SetTutorialStats(0x{Value:X8}); no reply.", tutorialStat);
							return;

						case GameCharAgentOpcodes.SetHideInspectOption:
						{
							if (packet.Body != null)
							{
								LogPacketBodyDebug("GameServer 0x207E body", packet.Body.ToArray());
								if (packet.Body.Length > 0)
									Logger.LogInformation("0x207E decoded: value0={Value0:X2}", packet.Body.ToArray()[0]);
							}

							break;
						}

						case GameCharAgentOpcodes.CinematicTransition:
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

						case GameCharAgentOpcodes.UpdateQuestMarkerNPC:
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

						case GameCharAgentOpcodes.DoSkillSellback:
						{
							if (packet.Body != null)
								LogPacketBodyDebug("GameServer 0x205C body", packet.Body.ToArray());

							break;
						}

						case GameCharAgentOpcodes.PlayStockphraseAsSubtitle:
						{
							// #234 create-cutscene speech: the client is asking us to resolve a stock phrase
							// ({stock_N}) and BLOCKS up to 60 s for the 0x205D reply. Answer it (Kalanthes's beach
							// lines) instead of silently timing out. See CreateFlow/StockPhraseResponder.cs.
							if (packet.Body != null)
								await _createFlow.HandlePlayStockphraseAsSubtitle(client, packet.Body.ToArray());

							break;
						}

						case GameCharAgentOpcodes.RequestShortcutBar: // client asks for its saved hotbar at login; reply with the restored bar (#71)
						{
							await _shortcutBar.SendRestoredBar(client);

							break;
						}

						case GameCharAgentOpcodes.SellItem:
						{
							await _shop.HandleSellItemToShop(client, packet);

							break;
						}

						case GameCharAgentOpcodes.UndoLastSoldItem:
						{
							await _shop.HandleUndoLastSoldItem(client);

							break;
						}

						case GameCharAgentOpcodes.BuyItem:
						{
							packet.Body.ReadUInt32(); // shop ID32 type (0xC75B)
							var shopId = packet.Body.ReadUInt32();
							var pos = packet.Body.ReadUInt32();

							// Resolve the clicked slot against the open shop, debit the wallet and grant the item live
							// (DispatchAddItem). The item-grant wall that gated this is gone (protocol_containeradditem).
							await _shop.HandleBuyItemFromShop(client, shopId, pos);

							break;
						}

						case GameCharAgentOpcodes.CloseShop:
						{
							Logger.LogInformation("Shop closed by {Character}.", client.Session.Character?.Name);

							break;
						}

						case GameCharAgentOpcodes.TryLootAll: // the loot-all click on an open loot window; body = [typetag][bag handle]
						{
							await _loot.HandleLootAll(client, packet);

							break;
						}

						case GameCharAgentOpcodes.TryLootItem: // single drag of one item out of the loot window
						{
							await _loot.HandleLootItem(client, packet);

							break;
						}

						case GameCharAgentOpcodes.CloseLootBag: // client closed the loot window; body = [typetag][bag handle]
						{
							await _loot.HandleCloseLootBag(client, packet);

							break;
						}

						case GameCharAgentOpcodes.TryLootCash: // loot the bag's coins; fires on every loot alongside TryLootAll
						{
							await _loot.HandleLootCash(client, packet);

							break;
						}

						case GameCharAgentOpcodes.RespawnMe: // the player's release/resurrect click; body = [u32 token]
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

						case GameCharAgentOpcodes.CancelBuff: // right-click-cancel a buff; body = [u32 buffId][u32 charId]
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

						case GameCharAgentOpcodes.Ping: // latency check (client 0x2085 -> server 0x2082)
						{
							await _keepAlive.HandleLatencyCheck(client, packet);

							break;
						}

						case GameCharAgentOpcodes.MoveTradeItemInternal: // player-to-player trade window item move; not implemented
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

						case GameCharAgentOpcodes.GetInstanceListForCurrentPlayfield: // When the 'Select instance' button is clicked
						{
							LogPacketBodyDebug("GameServer 0x207B body", packet.Body?.ToArray());

							//Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

							break;
						}

						case GameCharAgentOpcodes.PerformSpecialAction:
						{
							// The spellweave activate/deactivate TOGGLE (docs/combos.md): first press opens the trance, second
							// fires the accumulated weave. Body word is the toggle param (0xfc in the capture).
							var counter = packet.Body.ReadUInt32();

							Logger.LogInformation("0x205A (PerformSpecialAction) param={Counter:X4}", counter);
							await _spellweave.ToggleAsync(client, counter);

							break;
						}

						case GameCharAgentOpcodes.StackInventoryItemIfPossible: // (the old C++ docs called this "SpawnCheck", p.160-161)
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

						case GameCharAgentOpcodes.SplitInventoryItem:
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

						case GameCharAgentOpcodes.RemoveShortcut: // body = [u32 slot]
						{
							await _shortcutBar.HandleRemoveShortcut(client, packet);

							break;
						}

						case GameCharAgentOpcodes.UpdateShortcut: // body = [u32 slot][u32 flag][u32 container]
						{
							await _shortcutBar.HandleUpdateShortcut(client, packet);

							break;
						}

						case GameCharAgentOpcodes.AddShortcut:
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

							// In-game (non-editor): the player dragged an ability/item onto the bar - persist it.
							await _shortcutBar.HandleAddShortcut(client, packet);

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

					return;

				// GameAgent (the connection class): auth / chat / ping / server-stats (+ VanityToggle, GCAProject-ish).
				case ConanEndpoints.GameAgentType:
					var gaOp = (GameAgentOpcodes)packet.Opcode;
					if (gaOp is not GameAgentOpcodes.UpdateGameServerStats)
						LogPacketReceived(packet, gaOp);

					switch (gaOp)
					{
						case GameAgentOpcodes.Authenticate2:
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
							var isCreateWorldEntry = !isNewCharacter && _createFlow.TryConsumePendingCreateWorldEntry(clientInstance);
							client.Session.IsCreateWorldSession = isCreateWorldEntry;
							client.Session.IsCreateEditorConnection = isNewCharacter;

							if (!isCreateWorldEntry)
							{
								// New (unnamed) characters take the create-editor path; existing characters log in normally.
								var authSender = isNewCharacter
									? RetailCreatePost205FSender
									: null;
								var authReceiver = isNewCharacter
									? CreateEditorFraming.Receiver(client)
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
								await _createFlow.SendCreateWorldWithCutscene(client);
							}
							else if (isNewCharacter) // New character -> show the creator
							{
								Logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
								await RetailCreateEditorInitialBurst.SendRetailCreateEditorInitialBurst(client, Database);
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

						case GameAgentOpcodes.PassBlob:
						{
							await Handle0X2000(client, packet);

							break;
						}

						case GameAgentOpcodes.UpdateGameServerStats: // client 0x2005 (empty) -> server Pong 0x2009
						{
							await _keepAlive.HandlePing(client);

							break;
						}

						case GameAgentOpcodes.ChatVicinity:
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

						default:
						{
							// Resolve the method name from the (receiver class, opcode) pair (the GameServer multiplexes
							// several RPC classes, so the opcode alone is ambiguous) - logs the name, not bare hex.
							LogUnknownPacket(packet, packet.Opcode, GameServerMethodNames.Resolve(packet.Receiver.Type, packet.Opcode));
							break;
						}
					}

					return;
			}
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

		// Step 4: the enter-world handoff. The client learns the world server address ONLY from this 0x2002
		// packet (the address is carried explicitly here, not derived). The client opens a brand-new connection
		// to that address, which triggers the loading screen where the intro cinematic resources load. Retail used
		// a SEPARATE world port (creator + 1); we advertise the SAME GameServer port, so the fresh connection
		// lands back on the one GameServerListener (which serves the create-world entry path for the now-named
		// character). Body = [0x00][byteLen "host:port"].
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
