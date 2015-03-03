using System.Collections.Generic;
using LibFaolan.Config;
using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace PlayerAgent
{
    public partial class PlayerAgentListener : Server
    {
        public PlayerAgentListener(ushort port, Logger logger, IDatabase database) : base(port, logger, database)
        {
            Realms = new List<Realm>();
            Realms.Add(new Realm
            {
                Country = 0,
                FullStatus = 0,
                IpAddress = Settings.GameServerAddress,
                LoadStatus = 0,
                OnlineStatus = 2,
                Port = Settings.GameServerPort,
                RealmId = 0,
                RealmName = "Project Faolan",
                RealmType = 256
            });
        }

        public List<Realm> Realms { get; }

        public override void ClientConnected(NetworkClient client)
        {
            client.Tag = new Account();
        }

        public override void ReceivedPacket(NetworkClient client, Packet packet)
        {
            Logger.WriteLine("Received opcode: " + (Opcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (Account) client.Tag;

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.RequestAuth:
                {
                    account.Id = packet.Data.ReadUInt32();
                    var cookie = packet.Data.ReadUInt32();
                    account.AuthStatus = packet.Data.ReadUInt32();
                    account.LoadDetailsFromDatabase(Database);

                    //if (cookie != client.Cookie)
                    //Logger.WriteLine("Cookie mismatch!");

                    InitAuth(client, account);

                    break;
                }
                case Opcodes.RequestSmallCharList:
                {
                    var unk1 = packet.Data.ReadString();
                    //Log.Notice("Receive unknown String: %s\n", unk1.c_str());
                    //send before charlist char-specific packets
                    SendSmallCharList(client, account);

                    break;
                }
                case Opcodes.RequestCharRealmData:
                {
                    SendCharacterList(client, account, Database);
                    SendRealmList(client, account);
                    Request_Char_RealmData_1(client, account);

                    break;
                }

                case Opcodes.Ox20A6:
                {
                    Ox20a6_1(client, account);

                    break;
                }

                case Opcodes.EnterWorld:
                {
                    var characterId = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt32();
                    var unk1 = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();
                    var unk3 = packet.Data.ReadByte();
                    var unk4 = packet.Data.ReadUInt32();
                    var unk5 = packet.Data.ReadUInt32();
                    var unk6 = packet.Data.ReadUInt32();

                    account.Character = new Character(characterId);
                    account.Character.LoadDetailsFromDatabase(Database);
                    account.Character.UpdateLastInfo(Database, client);

                    EnterWorld(client, account);

                    break;
                }

                case Opcodes.Ox20D6:
                {
                    var languageId = packet.Data.ReadUInt32();
                    Logger.WriteLine("Maybe report language (Language: " + languageId + ")");

                    break;
                }

                case Opcodes.CreateCharacter:
                {
                    var iNDimId = packet.Data.ReadUInt32();

                    var headerData = new byte[] {0x8b, 0xd8, 0x99, 0x02};
                    var sender = new byte[] {0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03};
                    var receiver = new byte[] {0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05};

                    account.ClientInstance = 0xdeadbeef;

                    new PacketStream()
                        .WriteHeader(sender, receiver, headerData, 0x20b9)
                        .WriteUInt32(0x0000c350)
                        .WriteUInt32(account.ClientInstance)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case Opcodes.DeleteCharacter:
                {
                    //

                    break;
                }

                case Opcodes.Ox20Ec: // request server address?
                {
                    // packet has no data

                    SendPlayerAgent(client, account);
                    SendCsPlayerAgent(client, account);
                    SendGameServer(client, account);
                    SendLast(client, account);

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