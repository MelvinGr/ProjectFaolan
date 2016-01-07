using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Config;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Extentions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.PlayerAgent
{
    public partial class PlayerAgentListener : Server<ConanPacket>
    {
        public PlayerAgentListener(ushort port, Logger logger, IDatabase database) : base(port, logger, database)
        {
            Realms = new List<Realm>
            {
                new Realm
                {
                    Country = 0,
                    FullStatus = 0,
                    IpAddress = Settings.GameServerAddress,
                    LoadStatus = 0,
                    OnlineStatus = 2,
                    Port = Settings.GameServerPort,
                    Id = 0,
                    Name = "Project Faolan",
                    Type = 256
                }
            };
        }

        public List<Realm> Realms { get; }

        public override void ReceivedPacket(INetworkClient client, ConanPacket packet)
        {
            Logger.Info("Received opcode: " + (PlayerAgentOpcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");

            switch ((PlayerAgentOpcodes) packet.Opcode)
            {
                case PlayerAgentOpcodes.RequestAuth:
                {
                    client.Account.Id = packet.Data.ReadUInt32();
                    var cookie = packet.Data.ReadUInt32();
                    client.Account.AuthStatus = packet.Data.ReadUInt32();
                    client.Account.LoadDetailsFromDatabase(Database);

                    //if (cookie != client.Cookie)
                    //Logger.Info("Cookie mismatch!");

                    InitAuth(client);

                    break;
                }
                case PlayerAgentOpcodes.RequestSmallCharList:
                {
                    var unk1 = packet.Data.ReadString();
                    //Log.Notice("Receive unknown String: %s\n", unk1.c_str());
                    //send before charlist char-specific packets
                    SendSmallCharList(client);

                    break;
                }
                case PlayerAgentOpcodes.RequestCharRealmData:
                {
                    SendCharacterList(client, Database);
                    SendRealmList(client);
                    Request_Char_RealmData_1(client);

                    break;
                }

                case PlayerAgentOpcodes.Ox20A6:
                {
                    Ox20a6_1(client);

                    break;
                }

                case PlayerAgentOpcodes.EnterWorld:
                {
                    var characterId = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt32();
                    var unk1 = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();
                    var unk3 = packet.Data.ReadByte();
                    var unk4 = packet.Data.ReadUInt32();
                    var unk5 = packet.Data.ReadUInt32();
                    var unk6 = packet.Data.ReadUInt32();

                    client.Character = new Character(characterId);
                    client.Character.LoadDetailsFromDatabase(Database);
                    client.Character.UpdateLastInfo(Database, client);

                    EnterWorld(client);

                    break;
                }

                case PlayerAgentOpcodes.Ox20D6:
                {
                    var languageId = packet.Data.ReadUInt32();
                    Logger.Info("Maybe report language (Language: " + languageId + ")");

                    break;
                }

                case PlayerAgentOpcodes.CreateCharacter:
                {
                    var iNDimId = packet.Data.ReadUInt32();

                    var headerData = new byte[] {0x8b, 0xd8, 0x99, 0x02};
                    var sender = new byte[] {0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03};
                    var receiver = new byte[] {0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05};

                    /*Database.ExecuteNonQuery("INSERT INTO Characters (account_id, realm_id) VALUES (" +
                                             client.Account.Id +
                                             ", " + iNDimId + ")");
                    var charId = (uint) Database.LastInsertRowId();

                    Database.ExecuteNonQuery("INSERT INTO clientinstances (accountid, characterid) VALUES (" +
                                             client.Account.Id + ", " + charId + ")");
                    client.Account.ClientInstance = (uint) Database.LastInsertRowId();*/

                    new PacketStream()
                        .WriteHeader(sender, receiver, headerData, PlayerAgentRespondseOpcodes.CreateCharacter)
                        .WriteUInt32(0x0000c350)
                        .WriteUInt32(client.Account.ClientInstance)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case PlayerAgentOpcodes.DeleteCharacter:
                {
                    var data = packet.Data.ToArray();

                    Logger.Info("DELETE CHAR");

                    break;
                }

                case PlayerAgentOpcodes.Ox20Ec: // request server address?
                {
                    // packet has no data

                    SendAgentServer(client);
                    SendCsPlayerAgent(client);
                    SendGameServer(client);
                    SendLast(client);

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