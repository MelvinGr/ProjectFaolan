using System;
using LibFaolan;
using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.DllImport;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Network.Shared;

namespace GameServer
{
    public partial class GameServerListener : Server<ConanPacket>
    {
        public GameServerListener(ushort port, Logger logger, IDatabase database) : base(port, logger, database)
        {
        }

        public override void ClientConnected(NetworkClient client)
        {
            Logger.WriteLine("New client with address: " + client.IpAddress);
            client.Tag = new ConanAccount();
        }

        public override void ClientDisconnected(NetworkClient client)
        {
            Logger.WriteLine("Client with address: " + client.IpAddress + " disconnected!");
        }

        public override void ReceivedPacket(NetworkClient client, ConanPacket packet)
        {
            Logger.WriteLine("Received opcode: " + (Opcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (ConanAccount) client.Tag;

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.Hello:
                {
                    account.nClientInst = packet.Data.ReadUInt32(); // 0x0802e5d4
                    var unk0 = packet.Data.ReadUInt32(); // 0x310cec57
                    var clientVersion = packet.Data.ReadString();

                    account.Id = 1;
                    account.LoadDetailsFromDatabase(Database);
                    account.charInfo = new ConanCharacter(2);
                    account.charInfo.LoadDetailsFromDatabase(Database);

                    Logger.WriteLine("CharID: " + account.nClientInst.ToHex());
                    Logger.WriteLine("Recieve Client Version: " + clientVersion);

                    /*if (clientVersion != "v4.00.NoTS@369764")
                    {
                        Logger.WriteLine("Incompatible client connects to the Server");
                        //break;
                    }*/

                    ReportDimensionID(client, account, 1);
                    ReportServerID(client, account, 0x00000006);
                    AckAuthentication(client, account, 1);

                    //LoadWorldHandler(client, account);
                    SendEverything(client);

                    break;
                }

                case Opcodes.Ox2000:
                {
                    Ox2000(client, account, packet);

                    break;
                }

                case Opcodes.Ox206A: // request change map
                {
                    /*byte[] sender = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14 };
                    byte[] receiver = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08 };

                    PacketBuffer aBuffer = new PacketBuffer();
                    aBuffer.WriteHeader(sender, receiver, null, 0x2009, true);
                    aBuffer.WriteUInt32(0x00000067);
                    aBuffer.WriteUInt32(0x5a32f0d7);
                    aBuffer.WriteUInt32(0x0000c350);
                    aBuffer.WriteUInt32(account.nClientInst);
                    aBuffer.WriteByte(0);
                    aBuffer.WriteUInt64(0);
                    aBuffer.WriteUInt64(0);
                    aBuffer.WriteUInt64(0);
                    aBuffer.WriteUInt32(0x3f800000);
                    aBuffer.WriteByte(0x62);
                    aBuffer.WriteUInt32(0x0000c79c);
                    aBuffer.WriteUInt32(0x00000faa); //map
                    aBuffer.WriteUInt32(account.nClientInst);
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

                    var packetData = packet.Data;
                    //

                    break;
                }
                case Opcodes.Ox207B:
                {
                    var packetData = packet.Data;
                    //Logger.WriteLine("Receive opcode 0x207b. Maybe hide object(helmet,...)");

                    break;
                }

                case Opcodes.Ox205A:
                {
                    var packetData = packet.Data;
                    //

                    break;
                }

                case Opcodes.Ping: // Ping
                {
                    var sender = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x4C};
                    var receiver = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xD4, 0xCB, 0x8B, 0x40};

                    new PacketBuffer() // p.158
                        .WriteHeader(sender, receiver, null, SendOpcodes.Pong, true)
                        .WriteUInt32(0x42c80000) // old = 0x42c80000, new = 0x42B32A07
                        .WriteUInt32(0)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case Opcodes.SpawnCheck: // p.160&161
                {
                    /*
                        0x00, 0x00, 0xC3, 0x50, 
                        0x00, 0x02, 0x7A, 0x66,
                        0x00, 0x00, 0xC3, 0x50,
                        0x08, 0x02, 0xE5, 0xD4
                    */

                    var part1 = packet.Data.ReadUInt32();
                    var spawnId = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt32();
                    var unk1 = packet.Data.ReadUInt32();

                    var sender = new byte[] {0x0d, 0x5d, 0xb9, 0xec, 0xa9, 0x10, 0x18};
                    var receiver = new byte[] {0x0d, 0x91, 0xf7, 0x87, 0x8b, 0x10, 0xe6, 0x8f, 0x80, 0x08};

                    new PacketBuffer()
                        .WriteHeader(sender, receiver, null, 0x2008, true)
                        .WriteUInt32(part1)
                        .WriteUInt32(spawnId)
                        .WriteByte(0)
                        .Send(client);

                    break;
                }

                case Opcodes.GCPing: // GCPing?  p.162
                {
                    var counter = packet.Data.ReadUInt32();

                    var sender = new byte[] {0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x4C};
                    var receiver = new byte[] {0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40};

                    var time = 0;
                    Other.time(ref time);

                    new PacketBuffer()
                        .WriteHeader(sender, receiver, null, 0x0000207D, true)
                        .WriteUInt32(counter)
                        .WriteUInt32(0x0000004E)
                        // counter  retvalue
                        // 1        0x2B, 0x95, 0x70, 0x58 -> p.167
                        // 2        0x2C, 0x70, 0x01, 0x78 -> p.448
                        // 3        0x2c, 0x7e, 0xc6, 0xb8 -> p.540
                        // 4        0x2c, 0x7e, 0xc6, 0xb8 -> p.541
                        // 5        0x2c, 0xa6, 0xb1, 0x78 -> p.564
                        .WriteUInt32(time) // not the correct value..
                        .Send(client);

                    if (account.state == 1 && account.counter > 0)
                    {
                        account.state = 0;
                        account.counter = 0;

                        var sender2 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
                        var receiver2 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};
                        var data1 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x0e, 0x08, 0x05, 0x10, 0x00, 0x18, 0xec, 0x97, 0x02, 0x32, 0x04, 0x08,
                            0x00, 0x10, 0x00
                        };

                        var aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000001f);
                        aBuffer.WriteUInt32(0xa36d3b74);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data1);
                        aBuffer.Send(client);

                        var data2 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2a, 0xf8, 0x00, 0x00, 0x01,
                            0xf9, 0x00, 0x00, 0x43, 0x30, 0x00, 0x00, 0x01, 0xfb, 0x00, 0x00, 0x00, 0x00
                        };

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000029);
                        aBuffer.WriteUInt32(0x96b8dc59);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data2);
                        aBuffer.Send(client);
                    }

                    break;
                }

                default:
                {
                    Logger.WriteLine("Unknown packet: " + packet);
                    break;
                }
            }
        }
    }
}