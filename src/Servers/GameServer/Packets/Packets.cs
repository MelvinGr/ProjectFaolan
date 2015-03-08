using System;
using LibFaolan.Data;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void ReportDimensionId(NetworkClient client, Account account, UInt32 realmId)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x200b) // ReportDimesionID
                .WriteString(realmId.ToString())
                .Send(client);
        }

        private void ReportServerId(NetworkClient client, Account account, UInt32 value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x200c) // ReportServerID
                .WriteUInt32(0x0000000b)
                .WriteUInt32(value)
                .Send(client);
        }

        private void AckAuthentication(NetworkClient client, Account account, UInt32 value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x2001) // ReportServerID
                .WriteUInt32(value)
                .Send(client);
        }

        private static void SendReadyForPlayScreen(NetworkClient client, Account account)
        {
            var blobData1 = new byte[]
            {
                0x43, 0xa7, 0x40, 0x00,
                0x43, 0x14, 0x66, 0x66,
                0x44, 0x25, 0x4c, 0xcd,
                0x00, 0x00, 0x07, 0xe2, //2018 = 2*1009 -> -1 -> 1xCoord
                0x43, 0xa7, 0x40, 0x00,
                0x43, 0x14, 0x66, 0x66,
                0x44, 0x25, 0x4c, 0xcd,
                0x00, 0x00, 0xc7, 0x9e,
                0x00, 0x00, 0x0f, 0xaa,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x64,
                0x00
            };

            var aBuffer = new PacketStream();
            aBuffer.WriteHeader(Sender6, Receiver6, null, 0x2024);
            aBuffer.WriteArray(blobData1);
            aBuffer.Send(client);

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x06ec1255)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(account.ClientInstance)
                    .WriteUInt16(0x0009))
                .Send(client);

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x864cfef8)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(account.ClientInstance)
                    .WriteByte(0))
                .Send(client);
        }
    }
}