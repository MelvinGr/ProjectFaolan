using System;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        private void MapChange(INetworkClient client)
        {
            var sender0 = new byte[] {0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63};
            var receiver0 = new byte[] {0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01};

            new PacketStream()
                .WriteHeader(sender0, receiver0, null, 0x00002000)
                .WriteArrayUInt32Length(new ConanStream()
                    .WriteUInt32(GameServerOx2000RespondsOpcodes.MapChange)
                    .WriteUInt32(0)
                    .WriteUInt32(0)
                    .WriteByte(0)
                    .WritePosition(client.Character)
                    .WriteByte(0x62)
                    .WriteUInt32(0x0000C79C)
                    .WriteUInt32(client.Character.MapId ?? throw new Exception("client.Character.MapId == null"))
                    .WriteArray(
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x01,
                        0x00, 0x00, 0x9C, 0x50,
                        0x00, 0x34, 0x34, 0x8C,
                        0x00,
                        0x00, 0x00, 0x00, 0x01,
                        0x00, 0x00, 0x00, 0x18,
                        0x00, 0x00, 0x00, 0x01,
                        0x00, 0x34, 0x34, 0x8C,
                        0x00, 0x00,
                        0xCB, 0x20, 0x41, 0x70,
                        0x00, 0x00,
                        0x3E, 0xF2, 0x95, 0x73,
                        0x00, 0x00, 0x00, 0x03,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0x00, 0x00, 0x00, 0x07,
                        0x00, 0x00, 0x00, 0x01
                    ))
                .Send(client);
        }
    }
}