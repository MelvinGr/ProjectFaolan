using System;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace CSPlayerAgent
{
    public partial class CsPlayerAgentListener : Server
    {
        public CsPlayerAgentListener(UInt16 port, Logger logger, IDatabase database) : base(port, logger, database)
        {
        }

        public override void ClientConnected(NetworkClient client)
        {
            Logger.WriteLine("New client with address: " + client.IpAddress);
        }

        public override void ClientDisconnected(NetworkClient client)
        {
            Logger.WriteLine("Client with address: " + client.IpAddress + " disconnected!");
        }

        public override void ReceivedPacket(NetworkClient client, Packet packet)
        {
            Logger.WriteLine("Received opcode: " + (Opcodes) packet.Opcode + " (" + packet.Opcode.ToHex() + ")");

            switch ((Opcodes) packet.Opcode)
            {
                case Opcodes.Authenticate:
                {
                    var nClientInst = packet.Data.ReadUInt32(); // ID64::InstanceType 
                    var accountId = packet.Data.ReadUInt32();
                    var auth = packet.Data.ReadUInt32();
                    var characterId = nClientInst & 0x00ffffff;

                    byte[] sender = {0x0d, 0x14, 0x56, 0xd5, 0x6d, 0x10, 0x04};
                    byte[] receiver = {0x0d, 0xc8, 0x60, 0xd5, 0xbb, 0x10, 0x84, 0x80, 0x80, 0x08};

                    new PacketStream()
                        .WriteHeader(sender, receiver, null, 0x2000, true)
                        .WriteUInt32(auth)
                        .Send(client);

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