using LibFaolan.Data;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private static void MapChange(NetworkClient client, Account account)
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
                    .WriteUInt32(Ox2000RespondsOpcodes.MapChange)
                    .WriteUInt32(0)
                    .WriteUInt32(0)
                    .WriteByte(0)
                    .WriteVector3(account.Character.Position)
                    .WriteByte(0x62)
                    .WriteUInt32(0x0000C79C)
                    .WriteUInt32(account.Character.Map)
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

            if (data == null)
                data = spellData;

            new PacketStream()
                .WriteHeader(Sender0, Receiver0, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(Ox2000RespondsOpcodes.ApplySpell)
                    .WriteUInt32(0x0000C350)
                    .WriteUInt32(applyTo) //Apply to ?
                    .WriteUInt16(0x0000)
                    .WriteUInt32(spellId) // SpellID
                    .WriteUInt32(0x0000C350)
                    .WriteUInt32(applyFrom) // apply from ? 
                    .WriteArray(data))
                .Send(client);
        }

        private static void SetTimeofDay(NetworkClient client)
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
            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(Ox2000RespondsOpcodes.SetDayTime)
                    .WriteArray(packetData136))
                .Send(client);
        }

        private static void ReportDimensionId(NetworkClient client, Account account, uint realmId)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x200b) // ReportDimesionID
                .WriteString(realmId.ToString())
                .Send(client);
        }

        private static void ReportServerId(NetworkClient client, Account account, uint value)
        {
            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x200c) // ReportServerID
                .WriteUInt32(0x0000000b)
                .WriteUInt32(value)
                .Send(client);
        }

        private static void AckAuthentication(NetworkClient client, Account account, uint value)
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
                //0x43, 0xa7, 0x40, 0x00,
                //0x43, 0x14, 0x66, 0x66,
                //0x44, 0x25, 0x4c, 0xcd,
                //0x00, 0x00, 0x07, 0xe2, //2018 = 2*1009 -> -1 -> 1xCoord
                //0x43, 0xa7, 0x40, 0x00,
                //0x43, 0x14, 0x66, 0x66,
                //0x44, 0x25, 0x4c, 0xcd,
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
            aBuffer.WriteVector3(account.Character.Position);
            aBuffer.WriteUInt32(0x00007e2); //2018 = 2*1009 -> -1 -> 1xCoord
            aBuffer.WriteVector3(account.Character.Position);
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