using System;
using System.Collections.Generic;
using LibFaolan;
using LibFaolan.Config;
using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Network.Shared;

namespace PlayerAgent
{
    public partial class PlayerAgentListener : Server<ConanPacket>
    {
        public List<ConanRealm> Realms { get; }

        public PlayerAgentListener(ushort port, Logger logger, IDatabase database) : base(port, logger, database)
        {
            Realms = new List<ConanRealm>();
            Realms.Add(new ConanRealm
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
            Logger.WriteLine("Received opcode: " + (Opcodes)packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (ConanAccount) client.Tag;

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.Request_Auth:
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
                case Opcodes.Request_SmallCharList:
                {
                    var unk1 = packet.Data.ReadString();
                    //Log.Notice("Receive unknown String: %s\n", unk1.c_str());
                    //send before charlist char-specific packets
                    SendSmallCharList(client, account);

                    break;
                }
                case Opcodes.Request_Char_RealmData:
                {
                    SendCharacterList(client, account, Database);
                    SendRealmList(client, account);
                    Request_Char_RealmData_1(client, account);

                    break;
                }

                case Opcodes.Ox20a6:
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

                    account.Character = new ConanCharacter(characterId);
                    account.Character.LoadDetailsFromDatabase(Database);
                    account.Character.UpdateLastInfo(Database, client);

                    EnterWorld(client, account);

                    break;
                }

                case Opcodes.Ox20d6:
                {
                    var languageId = packet.Data.ReadUInt32();
                    Logger.WriteLine("Maybe report language (Language: " + languageId + ")");

                    break;
                }

                case Opcodes.CreateCharacter:
                {
                    UInt32 i_nDimID = packet.Data.ReadUInt32();

                    var headerData = new byte[]{ 0x8b, 0xd8, 0x99, 0x02 };
                    var sender = new byte[] { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
                    var receiver = new byte[] { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

                    account.nClientInst = 0xdeadbeef;

                    new PacketBuffer()
                        .WriteHeader(sender, receiver, headerData, 0x20b9, true)
                        .WriteUInt32(0x0000c350)
                        .WriteUInt32(account.nClientInst)
                        .WriteUInt32(0)
                        .Send(client);

                    break;
                }

                case Opcodes.DeleteCharacter:
                {
                    //

                    break;
                }

                case Opcodes.Ox20EC: // request server address?
                {
                    // packet has no data

                    SendPlayerAgent(client, account);
                    SendCSPlayerAgent(client, account);
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