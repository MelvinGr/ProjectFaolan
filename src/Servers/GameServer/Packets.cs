using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibFaolan;
using LibFaolan.Data;
using LibFaolan.Network.Shared;

namespace GameServer
{
    partial class GameServerListener
    {
        void ReportDimensionID(NetworkClient client, ConanAccount account, UInt32 realmId)
        {
            var sender = new byte[] { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b };
            var receiver = new byte[] { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08 };

            new PacketBuffer()
            .WriteHeader(sender, receiver, null, 0x200b, true) // ReportDimesionID
            .WriteString(realmId.ToString())
            .Send(client);
        }

        void ReportServerID(NetworkClient client, ConanAccount account, UInt32 value)
        {
            var sender = new byte[] { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b };
            var receiver = new byte[] { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08 };

            new PacketBuffer()
            .WriteHeader(sender, receiver, null, 0x200c, true) // ReportServerID
            .WriteUInt32(0x0000000b)
            .WriteUInt32(value)
            .Send(client);
        }

        void AckAuthentication(NetworkClient client, ConanAccount account, UInt32 value)
        {
            var sender = new byte[] { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b };
            var receiver = new byte[] { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08 };

            new PacketBuffer()
            .WriteHeader(sender, receiver, null, 0x2001, true) // ReportServerID
            .WriteUInt32(value)
            .Send(client);
        }
    }
}
