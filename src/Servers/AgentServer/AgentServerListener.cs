using System;
using LibFaolan.Config;
using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace AgentServer
{
    public partial class AgentServerListener : Server<AgentServerPacket, ProtocolFactory<AgentServerWireProtocol>>
    {
        public AgentServerListener(UInt16 port, Logger logger, IDatabase database) : base(port, logger, database)
        {
        }

        public override void ClientConnected(NetworkClient client)
        {
            client.Tag = new Account();
        }

        public override void ReceivedPacket(NetworkClient client, AgentServerPacket packet)
        {
            Logger.Info("Received opcode: " + (Opcodes)packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (Account)client.Tag;

            switch ((Opcodes)packet.Opcode)
            {
            case Opcodes.Hello:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    account.ClientInstance = packet.Data.ReadUInt32();
                    account.Id = packet.Data.ReadUInt32();

                    var charId = (UInt32)Database.ExecuteScalar<Int64>(
                        "SELECT characterid FROM clientinstances " +
                        "WHERE accountid=" + account.Id + " AND clientinst=" + account.ClientInstance);

                    account.Character = new Character(charId);
                    account.Character.LoadDetailsFromDatabase(Database);

                    new ConanStream()
                        .WriteUInt32(0x00050000)
                        .Send(client);

                    new ConanStream()
                        .WriteUInt16(0x0014)
                        .WriteArrayPrependLengthUInt16(new ConanStream()
                            .WriteUInt32(account.ClientInstance)
                            .WriteUInt32(0x00000000)
                            .WriteString(account.Character.Name.Length > 0 ?
                            account.Character.Name : "Character" + account.ClientInstance))
                        .Send(client);

                    // "<localized category=20000 token=\"welcome_message\">"
                    SendSystemMessage(client, Settings.WelcomeString);
                    SendSystemMessage(client, Statics.BuildInfo.Replace("\n", "<br />"));

                    new ConanStream()
                        .WriteUInt16(0x003c)
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
                    Logger.Info("Unknown packet: " + packet);
                    break;
                }
            }
        }
    }
}