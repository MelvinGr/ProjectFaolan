using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        private static void MapChange(NetworkClient client)
        {
            var packetData80 = new byte[]
            {
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x9C, 0x50,
                0x00, 0x10, 0xF6, 0x3A,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x18,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x10, 0xF6, 0x3A,
                0x00, 0x00, 0xCB, 0x20,
                0x41, 0x70,
                0x00, 0x00, 0x3F, 0x42,
                0x40, 0xA8,
                0x00, 0x00, 0x00, 0x02, // len for next 2?
                0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF,
                0x00, 0x00, 0x00, 0x07,
                0x00, 0x00, 0x00, 0x01
            };

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(GameServerOx2000RespondsOpcodes.MapChange)
                    .WriteUInt32(0)
                    .WriteUInt32(0)
                    .WriteByte(0)
                    .WriteVector3(client.Character.Position)
                    .WriteByte(0x62)
                    .WriteUInt32(0x0000C79C)
                    .WriteUInt32(client.Character.MapId)
                    .WriteArray(packetData80))
                .Send(client);
        }

        private static void ApplySpell(NetworkClient client, uint applyTo, uint applyFrom, uint spellId,
            byte[] data = null)
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
                .WriteHeader(Sender0, Receiver0, null,
                    0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
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

        private static void SetTimeOfDay(NetworkClient client)
        {
            var packetData136 = new byte[]
            {
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x01, 0x46, 0xA8, 0xD8,
                0x00,
                0x00, 0x00, 0x00, 0x03,
                0x00, 0x01, 0x42, 0x10,
                0x54, 0xEB, 0x48, 0xD4,
                0x3F, 0x80,
                0x00, 0x00
            };
            new PacketStream().WriteHeader(Sender5,
                    Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(GameServerOx2000RespondsOpcodes.SetDayTime)
                    .WriteArray(packetData136))
                .Send(client);
        }

        private static void ReportDimensionId(NetworkClient client, uint realmId)
        {
            /*var sender0 = new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63 };
            var receiver0 = new byte[] { 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01 };
            new PacketStream()
                .WriteHeader(sender0, receiver0, null, GameServerRespondOpcodes.ReportDimensionId)
                .WriteString(realmId.ToString())
                .Send(client);*/
        }

        private static void ReportServerId(NetworkClient client, uint value)
        {
            new PacketStream()
                .WriteHeader(Sender11, Receiver11, null, GameServerRespondOpcodes.ReportServerId)
                .WriteUInt32(0x0000000b)
                .WriteUInt32(value)
                .Send(client);
        }

        private static void AckAuthentication(NetworkClient client, uint value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, GameServerRespondOpcodes.AckAuthentication)
                .WriteUInt32(value)
                .Send(client);
        }

        private static void SendReadyForPlayScreen(NetworkClient client)
        {
            var aBuffer = new PacketStream();
            aBuffer.WriteHeader(Sender6, Receiver6, null, GameServerRespondOpcodes.ReadyForPlayScreen);
            aBuffer.WriteVector3(client.Character.Position);
            aBuffer.WriteUInt32(0x00007e2); //2018 = 2*1009 -> -1 -> 1xCoord
            aBuffer.WriteVector3(client.Character.Position);
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
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x06ec1255)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteUInt16(0x0009))
                .Send(client);

            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x864cfef8)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteByte(0))
                .Send(client);
        }
    }
}