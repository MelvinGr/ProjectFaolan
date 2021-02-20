using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;

namespace Faolan.UniverseAgent
{
    partial class UniverseAgentListener
    {
        private static readonly byte[] Sender = {0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01};
        private static readonly byte[] Receiver = {0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0x99, 0xA7, 0x42};
        private static readonly byte[] Receiver2 = {0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xec, 0xeb, 0x80, 0xde, 0x03};

        private static void InitAuth(NetworkClient client)
        {
            new PacketStream()
                .WriteHeader(Sender, Receiver, null, UniverseAgentResponseOpcodes.InitiateAuthentication)
                .WriteString(Account.AuthChallenge)
                .Send(client);
        }

        private static void SetRegionState(NetworkClient client)
        {
            new PacketStream()
                .WriteHeader(Sender, Receiver2, null, UniverseAgentResponseOpcodes.SetRegion)
                .WriteArray
                (
                    0x01, 0x01, 0x01, 0x01,
                    0x3f, 0x80,
                    0x00, 0x00,
                    0x3f, 0x80,
                    0x00, 0x00, 0x01, 0x01,
                    0x00
                )
                .Send(client);
        }

        private void SendPlayerAgentRealm(NetworkClient client)
        {
            new PacketStream()
                .WriteHeader(Sender, Receiver2, null, UniverseAgentResponseOpcodes.SendPlayerAgentRealm)
                .WriteUInt32(0x01) // authstatus
                .WriteUInt64(client.Account.LongId)
                .WriteString($"{Configuration.ServerIpAddress()}:{Configuration.PlayerAgentPort()}")
                .WriteUInt32(client.Account.Id)
                .WriteUInt32(0x00000000)
                .Send(client);
        }
    }
}