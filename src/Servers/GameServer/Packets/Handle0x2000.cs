using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private static readonly byte[] sender0 = {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x4C};
        private static readonly byte[] receiver0 = {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xD4, 0xCB, 0x8B, 0x40};
        private static readonly byte[] sender1 = {0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x4C};
        private static readonly byte[] receiver1 = {0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40};
        private static readonly byte[] sender2 = {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
        private static readonly byte[] receiver2 = {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};
        private static readonly byte[] sender3 = {0x0d, 0xfc, 0x56, 0x97, 0x5b, 0x10, 0x14};
        private static readonly byte[] receiver3 = {0x0d, 0x48, 0x76, 0x37, 0x28, 0x10, 0x84, 0x80, 0x80, 0x08};
        private static readonly byte[] sender4 = {0x0d, 0x5d, 0xb9, 0xec, 0xa9, 0x10, 0x14};
        private static readonly byte[] receiver4 = {0x0d, 0x91, 0xf7, 0x87, 0x8b, 0x10, 0x84, 0x80, 0x80, 0x08};
        private static readonly byte[] sender5 = {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
        private static readonly byte[] receiver5 = {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};
        private static readonly byte[] sender6 = {0x0d, 0x5d, 0xb9, 0xec, 0xa9, 0x10, 0x0b};
        private static readonly byte[] receiver6 = {0x0d, 0x91, 0xf7, 0x87, 0x8b, 0x10, 0xe6, 0x8f, 0x80, 0x08};

        private void Handle0x2000(NetworkClient client, Account account, Packet packet)
        {
            if (packet.SenderInt[1] == 0x48)
            {
                var sId1 = packet.Data.ReadUInt32();
                var sId2 = packet.Data.ReadUInt32();
                var sId3 = packet.Data.ReadUInt32();
                var sId4 = packet.Data.ReadUInt32();

                var aBuffer = new PacketStream();
                aBuffer.WriteHeader(sender3, receiver3, null, 0x2000, true);
                aBuffer.WriteUInt32(sId3);
                aBuffer.WriteUInt32(sId4);
                aBuffer.WriteUInt32(sId4 != 0x000027f9 ? 0x00000002 : 0x00000000);
                aBuffer.Send(client);
            }
            else
            {
                var pbLength = packet.Data.ReadUInt32();
                var pbOpcode = (DataOpcodes) packet.Data.ReadUInt32();
                Logger.WriteLine("Received data opcode: " + pbOpcode + " (" + pbOpcode.ToHex() + ")");
                switch (pbOpcode)
                {
                    case DataOpcodes.Oxf98E10B3:
                    {
                        Handle0xf98e10b3(client, account, packet);
                        break;
                    }
                    case DataOpcodes.Ox36D3B74:
                    {
                        Handle0xa36d3b74(client, account, packet);
                        break;
                    }
                    case DataOpcodes.Move:
                    {
                        var dataobjdec = packet.Data.ReadUInt32();
                        var dataclientinst = packet.Data.ReadUInt32();
                        var movingType = (MovingTypes) packet.Data.ReadUInt16();

                        Logger.WriteLine("Moving type: " + movingType + "(" + movingType.ToHex() + ")");

                        switch (movingType)
                        {
                            case MovingTypes.Run: //MELVIN
                            case MovingTypes.Walk: //MELVIN
                            case MovingTypes.Ox011A:
                            case MovingTypes.Ox011E:
                            case MovingTypes.Ox001E:
                            case MovingTypes.Ox001A:
                            {
                                var charCoords = packet.Data.ReadVector3();
                                var datadat = packet.Data.ReadArray(pbLength - ((6*4) + (1*2)));

                                var aBuffer = new PacketStream();
                                aBuffer.WriteHeader(sender5, receiver5, null, 0x2000, true);
                                aBuffer.WriteUInt32(pbLength);
                                aBuffer.WriteUInt32(pbOpcode);
                                aBuffer.WriteUInt32(dataobjdec);
                                aBuffer.WriteUInt32(dataclientinst);
                                aBuffer.WriteUInt16(movingType);
                                aBuffer.WriteVector3(charCoords);
                                //aBuffer.WriteArray(datadat, (pbLength - ((6 * 4) + (1 * 2))));
                                aBuffer.WriteArray(datadat);

                                aBuffer.Send(client);
                                break;
                            }
                            default:
                            {
                                Logger.WriteLine("Received unknown moving type: " + movingType.ToHex());
                                break;
                            }
                        }
                        break;
                    }
                    case DataOpcodes.ReadyForPlayScreen: //peer0_160
                    {
                        var blobData1 = new byte[]
                        {
                            0x43, 0xa7, 0x40, 0x00,
                            0x43, 0x14, 0x66, 0x66,
                            0x44, 0x25, 0x4c, 0xcd,
                            0x00, 0x00, 0x07, 0xe2, //2018 = 2*1009 -> -1 -> 1xCoord
                            0x43, 0xa7, 0x40, 0x00,
                            0x43, 0x14, 0x66, 0x66,
                            0x44, 0x25, 0x4c, 0xcd,
                            0x00, 0x00, 0xc7, 0x9e,
                            0x00, 0x00, 0x0f, 0xaa,
                            0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x64,
                            0x00
                        };

                        var aBuffer = new PacketStream();
                        aBuffer.WriteHeader(sender6, receiver6, null, 0x2024, true);
                        aBuffer.WriteArray(blobData1);
                        aBuffer.Send(client);

                        var blobData2 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x0e,
                            0x06, 0xec, 0x12, 0x55,
                            0x00, 0x00, 0xc3, 0x50
                        };
                        var blobData2_1 = new byte[]
                        {
                            0x00, 0x09
                        };
                        aBuffer = new PacketStream();
                        aBuffer.WriteHeader(sender5, receiver5, null, 0x2000, true);
                        aBuffer.WriteArray(blobData2);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(blobData2_1);
                        aBuffer.Send(client);

                        var blobData3 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x0d, 0x86, 0x4c, 0xfe, 0xf8, 0x00, 0x00, 0xc3, 0x50
                        };
                        aBuffer = new PacketStream();
                        aBuffer.WriteHeader(sender5, receiver5, null, 0x2000, true);
                        aBuffer.WriteArray(blobData3);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteByte(0);
                        aBuffer.Send(client);

                        if (account.charInfo.Level == 0)
                            HandleNewCharacter(client, account, packet);

                        break;
                    }
                    case DataOpcodes.InteractWithObject:
                    {
                        var cInstPreId = packet.Data.ReadUInt32();
                        var clientInst = packet.Data.ReadUInt32();
                        var unk0 = packet.Data.ReadUInt16();
                        var unk1 = packet.Data.ReadUInt32();
                        var unklength0 = packet.Data.ReadUInt32();
                        var unkdata = packet.Data.ReadArray(unklength0);
                        var objPreId = packet.Data.ReadUInt32();
                        var objId = packet.Data.ReadUInt32();
                        var unk2 = packet.Data.ReadUInt32();

                        Logger.WriteLine("Interacting with object");

                        break;
                    }
                    case DataOpcodes.CloseGame:
                    {
                        var unk0 = packet.Data.ReadUInt32();
                        var accountID = packet.Data.ReadUInt32();

                        Logger.WriteLine("Received close game! (x button)");
                        break;
                    }
                    case DataOpcodes.SelectDeselect:
                    {
                        Logger.WriteLine("SelectDeselect!");

                        break;
                    }
                    default:
                    {
                        Logger.WriteLine("Unknown data Opcode: " + pbOpcode.ToHex());
                        break;
                    }
                }
            }
        }
    }
}