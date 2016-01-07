using Faolan.Core;
using Faolan.Core.Config;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Extentions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.AgentServer
{
    public partial class AgentServerListener : Server<AgentServerPacket>
    {
        public AgentServerListener(ushort port, Logger logger, IDatabase database)
            : base(port, logger, database)
        {
        }

        public override void ReceivedPacket(INetworkClient client, AgentServerPacket packet)
        {
            Logger.Info("Received opcode: " + packet.Opcode + " (" + packet.Opcode.ToHex() + ")");

            switch (packet.Opcode)
            {
                case AgentServerOpcodes.Hello:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    client.Account.ClientInstance = packet.Data.ReadUInt32();
                    client.Account.Id = packet.Data.ReadUInt32();

                    var charId = (uint) 0; /*Database.ExecuteScalar<long>(
                        "SELECT characterid FROM clientinstances " +
                        "WHERE Accountid=" + client.Account.Id + " AND clientinst=" +
                        client.Account.ClientInstance);*/

                    client.Character = new Character(charId);
                    client.Character.LoadDetailsFromDatabase(Database);

                    new ConanStream()
                        .WriteUInt32(0x00050000)
                        .Send(client);

                    new ConanStream()
                        .WriteUInt16(AgentServerRespondsOpcodes.Ox0014)
                        .WriteArrayPrependLengthUInt16(new ConanStream()
                            .WriteUInt32(client.Account.ClientInstance)
                            .WriteUInt32(0x00000000)
                            .WriteString(client.Character.Name.Length > 0
                                ? client.Character.Name
                                : "Character" + client.Account.ClientInstance))
                        .Send(client);

                    // "<localized category=20000 token=\"welcome_message\">"
                    SendSystemMessage(client, Settings.WelcomeString);
                    SendSystemMessage(client, Statics.BuildInfo.Replace("\n", "<br />"));

                    new ConanStream()
                        .WriteUInt16(AgentServerRespondsOpcodes.Ox003C)
                        .WriteArrayPrependLengthUInt16(new ConanStream()
                            .WriteByte(0x04)
                            .WriteUInt32(0x0000232a)
                            .WriteString("~Trial")
                            .WriteUInt32(0x00008044)
                            .WriteUInt16(0x0000))
                        .Send(client);

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