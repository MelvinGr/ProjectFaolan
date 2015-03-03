using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void Handle0X2000(NetworkClient client, Account account, Packet packet)
        {
            var pbLength = packet.Data.ReadUInt32();
            var pbOpcode = (DataOpcodes) packet.Data.ReadUInt32();
            Logger.WriteLine("Received data opcode: " + pbOpcode + " (" + pbOpcode.ToHex() + ")");

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
                        case MovingTypes.Run: //MELVIN
                        case MovingTypes.Walk: //MELVIN
                        case MovingTypes.Ox011A:
                        case MovingTypes.Ox011E:
                        case MovingTypes.Ox001E:
                        case MovingTypes.Ox001A:
                        {
                            var charCoords = packet.Data.ReadVector3();
                            var datadat = packet.Data.ReadArray(pbLength - 6*4 + 1*2);

                            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                                .WriteArrayPrependLengthUInt32(new ConanStream()
                                    .WriteUInt32(pbOpcode) // opcode
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
                    aBuffer.WriteHeader(Sender6, Receiver6, null, 0x2024);
                    aBuffer.WriteArray(blobData1);
                    aBuffer.Send(client);

                    new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                        .WriteArrayPrependLengthUInt32(new ConanStream()
                            .WriteUInt32(0x06ec1255) // opcode..(?)
                            .WriteUInt32(0x0000c350)
                            .WriteUInt32(account.ClientInstance)
                            .WriteUInt16(0x0009))
                        .Send(client);

                    new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                        .WriteArrayPrependLengthUInt32(new ConanStream()
                            .WriteUInt32(0x864cfef8) // opcode..(?)
                            .WriteUInt32(0x0000c350)
                            .WriteUInt32(account.ClientInstance)
                            .WriteByte(0))
                        .Send(client);

                    if (account.Character.Level == 0)
                        HandleNewCharacter(client, account, packet);

                    break;
                }
                case DataOpcodes.InteractWithObject:
                {
                    var cInstPreId = packet.Data.ReadUInt32();
                    var clientInst = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt16();
                    var unk1 = packet.Data.ReadUInt32();
                    var unkdata = packet.Data.ReadArrayPrependLengthUInt32();
                    var objPreId = packet.Data.ReadUInt32();
                    var objId = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();

                    Logger.WriteLine("Interacting with object");

                    break;
                }
                case DataOpcodes.CloseGame:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    var accountId = packet.Data.ReadUInt32();

                    Logger.WriteLine("Received close game! (x button)");
                    break;
                }
                case DataOpcodes.SelectDeselect:
                {
                    var data = packet.Data.ToArray();

                    Logger.WriteLine("SelectDeselect!");
                    break;
                }
                default:
                {
                    var data = packet.Data.ToArray();

                    Logger.WriteLine("Unknown data Opcode: " + pbOpcode.ToHex());
                    break;
                }
            }
        }
    }
}