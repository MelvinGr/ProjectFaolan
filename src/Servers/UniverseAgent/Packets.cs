using LibFaolan.Config;
using LibFaolan.Data;
using LibFaolan.Network;

namespace UniverseAgent
{
    partial class UniverseAgentListener
    {
        private static readonly byte[] Sender = {0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01};
        private static readonly byte[] Receiver = {0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xdc, 0xc8, 0x49};
        private static readonly byte[] Receiver2 = {0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xec, 0xeb, 0x80, 0xde, 0x03};

        public static void InitAuth(NetworkClient client, Account account)
        {
            new ConanStream()
                .WriteHeader(Sender, Receiver, null, 0x2000, true)
                .WriteString(account.AuthChallenge)
                .Send(client);
        }

        public static void SetRegionState(NetworkClient client, Account account)
        {
            new ConanStream()
                .WriteHeader(Sender, Receiver2, null, 0x2005, true)
                .WriteArray(0x01, 0x01, 0x01, 0x01, 0x3f, 0x80, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x01, 0x00)
                .Send(client);
        }

        /*public static void AckAuthenticate(ConanClient client, UInt32 authStatus, UInt32 eReason, UInt16 errorCode)
        {
            byte[] sender = {0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01};
            byte[] receiver = {0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05};

            var aBuffer = new PacketBuffer()
                .WriteHeader(sender, receiver, null, 0x2001, true)
                .WriteUInt32(authStatus)
                .WriteUInt64(client.Id);

            if (errorCode == 0)
                aBuffer.WriteString(Statics.TerritoryManagerAddr);

            aBuffer.WriteUInt32(client.Cookie);
            aBuffer.WriteUInt32(eReason);

            if (errorCode > 0)
                aBuffer.WriteUInt16(errorCode);

            aBuffer.Send(client);
        }*/

        public static void SendPlayerAgentRealm(NetworkClient client, Account account)
        {
            new ConanStream()
                .WriteHeader(Sender, Receiver2, null, 0x2001, true)
                .WriteUInt32(0x01) // authstatus
                .WriteUInt64(account.LongId)
                .WriteString(Settings.PlayerAgentAddress + ":" + Settings.PlayerAgentPort)
                .WriteArray(0x31, 0x0c, 0xec, 0x57, 0x00, 0x00, 0x00, 0x00)
                .Send(client);
        }
    }
}