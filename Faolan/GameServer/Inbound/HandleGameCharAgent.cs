using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy.Bank;
using Faolan.GameServer.Economy.Shop;
using Faolan.GameServer.Economy.Tradepost;
using Faolan.GameServer.FullCharSnapshot;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Outbound;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// GameCharAgent dispatch: the 0x2002/0x2003 collision forms (bind/tutorial) + every per-character method
		// (loot / shop / respawn / shortcut / latency / ...). Routed here by receiver class from ReceivedPacket.
		private async Task HandleGameCharAgentPacket(INetworkClient client, ConanPacket packet)
		{
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
					        aBuffer.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR);
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
		}
	}
}
