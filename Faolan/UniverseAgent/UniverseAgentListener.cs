using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Extentions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.UniverseAgent
{
    public partial class UniverseAgentListener : Server<ConanPacket>
    {
        public UniverseAgentListener(ushort port, Logger logger, IDatabase database)
            : base(port, logger, database)
        {
        }

        public override void ReceivedPacket(INetworkClient client, ConanPacket packet)
        {
            Logger.Info("Received opcode: " + (UniverseAgentOpcodes) packet.Opcode + " (" + packet.Opcode.ToHex() +
                        ")");

            switch ((UniverseAgentOpcodes) packet.Opcode)
            {
                case UniverseAgentOpcodes.InitiateAuthentication:
                {
                    client.Account.Name = packet.Data.ReadString();
                    var unk0 = packet.Data.ReadUInt32();

                    client.Account.LoadDetailsFromDatabase(Database);
                    InitAuth(client);

                    break;
                }
                case UniverseAgentOpcodes.AnswerChallenge:
                {
                    var cAnswerChallenge = packet.Data.ReadString();

                    // Decrypt data here
                    /*var decryptedData = client.Username + "|" + client.AuthChallenge + "|PASSWORD";
                    var decryptedDataVector = decryptedData.Split('|');

                    if (decryptedDataVector.Length != 3) // wrong decryption
                    {
                        Logger.Info("Wrong decryption of username and password");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }

                    var username = decryptedDataVector[0];
                    var nChallenge = decryptedDataVector[1];
                    var password = decryptedDataVector[2];

                    if (nChallenge != client.AuthChallenge) // wrong auth
                    {
                        Logger.Info("Wrong Auth");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }
                    */
                    if (!client.Account.CheckLogin(Database, client.Account.Name)) // wrong login
                    {
                        Logger.Info("Wrong Login");
                        //Packets.AckAuthenticate(client, 0xffffff, 0, 0x0e);
                        break;
                    }

                    /*
                    if (client.nClientInst == -1) // could not get clientInst
                    {
                        Logger.Info("Could not get clientInst");
                        Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
                        break;
                    }*/

                    if (client.Account.IsBanned) //(Database)) // player banned
                    {
                        Logger.Info("Banned Player tried to login");
                        //Packets.AckAuthenticate(client, 0xffffff, 0, 0x17);
                        break;
                    }

                    Logger.Info("User {0} is logging on with accountId: {1}", client.Account.Name, client.Account.Id);

                    client.Account.UpdateLastInfo(Database, client);
                    SetRegionState(client);
                    SendPlayerAgentRealm(client);

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