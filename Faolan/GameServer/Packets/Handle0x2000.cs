using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private void Handle0X2000(INetworkClient client, ConanPacket packet)
		{
			var pbLength = packet.Data.ReadUInt32();
			var pbOpcode = packet.Data.ReadUInt32<GameServerDataOpcodes>();
			Logger.LogInformation($"Received data opcode: {pbOpcode} ({pbOpcode.ToHex()})");

			switch (pbOpcode)
			{
				case GameServerDataOpcodes.Oxf98E10B3:
				{
					Handle0Xf98E10B3(client, packet);
					break;
				}

				case GameServerDataOpcodes.Ox36D3B74:
				{
					HandleCommandPacket(client, packet);
					break;
				}

				case GameServerDataOpcodes.Move:
				{
					var dataobjdec = packet.Data.ReadUInt32();
					var dataclientinst = packet.Data.ReadUInt32();
					var movingType = packet.Data.ReadUInt16<MovingType>();

					switch (movingType)
					{
						case MovingType.MountedRun:
						case MovingType.Walk:
						case MovingType.Jump:
						case MovingType.WalkJump:
						case MovingType.MountedJump:
						case MovingType.Falling:
						case MovingType.Ox011A: // run
						case MovingType.Ox011E: // rotate?
						case MovingType.Ox001E:
						case MovingType.Ox001A:
						{
							var charCoords = packet.Data.ReadVector3();
							var datadat = packet.Data.ReadArray(pbLength - 6 * 4 + 1 * 2);

							Database.UpdateCharacterPosition(dataclientinst, charCoords);

							// send to all (nearby) clients?
							new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
								.WriteArrayUInt32Length(new ConanStream()
									.WriteUInt32(pbOpcode)
									.WriteUInt32(dataobjdec)
									.WriteUInt32(dataclientinst)
									.WriteUInt16(movingType)
									.WriteVector3(charCoords)
									.WriteArray(datadat))
								.Send(client);

							break;
						}

						default:
						{
							Logger.LogInformation($"Received unknown moving type: {movingType.ToHex()}");
							break;
						}
					}

					break;
				}

				case GameServerDataOpcodes.ReadyForPlayScreen: //peer0_160
				{
					SendReadyForPlayScreen(client);
					if (client.Character.Name == null)
						HandleNewCharacter(client, packet);

					break;
				}

				case GameServerDataOpcodes.Interact:
				{
					var cInstPreId = packet.Data.ReadUInt32();
					var clientInst = packet.Data.ReadUInt32();
					var unk0 = packet.Data.ReadUInt16();
					var actionType = packet.Data.ReadUInt32<ActionType>();
					var unkData = packet.Data.ReadArrayUInt32Length();
					var unk1 = packet.Data.ReadUInt32(); // fromid?
					var target = packet.Data.ReadUInt32();
					var unk2 = packet.Data.ReadUInt32();

					/*
					    switch weapons button:
					    unk0: 3
					    actionType: 2e (46)
					    unkdata: 08 f3 ca 04 10 01 28 0a 30 00
					    unk1: 0
					    targetSlotId: 0
					    unk2: 0

					    move helm to slot 30 (bottom right)
					    unk0: 3
					    actionType: 9
					    unkdata: 08 9a c8 02 10 01 28 0a 30 00
					    unk1: 2
					    targetSlotId: 1d (29)
					    unk2: 0

					    move cape to slot 29
					    unk0: 3
					    actionType: 9
					    unkdata: 08 9a c8 02 10 01 28 0a 30 00
					    unk1: 0b
					    targetSlotId: 1c
					    unk2: 0

					    move vanity helm to slot 30 (bottom right)
					    unk0: 3
					    actionType: 9
					    unkdata: 08 9a c8 02 10 01 28 0a 30 00
					    unk1: 03ea
					    targetSlotId: 1d (29)
					    unk2: 0

					    cast spell "cannibalism"
					    unk0: 3
					    actionType: 4
					    unkdata: 08 c0 e8 0a 10 01 28 08 30 00 52 09 08 d0 86 03 10 d4 cb 8b 40
					    unk1: 1
					    targetSlotId: 0x00142fd4
					    unk2: 0x01000000
					    */

					switch (actionType)
					{
						case ActionType.CastSpell:
						{
							Logger.LogInformation($"Player with ID: {clientInst.ToHex()} CastSpell");
							break;
						}

						case ActionType.MoveEquippedToInventory:
						{
							var fromSlot = (CharacterEquipmentSlot)unk1;
							var targetSlotId = (CharacterEquipmentSlot)target;

							Logger.LogInformation($"Player with ID: {clientInst.ToHex()} MoveEquipedToInventory ({fromSlot} -> {targetSlotId})");

							break;
						}

						case ActionType.SwitchWeaponsButton:
						{
							Logger.LogInformation($"Player with ID: {clientInst.ToHex()} SwitchWeaponsButton");
							break;
						}

						default:
						{
							Logger.LogInformation($"Player with ID: {clientInst} UNKNOWN ACTION: {actionType.ToHex()}");
							break;
						}
					}

					break;
				}

				case GameServerDataOpcodes.CloseGame:
				{
					var unk0 = packet.Data.ReadUInt32();
					var accountId = packet.Data.ReadUInt32();

					Logger.LogInformation($"Account with ID: {accountId.ToHex()} Has logged off");

					// is handled in ClientDisconnected
					//account.Character.SaveDataToDatabase(Database);
					break;
				}

				case GameServerDataOpcodes.SelectDeselect:
				{
					var length = packet.Data.Length - 4;

					var data = packet.Data.ToArray();
					Logger.LogInformation("GameServerDataOpcodes.SelectDeselect");

					/*switch(length)
					    {
					        case PassBlob::SELECT_NPC:
					            {
					                Log.Debug("Receive GA_PassBlob - SELECT_OBJECT - SELECT_NPC\n");
					                try
					                {
					                    uint32 data = packet->data->read<uint32>();
					                    uint32 ClientInst = packet->data->read<uint32>();
					                    uint8 unk0 = packet->data->read<uint8>();
					                    uint32 data2 = packet->data->read<uint32>();
					                    client->charInfo.combat.target = packet->data->read<uint32>();
					                    uint32 unk1 = packet->data->read<uint32>();
					                    uint32 unk2 = packet->data->read<uint32>();
					                    uint32 end = packet->data->read<uint32>();

					                    PacketBuffer aBuffer(500);
					                    aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, GI_PassBlob);
					                    aBuffer.write<uint32>(length);
					                    aBuffer.write<uint32>(SELECT_OBJECT);
					                    aBuffer.write<uint32>(data);
					                    aBuffer.write<uint32>(ClientInst);
					                    aBuffer.write<uint8>(unk0);
					                    aBuffer.write<uint32>(data2);
					                    aBuffer.write<uint32>(client->charInfo.combat.target);
					                    aBuffer.write<uint32>(unk1);
					                    aBuffer.write<uint32>(unk2);
					                    aBuffer.write<uint32>(end);
					                    aBuffer.doItAll(client->clientSocket);
					                    Log.Debug("Sent GI_PassBlob - SELECT_OBJECT - SELECT_NPC\n");
					                }
					                catch(char* errMsg)
					                {
					                    Log.Error("Error at receiving SelectionDatas @ SELECT_NPC - SelectObjet.cpp\nError Message:\n%s\n", errMsg);
					                }
					                break;
					            }

					        default:
					            {
					                Log.Debug("Receive GA_PassBlob - SELECT_OBJECT - Unknown Length: 0x%08x\n", length);
					                try
					                {
					                    PacketBuffer aBuffer(500);
					                    aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					                    aBuffer.write<uint32>(length);
					                    aBuffer.write<uint32>(SELECT_OBJECT);
					                    aBuffer.writeArray(packet->data->readArray(length-4), length-4);
					                    aBuffer.doItAll(client->clientSocket);
					                    Log.Debug("Sent GI_PassBlob - SELECT_OBJECT - Unknown\n");
					                }
					                catch(char* errMsg)
					                {
					                    Log.Error("Error at receiving SelectionDatas @ SELECT_NPC - SelectObjet.cpp\nError Message:\n%s\n", errMsg);
					                }
					                break;
					            }
					    }                    
					    */

					break;
				}

				case GameServerDataOpcodes.DropItem:
				{
					var data = packet.Data.ToArray();
					Logger.LogInformation("DROP ITEM!");

					break;
				}

				case GameServerDataOpcodes.MoveItemInInventory:
				{
					var data = packet.Data.ToArray();
					Logger.LogInformation("MoveItemInInventory");

					break;
				}

				case GameServerDataOpcodes.DeleteQuest:
				{
					var data = packet.Data.ToArray();
					Logger.LogInformation("DeleteQuest");

					break;
				}

				case GameServerDataOpcodes.InventoryClaimsButton:
				{
					var data = packet.Data.ToArray();
					Logger.LogInformation("InventoryClaimsButton");

					break;
				}

				default:
				{
					var data = packet.Data.ToArray();
					Logger.LogWarning($"Unknown data Opcode: {pbOpcode.ToHex()}\r\n{data.ToHex()}");

					break;
				}
			}
		}
	}
}
