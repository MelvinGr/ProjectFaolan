using Faolan.Core.Network;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        private static void Send0X200A(NetworkClient client)
        {
            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x0013AF60)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x00142FD4)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x002DEFE2)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x0038DA35)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null,
                    0x0000200A)
                .WriteUInt32(0x003E338F)
                .Send(client);
        }
    }
}