using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void Send0x200A(NetworkClient client)
        {
            new PacketStream()
                .WriteHeader(sender1, receiver1, null, 0x0000200A, true)
                .WriteUInt32(0x0013AF60)
                .Send(client);

            new PacketStream()
                .WriteHeader(sender1, receiver1, null, 0x0000200A, true)
                .WriteUInt32(0x00142FD4)
                .Send(client);

            new PacketStream()
                .WriteHeader(sender1, receiver1, null, 0x0000200A, true)
                .WriteUInt32(0x002DEFE2)
                .Send(client);

            new PacketStream()
                .WriteHeader(sender1, receiver1, null, 0x0000200A, true)
                .WriteUInt32(0x0038DA35)
                .Send(client);

            new PacketStream()
                .WriteHeader(sender1, receiver1, null, 0x0000200A, true)
                .WriteUInt32(0x003E338F)
                .Send(client);
        }
    }
}