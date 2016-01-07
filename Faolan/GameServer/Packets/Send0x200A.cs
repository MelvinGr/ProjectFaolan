using Faolan.Core.Network;

namespace Faolan.GameServer.Packets
{
    internal partial class Packets
    {
        public static void Send0X200A(INetworkClient client)
        {
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x0013AF60)
                .Send(client);

            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x00142FD4)
                .Send(client);

            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x002DEFE2)
                .Send(client);

            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x0038DA35)
                .Send(client);

            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x003E338F)
                .Send(client);
        }
    }
}