using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void Handle0X2000(NetworkClient client, Account account, ConanPacket packet)
        {
            var pbLength = packet.Data.ReadUInt32();
            var pbOpcode = (DataOpcodes) packet.Data.ReadUInt32();
            Logger.Info("Received data opcode: " + pbOpcode + " (" + pbOpcode.ToHex() + ")");

            switch (pbOpcode)
            {
                case DataOpcodes.Oxf98E10B3:
                {
                    Handle0Xf98E10B3(client, account, packet);
                    break;
                }
                case DataOpcodes.Ox36D3B74:
                {
                    HandleCommandPacket(client, account, packet);
                    break;
                }
                case DataOpcodes.Move:
                {
                    var dataobjdec = packet.Data.ReadUInt32();
                    var dataclientinst = packet.Data.ReadUInt32();
                    var movingType = (MovingTypes) packet.Data.ReadUInt16();

                    switch (movingType)
                    {
                        case MovingTypes.MountedRun:
                        case MovingTypes.Walk:
                        case MovingTypes.Jump:
                        case MovingTypes.WalkJump:
                        case MovingTypes.MountedJump:
                        case MovingTypes.Falling:
                        case MovingTypes.Ox011A: // run
                        case MovingTypes.Ox011E: // rotate?
                        case MovingTypes.Ox001E:
                        case MovingTypes.Ox001A:
                        {
                            var charCoords = packet.Data.ReadVector3();
                            var datadat = packet.Data.ReadArray(pbLength - 6*4 + 1*2);

                            var recvClient = Accounts.FirstOrDefault(a => a.ClientInstance == dataclientinst);
                            recvClient.Character.Position = charCoords;
                            //account.Character.Position = charCoords;

                            // send to all (nearby) clients?
                            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                                .WriteArrayPrependLengthUInt32(new ConanStream()
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
                            Logger.Info("Received unknown moving type: " + movingType.ToHex());
                            break;
                        }
                    }
                    break;
                }
                case DataOpcodes.ReadyForPlayScreen: //peer0_160
                {
                    SendReadyForPlayScreen(client, account);
                    if (account.Character.Level == 0)
                        HandleNewCharacter(client, account, packet);

                    break;
                }
                case DataOpcodes.Interact:
                {
                    var cInstPreId = packet.Data.ReadUInt32();
                    var clientInst = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt16();
                    var actionType = (ActionTypes) packet.Data.ReadUInt32();
                    var unkdata = packet.Data.ReadArrayPrependLengthUInt32();
                    var unk1 = packet.Data.ReadUInt32(); // fromid?
                    var targetSlotId = packet.Data.ReadUInt32();
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
                        case ActionTypes.CastSpell:
                        {
                            Logger.Info("Player with ID: " + clientInst.ToHex() + " CastSpell");
                            break;
                        }
                        case ActionTypes.MoveEquipedToInventory:
                        {
                            var fromSlot = (CharacterEquipSlotsIDs) unk1;

                            Logger.Info("Player with ID: " + clientInst.ToHex() + " MoveEquipedToInventory"
                                        + " (" + fromSlot + " -> " + targetSlotId + ")");

                            break;
                        }
                        case ActionTypes.SwitchWeaponsButton:
                        {
                            Logger.Info("Player with ID: " + clientInst.ToHex() + " SwitchWeaponsButton");
                            break;
                        }
                        default:
                        {
                            Logger.Info("Player with ID: " + clientInst + " UNKNOWN ACTION: " + actionType.ToHex());
                            break;
                        }
                    }

                    break;
                }
                case DataOpcodes.CloseGame:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    var accountId = packet.Data.ReadUInt32();

                    Logger.Info("Account with ID: " + accountId.ToHex() + " Has logged off");

                    account.Character.SaveDataToDatabase(Database);
                    break;
                }
                case DataOpcodes.SelectDeselect:
                {
                    var length = packet.Data.Length - 4;

                    var data = packet.Data.ToArray();

                    Logger.Info("DataOpcodes.SelectDeselect");

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
                case DataOpcodes.DropItem:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("DROP ITEM!");

                    break;
                }
                case DataOpcodes.MoveItemInInventory:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("MoveItemInInventory");

                    break;
                }
                case DataOpcodes.DeleteQuest:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("DeleteQuest");

                    break;
                }
                case DataOpcodes.InventoryClaimsButton:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("InventoryClaimsButton");

                    break;
                }
                default:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("Unknown data Opcode: " + pbOpcode.ToHex());
                    break;
                }
            }
        }
    }
}