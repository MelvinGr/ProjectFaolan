using System;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace AgentServer
{
    public partial class AgentServerListener : Server
    {
        public AgentServerListener(UInt16 port, Logger logger, IDatabase database) : base(port, logger, database)
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
                //

                default:
                {
                    Logger.WriteLine("Unknown packet: " + packet);
                    break;
                }
            }
        }
    }
}