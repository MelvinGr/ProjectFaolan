using LibFaolan.Data;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void Send0X200A(NetworkClient client, Account account)
        {
            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null, 0x0000200A)
                .WriteUInt32(0x0013AF60)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null, 0x0000200A)
                .WriteUInt32(0x00142FD4)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null, 0x0000200A)
                .WriteUInt32(0x002DEFE2)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null, 0x0000200A)
                .WriteUInt32(0x0038DA35)
                .Send(client);

            new PacketStream()
                .WriteHeader(Sender1, Receiver1, null, 0x0000200A)
                .WriteUInt32(0x003E338F)
                .Send(client);
        }
    }
}