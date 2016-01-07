using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace CSPlayerAgent
{
    public partial class CsPlayerAgentListener : Server<ConanPacket, ProtocolFactory<ConanWireProtocol>>
    {
        public CsPlayerAgentListener(ushort port, Logger logger, IDatabase database) : base(port, logger, database)
        {
        }

        public override void ClientConnected(NetworkClient client)
        {
            client.Tag = new Account();
        }

        public override void ReceivedPacket(NetworkClient client, ConanPacket packet)
        {
            Logger.Info("Received opcode: " + (Opcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");
            var account = (Account) client.Tag;

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.Authenticate:
                {
                    account.ClientInstance = packet.Data.ReadUInt32(); // ID64::InstanceType 
                    account.Id = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadByte();
                    var auth = packet.Data.ReadUInt32();

                    byte[] sender = {0x0D, 0x14, 0x56, 0xD5, 0x6D, 0x10, 0x44};
                    byte[] receiver = {0x0D, 0xC8, 0x60, 0xD5, 0xBB, 0x10, 0xD4, 0xCB, 0x8B, 0x40};

                    new PacketStream()
                        .WriteHeader(sender, receiver, null, 0x2000)
                        .WriteUInt32(auth)
                        .Send(client);

                    break;
                }

                case Opcodes.Ox2019: // register?
                {
                    var unk0 = packet.Data.ReadUInt32(); // 0x00, 0x00, 0xC3, 0x50
                    var nClientInst = packet.Data.ReadUInt32(); // 0x08, 0x02, 0xE5, 0xD4

                    Logger.Info("Opcodes.Ox2019");

                    break;
                }

                case Opcodes.Ox2072:
                {
                    Logger.Info("Opcodes.Ox2072");
                    break;
                }

                case Opcodes.Ox202a:
                {
                    Logger.Info("Opcodes.Ox202a");

                    break;
                }

                case Opcodes.Ox203b:
                {
                    Logger.Info("Opcodes.Ox203b");
                    break;
                }

                case Opcodes.Ox203c:
                {
                    Logger.Info("Opcodes.Ox203c");
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