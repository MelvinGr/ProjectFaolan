using System.IO;
using AgentServer;
using LibFaolan.Data;
using LibFaolan.Data.Enums;
using LibFaolan.Database;
using LibFaolan.DllImport;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace GameServer
{
    public partial class GameServerListener : Server<ConanPacket, ProtocolFactory<ConanWireProtocol>>
    {
        private static AgentServerListener _agentServerListener;
        private new static IDatabase Database;
        private new static Logger Logger;

        public GameServerListener(ushort port, Logger logger, IDatabase database,
            AgentServerListener agentServerListener)
            : base(port, logger, database)
        {
            Database = database;
            Logger = logger;
            _agentServerListener = agentServerListener;

            ConanMap.Init(Database);
            Spell.Init(Database);
            InitGmCommands();
        }

        public override void ClientConnected(NetworkClient client)
        {
            client.Tag = new Account();
        }

        public override void ClientDisconnected(NetworkClient client)
        {
            base.ClientDisconnected(client);

            ((Account) client.Tag).Character?.SaveDataToDatabase(Database);
        }

        public override void ReceivedPacket(NetworkClient client, ConanPacket packet)
        {
            Logger.Info("Received opcode: " + (Opcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (Account) client.Tag;

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.Hello:
                {
                    account.ClientInstance = packet.Data.ReadUInt32(); // 0x0802e5d4
                    account.Id = packet.Data.ReadUInt32(); // 0x310cec57
                    var clientVersion = packet.Data.ReadString();

                    var charId = (uint) Database.ExecuteScalar<long>(
                        "SELECT characterid FROM clientinstances " +
                        "WHERE accountid=" + account.Id + " AND clientinst=" + account.ClientInstance);

                    account.Character = new Character(charId);
                    account.Character.LoadDetailsFromDatabase(Database);
                    account.LoadDetailsFromDatabase(Database);

                    Logger.Info("CharID: " + account.ClientInstance.ToHex());
                    Logger.Info("Recieve Client Version: " + clientVersion);

                    /*if (clientVersion != "v4.00.NoTS@369764")
                    {
                        Logger.Info("Incompatible client connects to the Server");
                        //break;
                    }*/

                    ReportDimensionId(client, account, account.Character.RealmId); // 1
                    ReportServerId(client, account, account.Character.RealmId); // 0x00000006
                    AckAuthentication(client, account, 1);

                    Send0X201C(client, account); // No imediate visible change when not sending these
                    Send0X200A(client, account);

                    var packetData135 = Functions.HexStreamToByteArray(File.ReadAllText("../../other/fs135.hex")
                        .Replace("\r", "").Replace("\n", ""));
                    new PacketStream() // To big to keep here, but necessary to send...
                        .WriteHeader(Sender0, Receiver0, null, 0x2000)
                        .WriteArrayPrependLengthUInt32(new ConanStream()
                            .WriteUInt32(Ox2000RespondsOpcodes.Ox737A6DF9)
                            .WriteArray(packetData135))
                        .Send(client);

                    MapChange(client, account);
                    SpawnPlayer(client, account);
                    SetTimeofDay(client);

                    //SendPlayerBuffsTest(client, account);
                    //SendSitOnMountTest(client, account);
                    //Send0x33A56FB0(client); // No imediate visible change when not sending these 
                    //SendObjectSpawnTest(client); // No imediate visible change when not sending these
                    //SendSpawnNPCAndPlayersTest(client); // Spawn some NPC's and other players
                    //Send0x4F57DC08(client); // No imediate visible change when not sending these
                    //Send0x642CD3D6(client); // No imediate visible change when not sending these
                    //Send0x96C46740(client); // No imediate visible change when not sending these

                    break;
                }

                case Opcodes.Ox2000:
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
                        Handle0X2000(client, account, packet);

                    break;
                }

                case Opcodes.Ox206A: // request change map(?)
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

                    //var packetData = packet.Data.ToArray().ToHexString();
                    Logger.Info("Opcodes.Ox206A");

                    break;
                }
                case Opcodes.Ox207B:
                {
                    //var packetData = packet.Data;
                    Logger.Info("Opcodes.Ox207B");
                    //Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

                    break;
                }

                case Opcodes.Ox205A:
                {
                    //var packetData = packet.Data;
                    Logger.Info("Opcodes.Ox205A");
                    //

                    break;
                }

                case Opcodes.Ping: // Ping
                {
                    new PacketStream() // p.158
                        .WriteHeader(Sender10, Receiver10, null, SendOpcodes.Pong)
                        .WriteUInt32(0x42c80000) // old = 0x42c80000, new = 0x42B32A07
                        .WriteUInt32(0)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case Opcodes.SpawnCheck: // p.160&161
                {
                    var part1 = packet.Data.ReadUInt32();
                    var spawnId = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt32();
                    var nClientInst = packet.Data.ReadUInt32();

                    new PacketStream()
                        .WriteHeader(Sender9, Receiver9, null, 0x2008)
                        .WriteUInt32(part1)
                        .WriteUInt32(spawnId)
                        .WriteByte(0)
                        .Send(client);

                    break;
                }

                case Opcodes.GcPing: // GCPing?  p.162
                {
                    var counter = packet.Data.ReadUInt32();
                    var time = 0;
                    Other.time(ref time);

                    new PacketStream()
                        .WriteHeader(Sender8, Receiver8, null, 0x0000207D)
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

                    if (account.CreateState == 1 && account.CreateCounter > 0)
                    {
                        account.State = 0;
                        account.CreateCounter = 0;

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
                        aBuffer.WriteUInt32(account.ClientInstance);
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
                        aBuffer.WriteUInt32(account.ClientInstance);
                        aBuffer.WriteArray(data2);
                        aBuffer.Send(client);
                    }

                    break;
                }

                case Opcodes.ManualRemoveBuff:
                {
                    Logger.Info("REMOVE BUFF");

                    break;
                }

                case Opcodes.ChatCommand:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    var unk1 = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();
                    var unk3 = packet.Data.ReadUInt32();
                    var text = packet.Data.ReadString();

                    if (text[0] == '.' && text[1] != '.' && account.Type >= AccountType.GameMaster)
                        HandleGMCommand(client, account, text);
                    else
                        _agentServerListener.CharacterSay(account, text);

                    break;
                }

                default:
                {
                    Logger.Info("Unknown packet: " + packet);
                    break;
                }
            }
        }
    }
}