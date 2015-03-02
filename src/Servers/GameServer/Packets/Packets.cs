using System;
using LibFaolan.Data;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void ReportDimensionID(NetworkClient client, Account account, UInt32 realmId)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new ConanStream()
                .WriteHeader(sender, receiver, null, 0x200b, true) // ReportDimesionID
                .WriteString(realmId.ToString())
                .Send(client);
        }

        private void ReportServerID(NetworkClient client, Account account, UInt32 value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new ConanStream()
                .WriteHeader(sender, receiver, null, 0x200c, true) // ReportServerID
                .WriteUInt32(0x0000000b)
                .WriteUInt32(value)
                .Send(client);
        }

        private void AckAuthentication(NetworkClient client, Account account, UInt32 value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new ConanStream()
                .WriteHeader(sender, receiver, null, 0x2001, true) // ReportServerID
                .WriteUInt32(value)
                .Send(client);
        }
    }
}