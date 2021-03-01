using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        private static void ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, byte[] data = null)
        {
            // Use as 'dummy' data (comes from Scythe Shield Rank 3 (3 hours, 20 min, 28ish sec))
            var spellData = new byte[]
            {
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x13, 0x7E, 0x26, // 1277478
                0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x02, 0x38, 0x7E, // 145534 
                0x00, 0x00, 0x00, 0x50, // 80
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x50, // 80
                0x00, 0x00, 0x00, 0x00,
                0x14, 0xC5, 0xC9, 0x12 // 348506386
            };

            data ??= spellData;

            new PacketStream()
                .WriteHeader(Sender0, Receiver0, null, 0x2000)
                .WriteArrayUInt32Length(new ConanStream()
                    .WriteUInt32(GameServerOx2000RespondsOpcodes.ApplySpell)
                    .WriteUInt32(0x0000C350)
                    .WriteUInt32(applyTo) //Apply to ?
                    .WriteUInt16(0x0000)
                    .WriteUInt32(spellId) // SpellID
                    .WriteUInt32(0x0000C350)
                    .WriteUInt32(applyFrom) // apply from ? 
                    .WriteArray(data))
                .Send(client);
        }

        private static void SetTimeOfDay(INetworkClient client)
        {
            var sender0 = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63};
            var receiver0 = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01};
            new PacketStream()
                .WriteHeader(sender0, receiver0, null, 0x2000)
                .WriteArrayUInt32Length(new ConanStream()
                    .WriteUInt32(GameServerOx2000RespondsOpcodes.SetDayTime)
                    /*.WriteArray(
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x01, 0x46, 0xA8, 0xD8,
                        0x00, 0x00, 0x00, 0x00, 
                        0x03,
                        0x00, 0x01, 0x42, 0x10,
                        0x54, 0xEB, 0x48, 0xD4,
                        0x3F, 0x80)*/
                    .WriteArray(
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x01, 0x47, 0x59, 0x92,
                        0x00, 0x00, 0x00, 0x00,
                        0x01,
                        0x00, 0x01, 0x6C, 0xD7,
                        0x60, 0x32, 0xDC, 0x20,
                        0x3F, 0x80,
                        0x00, 0x00))
                .Send(client);
        }

        private static void ReportDimensionId(INetworkClient client, uint realmId)
        {
            var sender0 = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63};
            var receiver0 = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01};
            new PacketStream()
                .WriteHeader(sender0, receiver0, null, GameServerRespondOpcodes.ReportDimensionId)
                .WriteString(realmId.ToString())
                .Send(client);
        }

        private static void ReportServerId(INetworkClient client, uint value)
        {
            var sender0 = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63};
            var receiver0 = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01};
            var packetData0 = new byte[] {0x00, 0x00, 0x00, 0x63, 0x00, 0x00, 0x00, 0x10};
            new PacketStream()
                .WriteHeader(sender0, receiver0, null, GameServerRespondOpcodes.ReportServerId)
                .WriteArray(packetData0)
                .Send(client);
        }

        private static void AckAuthentication(INetworkClient client, uint value)
        {
            var sender0 = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63};
            var receiver0 = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01};
            var packetData0 = new byte[] {0x00, 0x00, 0x00, 0x01};
            new PacketStream()
                .WriteHeader(sender0, receiver0, null, GameServerRespondOpcodes.AckAuthentication)
                .WriteArray(packetData0)
                .Send(client);
        }

        private static void SendReadyForPlayScreen(INetworkClient client)
        {
            var aBuffer = new PacketStream();
            aBuffer.WriteHeader(Sender6, Receiver6, null, GameServerRespondOpcodes.ReadyForPlayScreen);
            aBuffer.WritePosition(client.Character);
            aBuffer.WriteUInt32(0x00007e2); //2018 = 2*1009 -> -1 -> 1xCoord
            aBuffer.WritePosition(client.Character);
            aBuffer.WriteArray(
                0x00, 0x00, 0xc7, 0x9e,
                0x00, 0x00, 0x0f, 0xaa,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x64,
                0x00);
            aBuffer.Send(client);

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayUInt32Length(new ConanStream()
                    .WriteUInt32(0x06ec1255)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteUInt16(0x0009))
                .Send(client);

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayUInt32Length(new ConanStream()
                    .WriteUInt32(0x864cfef8)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteByte(0))
                .Send(client);
        }
    }
}