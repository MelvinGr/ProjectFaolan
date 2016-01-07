using Faolan.Core.Network;

namespace Faolan.GameServer.Packets
{
    internal partial class Packets
    {
        public static void Send0X201C(INetworkClient client)
        {
            var packetData3 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D, 0x3B, 0xC9
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData3)
                .Send(client);

            var packetData4 = new byte[]
            {
                0x00, 0x00, 0x03, 0xEA, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCA
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData4)
                .Send(client);

            var packetData5 = new byte[]
            {
                0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCB
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData5)
                .Send(client);

            var packetData6 = new byte[]
            {
                0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCC
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData6)
                .Send(client);

            var packetData7 = new byte[]
            {
                0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCD
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData7)
                .Send(client);

            var packetData8 = new byte[]
            {
                0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCE
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData8)
                .Send(client);

            var packetData9 = new byte[]
            {
                0x00, 0x00, 0x03, 0xEF, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xCF
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData9)
                .Send(client);

            var packetData10 = new byte[]
            {
                0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD0
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData10)
                .Send(client);

            var packetData11 = new byte[]
            {
                0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD1
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData11)
                .Send(client);

            var packetData12 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD2
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData12)
                .Send(client);

            var packetData13 = new byte[]
            {
                0x00, 0x00, 0x03, 0xF3, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD3
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData13)
                .Send(client);

            var packetData14 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD4
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData14)
                .Send(client);

            var packetData15 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD5
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData15)
                .Send(client);

            var packetData16 = new byte[]
            {
                0x00, 0x00, 0x03, 0xF6, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD6
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData16)
                .Send(client);

            var packetData17 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD7
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData17)
                .Send(client);

            var packetData18 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD8
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData18)
                .Send(client);

            var packetData19 = new byte[]
            {
                0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xD9
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData19)
                .Send(client);

            var packetData20 = new byte[]
            {
                0x00, 0x00, 0x03, 0xFA, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDA
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData20)
                .Send(client);

            var packetData21 = new byte[]
            {
                0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDB
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000065)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData21)
                .Send(client);

            var packetData22 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDC
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData22)
                .Send(client);

            var packetData23 = new byte[]
            {
                0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDD
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData23)
                .Send(client);

            var packetData24 = new byte[]
            {
                0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDE
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData24)
                .Send(client);

            var packetData25 = new byte[]
            {
                0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xDF
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData25)
                .Send(client);

            var packetData26 = new byte[]
            {
                0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE0
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData26)
                .Send(client);

            var packetData27 = new byte[]
            {
                0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE1
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData27)
                .Send(client);

            var packetData28 = new byte[]
            {
                0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE2
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData28)
                .Send(client);

            var packetData29 = new byte[]
            {
                0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE3
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData29)
                .Send(client);

            var packetData30 = new byte[]
            {
                0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE4
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData30)
                .Send(client);

            var packetData31 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE5
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData31)
                .Send(client);

            var packetData32 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE6
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData32)
                .Send(client);

            var packetData33 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE7
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData33)
                .Send(client);

            var packetData34 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE8
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData34)
                .Send(client);

            var packetData35 = new byte[]
            {
                0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xE9
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData35)
                .Send(client);

            var packetData36 = new byte[]
            {
                0x00, 0x00, 0x00, 0x21, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xEA
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData36)
                .Send(client);

            var packetData37 = new byte[]
            {
                0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xEB
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData37)
                .Send(client);

            var packetData38 = new byte[]
            {
                0x00, 0x00, 0x00, 0x23, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xEC
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData38)
                .Send(client);

            var packetData39 = new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xED
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000068)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData39)
                .Send(client);

            var packetData40 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xEE
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000070)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData40)
                .Send(client);

            var packetData41 = new byte[]
            {
                0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xEF
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000070)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData41)
                .Send(client);

            var packetData42 = new byte[]
            {
                0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF0
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000070)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData42)
                .Send(client);

            var packetData43 = new byte[]
            {
                0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF1
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000070)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData43)
                .Send(client);

            var packetData44 = new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF2
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000070)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData44)
                .Send(client);

            var packetData45 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF3
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData45)
                .Send(client);

            var packetData46 = new byte[]
            {
                0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF4
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData46)
                .Send(client);

            var packetData47 = new byte[]
            {
                0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF5
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData47)
                .Send(client);

            var packetData48 = new byte[]
            {
                0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF6
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData48)
                .Send(client);

            var packetData49 = new byte[]
            {
                0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF7
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData49)
                .Send(client);

            var packetData50 = new byte[]
            {
                0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF8
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData50)
                .Send(client);

            var packetData51 = new byte[]
            {
                0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xF9
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData51)
                .Send(client);

            var packetData52 = new byte[]
            {
                0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFA
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData52)
                .Send(client);

            var packetData53 = new byte[]
            {
                0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFB
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData53)
                .Send(client);

            var packetData54 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFC
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData54)
                .Send(client);

            var packetData55 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFD
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData55)
                .Send(client);

            var packetData56 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFE
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData56)
                .Send(client);

            var packetData57 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3B, 0xFF
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData57)
                .Send(client);

            var packetData58 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x00
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData58)
                .Send(client);

            var packetData59 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x01
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData59)
                .Send(client);

            var packetData60 = new byte[]
            {
                0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x02
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData60)
                .Send(client);

            var packetData61 = new byte[]
            {
                0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x03
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData61)
                .Send(client);

            var packetData62 = new byte[]
            {
                0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x04
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData62)
                .Send(client);

            var packetData63 = new byte[]
            {
                0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x05
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData63)
                .Send(client);

            var packetData64 = new byte[]
            {
                0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x06
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData64)
                .Send(client);

            var packetData65 = new byte[]
            {
                0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x07
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData65)
                .Send(client);

            var packetData66 = new byte[]
            {
                0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x08
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData66)
                .Send(client);

            var packetData67 = new byte[]
            {
                0x00, 0x00, 0x00, 0x19, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x09
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData67)
                .Send(client);

            var packetData68 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1A, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0A
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData68)
                .Send(client);

            var packetData69 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1B, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0B
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData69)
                .Send(client);

            var packetData70 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0C
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData70)
                .Send(client);

            var packetData71 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0D
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData71)
                .Send(client);

            var packetData72 = new byte[]
            {
                0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0E
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData72)
                .Send(client);

            var packetData73 = new byte[]
            {
                0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x0F
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData73)
                .Send(client);

            var packetData74 = new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x0D,
                0x3C, 0x10
            };
            new PacketStream()
                .WriteHeader(GameServerListener.Sender1, GameServerListener.Receiver1, null,
                    0x0000201C)
                .WriteUInt32(0x00000071)
                .WriteUInt32(client.Account.ClientInstance)
                .WriteArray(packetData74)
                .Send(client);
        }
    }
}