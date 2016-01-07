using System.IO;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Extentions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.GameServer
{
    public partial class GameServerListener : Server<ConanPacket>
    {
        private static AgentServerListener _agentServerListener;
        private static IDatabase _databaseStatic;

        public GameServerListener(ushort port, Logger logger, IDatabase database,
            AgentServerListener agentServerListener)
            : base(port, logger, database)
        {
            _databaseStatic = database;
            _agentServerListener = agentServerListener;

            ConanMap.Init(Database);
            Spell.Init(Database);
            InitGmCommands();
        }

        public override void ClientDisconnected(INetworkClient client)
        {
            base.ClientDisconnected(client);

            client.Character?.SaveDataToDatabase(Database);
        }

        public override void ReceivedPacket(INetworkClient client, ConanPacket packet)
        {
            Logger.Info("Received opcode: " + (GameServerOpcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");

            switch ((GameServerOpcodes) packet.Opcode)
            {
                case GameServerOpcodes.Hello:
                {
                    client.Account.ClientInstance = packet.Data.ReadUInt32(); // 0x0802e5d4
                    client.Account.Id = packet.Data.ReadUInt32(); // 0x310cec57
                    var clientVersion = packet.Data.ReadString();

                    var charId = (uint) 0; //Database.ExecuteScalar<long>(
                    //    "SELECT characterid FROM clientinstances " +
                    //    "WHERE accountid=" + client.Account.Id + " AND clientinst=" + client.Account.ClientInstance);

                    client.Character = new Character(charId);
                    client.Character.LoadDetailsFromDatabase(Database);
                    client.Account.LoadDetailsFromDatabase(Database);

                    Logger.Info("CharID: " + client.Account.ClientInstance.ToHex());
                    Logger.Info("Recieve Client Version: " + clientVersion);

                    /*if (clientVersion != "v4.00.NoTS@369764")
                    {
                        Logger.Info("Incompatible client connects to the Server");
                        //break;
                    }*/

                    Packets.Packets.ReportDimensionId(client, client.Character.RealmId); // 1
                    Packets.Packets.ReportServerId(client, client.Character.RealmId); // 0x00000006
                    Packets.Packets.AckAuthentication(client, 1);

                    Packets.Packets.Send0X201C(client); // No imediate visible change when not sending these
                    Packets.Packets.Send0X200A(client); // No imediate visible change when not sending these

                    var packetData135 = Functions.HexStreamToByteArray(
                        File.ReadAllText("../../Faolan.Files/fs135.hex").Replace("\r", "").Replace("\n", ""));

                    new PacketStream() // To big to keep here, but necessary to send...
                        .WriteHeader(Sender0, Receiver0, null, 0x2000)
                        .WriteArrayPrependLengthUInt32(new ConanStream()
                            .WriteUInt32(GameServerOx2000RespondsOpcodes.Ox737A6Df9)
                            .WriteArray(packetData135))
                        .Send(client);

                    Packets.Packets.MapChange(client);
                    Packets.Packets.SpawnPlayer(client);
                    Packets.Packets.SetTimeofDay(client);

                    //SendPlayerBuffsTest(client);
                    //SendSitOnMountTest(client);
                    //Send0x33A56FB0(client); // No imediate visible change when not sending these 
                    //SendObjectSpawnTest(client); // No imediate visible change when not sending these
                    //SendSpawnNPCAndPlayersTest(client); // Spawn some NPC's and other players
                    //Send0x4F57DC08(client); // No imediate visible change when not sending these
                    //Send0x642CD3D6(client); // No imediate visible change when not sending these
                    //Send0x96C46740(client); // No imediate visible change when not sending these

                    break;
                }

                case GameServerOpcodes.Ox2000:
                {
                    if (packet.SenderInt[1] == 0x48)
                    {
                        var sId1 = packet.Data.ReadUInt32();
                        var sId2 = packet.Data.ReadUInt32();
                        var sId3 = packet.Data.ReadUInt32();
                        var sId4 = packet.Data.ReadUInt32();

                        var aBuffer = new PacketStream();
                        aBuffer.WriteHeader(Sender3, Receiver3, null, 0x2000);
                        aBuffer.WriteUInt32(sId3);
                        aBuffer.WriteUInt32(sId4);
                        aBuffer.WriteUInt32(sId4 != 0x000027f9 ? 0x00000002 : 0x00000000);
                        aBuffer.Send(client);
                    }
                    else
                    {
                        Packets.Packets.Handle0X2000(client, packet);
                    }

                    break;
                }
                case GameServerOpcodes.Ox200C: // request change map(?)
                {
                    var packetData = packet.Data;
                    Logger.Info("GameServerOpcodes.Ox200c");

                    break;
                }
                case GameServerOpcodes.Ox205C:
                {
                    var packetData = packet.Data;
                    Logger.Info("GameServerOpcodes.Ox205c");

                    break;
                }
                case GameServerOpcodes.Ox206A: // request change map(?)
                {
                    /*byte[] sender = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14 };
                            byte[] receiver = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08 };

                            PacketBuffer aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender, receiver, null, 0x2009, true);
                            aBuffer.WriteUInt32(0x00000067);
                            aBuffer.WriteUInt32(0x5a32f0d7);
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

                    //var packetData = packet.Data.ToArray().ToHexString();
                    Logger.Info("GameServerOpcodes.Ox206A");

                    break;
                }
                case GameServerOpcodes.Ox207B:
                {
                    var packetData = packet.Data;
                    Logger.Info("GameServerOpcodes.Ox207B");
                    //Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

                    break;
                }

                case GameServerOpcodes.Ox205A:
                {
                    var packetData = packet.Data;
                    Logger.Info("GameServerOpcodes.Ox205A");
                    //

                    break;
                }

                case GameServerOpcodes.Ping: // Ping
                {
                    new PacketStream() // p.158
                        .WriteHeader(Sender10, Receiver10, null, GameServerRespondOpcodes.Pong)
                        .WriteUInt32(0x42c80000) // old = 0x42c80000, new = 0x42B32A07
                        .WriteUInt32(0)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case GameServerOpcodes.SpawnCheck: // p.160&161
                {
                    var part1 = packet.Data.ReadUInt32();
                    var spawnId = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt32();
                    var nClientInst = packet.Data.ReadUInt32();

                    new PacketStream()
                        .WriteHeader(Sender9, Receiver9, null, GameServerRespondOpcodes.SpawnCheck)
                        .WriteUInt32(part1)
                        .WriteUInt32(spawnId)
                        .WriteByte(0)
                        .Send(client);

                    break;
                }

                case GameServerOpcodes.GcPing: // GCPing?  p.162
                {
                    var counter = packet.Data.ReadUInt32();
                    var time = Functions.Time();

                    new PacketStream()
                        .WriteHeader(Sender8, Receiver8, null, GameServerRespondOpcodes.GcPing)
                        .WriteUInt32(counter)
                        .WriteUInt32(0x0000004E)
                        // counter  retvalue
                        // 1        0x2B, 0x95, 0x70, 0x58 -> p.167
                        // 2        0x2C, 0x70, 0x01, 0x78 -> p.448
                        // 3        0x2c, 0x7e, 0xc6, 0xb8 -> p.540
                        // 4        0x2c, 0x7e, 0xc6, 0xb8 -> p.541
                        // 5        0x2c, 0xa6, 0xb1, 0x78 -> p.564
                        .WriteUInt32(time) // not the correct value? But still working
                        .Send(client);

                    // is this still relevant?
                    if (client.Account.CreateState == 1 && client.Account.CreateCounter > 0)
                    {
                        client.Account.State = 0;
                        client.Account.CreateCounter = 0;

                        var data1 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x0e, 0x08, 0x05, 0x10, 0x00, 0x18, 0xec, 0x97, 0x02, 0x32, 0x04,
                            0x08, 0x00, 0x10, 0x00
                        };

                        var aBuffer = new PacketStream();
                        aBuffer.WriteHeader(Sender7, Receiver7, null, 0x2000);
                        aBuffer.WriteUInt32(0x0000001f);
                        aBuffer.WriteUInt32(0xa36d3b74);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(client.Account.ClientInstance);
                        aBuffer.WriteArray(data1);
                        aBuffer.Send(client);

                        var data2 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2a, 0xf8, 0x00, 0x00,
                            0x01, 0xf9, 0x00, 0x00, 0x43, 0x30, 0x00, 0x00, 0x01, 0xfb, 0x00, 0x00, 0x00, 0x00
                        };

                        aBuffer = new PacketStream();
                        aBuffer.WriteHeader(Sender7, Receiver7, null, 0x2000);
                        aBuffer.WriteUInt32(0x00000029);
                        aBuffer.WriteUInt32(0x96b8dc59);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(client.Account.ClientInstance);
                        aBuffer.WriteArray(data2);
                        aBuffer.Send(client);
                    }

                    break;
                }

                case GameServerOpcodes.ManualRemoveBuff:
                {
                    var packetData = packet.Data.ToArray();
                    Logger.Info("REMOVE BUFF");

                    break;
                }

                case GameServerOpcodes.ChatCommand:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    var unk1 = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();
                    var unk3 = packet.Data.ReadUInt32();
                    var text = packet.Data.ReadString();

                    if (text[0] == '.' && text[1] != '.' && client.Account.Type >= AccountType.GameMaster)
                        HandleGmCommand(client, text);
                    else
                        _agentServerListener.CharacterSay(client.Account, text);

                    break;
                }

                case GameServerOpcodes.VanityToggle:
                {
                    var packetData = packet.Data.ToArray();
                    Logger.Info("VanityToggle");

                    break;
                }

                default:
                {
                    Logger.Warning("Unknown packet: " + packet);
                    break;
                }
            }
        }
    }
}