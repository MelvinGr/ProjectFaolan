using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace UniverseAgent
{
    public partial class UniverseAgentListener : Server
    {
        public UniverseAgentListener(ushort port, Logger logger, IDatabase database)
            : base(port, logger, database)
        {
        }

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
                case Opcodes.InitiateAuthentication:
                {
                    account.Username = packet.Data.ReadString();
                    var unk0 = packet.Data.ReadUInt32();

                    account.LoadDetailsFromDatabase(Database);
                    InitAuth(client, account);

                    break;
                }
                case Opcodes.AnswerChallenge:
                {
                    var cAnswerChallenge = packet.Data.ReadString();

                    // Decrypt data here
                    /*var decryptedData = client.Username + "|" + client.AuthChallenge + "|PASSWORD";
                    var decryptedDataVector = decryptedData.Split('|');

                    if (decryptedDataVector.Length != 3) // wrong decryption
                    {
                        Logger.WriteLine("Wrong decryption of username and password");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }

                    var username = decryptedDataVector[0];
                    var nChallenge = decryptedDataVector[1];
                    var password = decryptedDataVector[2];

                    if (nChallenge != client.AuthChallenge) // wrong auth
                    {
                        Logger.WriteLine("Wrong Auth");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }
                    */
                    if (!Database.CheckLogin(account.Username, account.Username)) // wrong login
                    {
                        Logger.WriteLine("Wrong Login");
                        //Packets.AckAuthenticate(client, 0xffffff, 0, 0x0e);
                        break;
                    }

                    /*
                    if (client.nClientInst == -1) // could not get clientInst
                    {
                        Logger.WriteLine("Could not get clientInst");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }*/

                    if (account.IsBanned(Database)) // player banned
                    {
                        Logger.WriteLine("Banned Player tried to login");
                        //Packets.AckAuthenticate(client, 0xffffff, 0, 0x17);
                        break;
                    }

                    Logger.WriteLine("User {0} is logging on with accountId: {1}", account.Username, account.Id);

                    account.UpdateLastInfo(Database, client);
                    SetRegionState(client, account);
                    SendPlayerAgentRealm(client, account);

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