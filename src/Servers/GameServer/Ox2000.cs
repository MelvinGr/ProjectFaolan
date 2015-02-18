using System;
using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Network.Shared;
using LibFaolan;

namespace GameServer
{
    partial class GameServerListener
    {
        public void Ox2000(NetworkClient client, ConanAccount account, ConanPacket packet)
        {
            if (packet.SenderInt[1] == 0x48)
            {
                var sId1 = packet.Data.ReadUInt32();
                var sId2 = packet.Data.ReadUInt32();
                var sId3 = packet.Data.ReadUInt32();
                var sId4 = packet.Data.ReadUInt32();

                var sender = new byte[] {0x0d, 0xfc, 0x56, 0x97, 0x5b, 0x10, 0x14};
                var receiver = new byte[] {0x0d, 0x48, 0x76, 0x37, 0x28, 0x10, 0x84, 0x80, 0x80, 0x08};

                var aBuffer = new PacketBuffer();
                aBuffer.WriteHeader(sender, receiver, null, 0x2000, true);
                aBuffer.WriteUInt32(sId3);
                aBuffer.WriteUInt32(sId4);
                aBuffer.WriteUInt32(sId4 != 0x000027f9 ? 0x00000002 : 0x00000000);
                aBuffer.Send(client);
            }
            else
            {
                var pbLength = packet.Data.ReadUInt32();
                var pbOpcode = packet.Data.ReadUInt32();
                Logger.WriteLine("Data Opcode: " + pbOpcode.ToHex());
                switch (pbOpcode)
                {
                    case 0xf98e10b3:
                    {
                        var sender1 = new byte[] {0x0d, 0x5d, 0xb9, 0xec, 0xa9, 0x10, 0x14};
                        var receiver1 = new byte[] {0x0d, 0x91, 0xf7, 0x87, 0x8b, 0x10, 0x84, 0x80, 0x80, 0x08};
                        var sender2 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
                        var receiver2 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};

                        var aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1da);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000008);
                        aBuffer.WriteUInt32(0xffffffff);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1db);
                        aBuffer.Send(client);

                        var data = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x08, 0x91, 0xc8, 0x80, 0x80, 0xe0, 0xca,
                            0xe4, 0xea, 0x20, 0x12, 0x07, 0x08, 0x00, 0x10, 0x81, 0x80, 0x80, 0x20, 0x12, 0x08, 0x08,
                            0x08, 0x10, 0xff, 0xff, 0xff, 0xff, 0x0f, 0x12, 0x06, 0x08, 0x17, 0x10, 0xdb, 0xc3, 0x07,
                            0x12, 0x04, 0x08, 0x2e, 0x10, 0x07, 0x1a, 0x53, 0x0a, 0x14, 0x0d, 0x79, 0x9b, 0xa3, 0x95,
                            0x15, 0x12, 0xcc, 0xbc, 0xaa, 0x1d, 0x57, 0x16, 0x3c, 0x4d, 0x25, 0x63, 0xa0, 0x9f, 0xa1,
                            0x12, 0x14, 0x0d, 0x79, 0x9b, 0xa3, 0x95, 0x15, 0x12, 0xcc, 0xbc, 0xaa, 0x1d, 0x57, 0x16,
                            0x3c, 0x4d, 0x25, 0x63, 0xa0, 0x9f, 0xa1, 0x1a, 0x14, 0x0d, 0x79, 0x9b, 0xa3, 0x95, 0x15,
                            0x12, 0xcc, 0xbc, 0xaa, 0x1d, 0x57, 0x16, 0x3c, 0x4d, 0x25, 0x63, 0xa0, 0x9f, 0xa1, 0x25,
                            0x4c, 0xf2, 0x04, 0x00, 0x2d, 0x4c, 0xf2, 0x04, 0x00, 0x38, 0x01, 0x45, 0x4b, 0x59, 0x41,
                            0x30, 0x00, 0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2020, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data);
                        aBuffer.Send(client);
                        
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1dc);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000008);
                        aBuffer.WriteUInt32(0xffffffff);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1dd);
                        aBuffer.Send(client);

                        var data2 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x80, 0x08, 0x92, 0xc8, 0x80, 0x80, 0xe0, 0xca,
                            0xe4, 0xea, 0x20, 0x12, 0x07, 0x08, 0x00, 0x10, 0x81, 0x80, 0x80, 0x20, 0x12, 0x08, 0x08,
                            0x08, 0x10, 0xff, 0xff, 0xff, 0xff, 0x0f, 0x12, 0x06, 0x08, 0x17, 0x10, 0xdd, 0xc3, 0x07,
                            0x12, 0x04, 0x08, 0x2e, 0x10, 0x07, 0x1a, 0x53, 0x0a, 0x14, 0x0d, 0xbc, 0x59, 0x3a, 0x99,
                            0x15, 0x13, 0x29, 0x69, 0x02, 0x1d, 0xcf, 0x54, 0x5f, 0x7a, 0x25, 0xa9, 0x3d, 0x47, 0x9d,
                            0x12, 0x14, 0x0d, 0xbc, 0x59, 0x3a, 0x99, 0x15, 0x13, 0x29, 0x69, 0x02, 0x1d, 0xcf, 0x54,
                            0x5f, 0x7a, 0x25, 0xa9, 0x3d, 0x47, 0x9d, 0x1a, 0x14, 0x0d, 0xbc, 0x59, 0x3a, 0x99, 0x15,
                            0x13, 0x29, 0x69, 0x02, 0x1d, 0xcf, 0x54, 0x5f, 0x7a, 0x25, 0xa9, 0x3d, 0x47, 0x9d, 0x25,
                            0xe3, 0xf1, 0x04, 0x00, 0x2d, 0xe3, 0xf1, 0x04, 0x00, 0x38, 0x01, 0x45, 0x4f, 0x47, 0x53,
                            0x4c, 0x00, 0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2020, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data2);
                        aBuffer.Send(client);
                        
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1de);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000008);
                        aBuffer.WriteUInt32(0xffffffff);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1df);
                        aBuffer.Send(client);

                        var data3 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x7d, 0x08, 0x93, 0xc8, 0x80, 0x80, 0xe0, 0xca,
                            0xe4, 0xea, 0x20, 0x12, 0x04, 0x08, 0x00, 0x10, 0x01, 0x12, 0x08, 0x08, 0x08, 0x10, 0xff,
                            0xff, 0xff, 0xff, 0x0f, 0x12, 0x06, 0x08, 0x17, 0x10, 0xdf, 0xc3, 0x07, 0x12, 0x04, 0x08,
                            0x2e, 0x10, 0x07, 0x1a, 0x53, 0x0a, 0x14, 0x0d, 0x99, 0xbf, 0x60, 0xdd, 0x15, 0xf6, 0x73,
                            0x09, 0x76, 0x1d, 0xca, 0xed, 0x49, 0x79, 0x25, 0xd2, 0xc7, 0xed, 0x8d, 0x12, 0x14, 0x0d,
                            0x99, 0xbf, 0x60, 0xdd, 0x15, 0xf6, 0x73, 0x09, 0x76, 0x1d, 0xca, 0xed, 0x49, 0x79, 0x25,
                            0xd2, 0xc7, 0xed, 0x8d, 0x1a, 0x14, 0x0d, 0x99, 0xbf, 0x60, 0xdd, 0x15, 0xf6, 0x73, 0x09,
                            0x76, 0x1d, 0xca, 0xed, 0x49, 0x79, 0x25, 0xd2, 0xc7, 0xed, 0x8d, 0x25, 0x15, 0x4b, 0x33,
                            0x00, 0x2d, 0x15, 0x4b, 0x33, 0x00, 0x38, 0x01, 0x45, 0x35, 0x50, 0x55, 0x43, 0x00, 0x00,
                            0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2020, true);
                        aBuffer.WriteUInt32(0x00000068);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data3);
                        aBuffer.Send(client);

                        var data4 = new byte[]
                        {
                            0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x9b, 0xc8, 0x02, 0x18,
                            0x01, 0x28, 0x08, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x04,
                            0xf2, 0x4c, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x30,
                            0x41, 0x59, 0x4b, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57,
                            0xa1, 0x9f, 0xa0, 0x63, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16,
                            0x57, 0xa1, 0x9f, 0xa0, 0x63, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90,
                            0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000078);
                        aBuffer.WriteUInt32(0x642cd3d6);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data4);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000065);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0x00000010);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1e1);
                        aBuffer.Send(client);

                        var data5 = new byte[]
                        {
                            0x00, 0x10, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x9b, 0xc8, 0x02, 0x18,
                            0x02, 0x28, 0x08, 0x30, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x00, 0x04,
                            0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x4c,
                            0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf,
                            0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54,
                            0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90,
                            0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000078);
                        aBuffer.WriteUInt32(0x642cd3d6);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data5);
                        aBuffer.Send(client);

                        var data6 = new byte[]
                        {
                            0x00, 0x10, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x9b, 0xc8, 0x02, 0x18,
                            0x03, 0x28, 0x08, 0x30, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x12, 0x00, 0x33,
                            0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x43,
                            0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca,
                            0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed,
                            0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90,
                            0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000078);
                        aBuffer.WriteUInt32(0x642cd3d6);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data6);
                        aBuffer.Send(client);

                        var data7 = new byte[]
                        {
                            0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x9b, 0xc8, 0x02, 0x18,
                            0x02, 0x28, 0x08, 0x30, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x00, 0x04,
                            0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x4c,
                            0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf,
                            0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54,
                            0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90,
                            0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000078);
                        aBuffer.WriteUInt32(0x642cd3d6);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data7);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000065);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0x00000005);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1e3);
                        aBuffer.Send(client);

                        var data8 = new byte[]
                        {
                            0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x9b, 0xc8, 0x02, 0x18,
                            0x03, 0x28, 0x08, 0x30, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x12, 0x00, 0x33,
                            0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x43,
                            0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca,
                            0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed,
                            0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90,
                            0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000078);
                        aBuffer.WriteUInt32(0x642cd3d6);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data8);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201c, true);
                        aBuffer.WriteUInt32(0x00000065);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0x00000017);
                        aBuffer.WriteUInt32(0x0001e1e5);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000015);
                        aBuffer.WriteUInt32(0xf98e10b3);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteByte(0);
                        aBuffer.WriteUInt32(0x00000360);
                        aBuffer.WriteUInt32(0x00000003);
                        aBuffer.Send(client);

                        var data9 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data9);
                        aBuffer.Send(client);

                        var data10 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data10);
                        aBuffer.Send(client);

                        var data11 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x38, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data11);
                        aBuffer.Send(client);

                        var data12 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x9c, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data12);
                        aBuffer.Send(client);

                        var data13 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x19, 0xc8, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data13);
                        aBuffer.Send(client);

                        var data14 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1a, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data14);
                        aBuffer.Send(client);

                        var data15 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1a, 0xf4, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data15);
                        aBuffer.Send(client);

                        var data16 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1b, 0xbc, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data16);
                        aBuffer.Send(client);

                        var data17 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1f, 0xa4, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data17);
                        aBuffer.Send(client);

                        var data18 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x6c, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data18);
                        aBuffer.Send(client);

                        var data19 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x6d, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data19);
                        aBuffer.Send(client);

                        var data20 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x6e, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data20);
                        aBuffer.Send(client);

                        var data21 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x6f, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data21);
                        aBuffer.Send(client);

                        var data22 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data22);
                        aBuffer.Send(client);

                        var data23 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x71, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data23);
                        aBuffer.Send(client);

                        var data24 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0xd0, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data24);
                        aBuffer.Send(client);

                        var data25 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x6c, 0x00, 0x0b, 0xbc, 0x46, 0x00, 0x0b, 0xbc, 0x46, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x4b, 0x4d, 0x39, 0x36, 0xc0, 0xeb, 0xa3, 0x41, 0x80, 0x61,
                            0x06, 0xf3, 0x12, 0xb8, 0x35, 0x3e, 0xb3, 0xc3, 0x96, 0x42, 0xc0, 0xeb, 0xa3, 0x41, 0x80,
                            0x61, 0x06, 0xf3, 0x12, 0xb8, 0x35, 0x3e, 0xb3, 0xc3, 0x96, 0x42, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data25);
                        aBuffer.Send(client);

                        var data26 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x6d, 0x00, 0x0b, 0xbc, 0x47, 0x00, 0x0b, 0xbc, 0x47, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x50, 0x44, 0x42, 0x31, 0x64, 0xa7, 0x64, 0xb9, 0x96, 0xb5,
                            0xca, 0xb8, 0xe4, 0x76, 0x7c, 0x76, 0xa7, 0x0d, 0xe4, 0xe7, 0x64, 0xa7, 0x64, 0xb9, 0x96,
                            0xb5, 0xca, 0xb8, 0xe4, 0x76, 0x7c, 0x76, 0xa7, 0x0d, 0xe4, 0xe7, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data26);
                        aBuffer.Send(client);

                        var data27 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x70, 0x00, 0x0b, 0xbc, 0x4b, 0x00, 0x0b, 0xbc, 0x4b, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x49, 0x31, 0x4b, 0x4b, 0xcd, 0x42, 0x9c, 0xfd, 0x62, 0xf3,
                            0x3e, 0x51, 0x4d, 0x49, 0x8e, 0x24, 0xb0, 0x4f, 0x6c, 0xcb, 0xcd, 0x42, 0x9c, 0xfd, 0x62,
                            0xf3, 0x3e, 0x51, 0x4d, 0x49, 0x8e, 0x24, 0xb0, 0x4f, 0x6c, 0xcb, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data27);
                        aBuffer.Send(client);

                        var data28 = new byte[]
                        {
                            0x00, 0x00, 0x04, 0xe2, 0x00, 0x32, 0x9b, 0x83, 0x00, 0x32, 0x9b, 0x83, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x50, 0x4d, 0x58, 0x85, 0x42, 0xef, 0xf0, 0x2c, 0xae,
                            0x85, 0x4a, 0xc7, 0x0d, 0x86, 0xcb, 0xeb, 0xf0, 0x6e, 0xcb, 0x85, 0x42, 0xef, 0xf0, 0x2c,
                            0xae, 0x85, 0x4a, 0xc7, 0x0d, 0x86, 0xcb, 0xeb, 0xf0, 0x6e, 0xcb, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data28);
                        aBuffer.Send(client);

                        var data29 = new byte[]
                        {
                            0x00, 0x00, 0x01, 0x03, 0x00, 0x38, 0x15, 0xc0, 0x00, 0x38, 0x15, 0xc0, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x35, 0x56, 0x33, 0x64, 0x01, 0x8d, 0x59, 0x5b, 0xb1,
                            0x00, 0x9d, 0x52, 0x74, 0xc5, 0x57, 0x6d, 0x47, 0x45, 0xd4, 0x64, 0x01, 0x8d, 0x59, 0x5b,
                            0xb1, 0x00, 0x9d, 0x52, 0x74, 0xc5, 0x57, 0x6d, 0x47, 0x45, 0xd4, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data29);
                        aBuffer.Send(client);

                        var data30 = new byte[]
                        {
                            0x00, 0x00, 0x04, 0xe4, 0x00, 0x39, 0xe7, 0x83, 0x00, 0x39, 0xe7, 0x83, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x37, 0x30, 0x39, 0x4c, 0x14, 0xbd, 0x51, 0x26, 0x54, 0x6d,
                            0xd8, 0x6b, 0x7c, 0x60, 0xbd, 0x85, 0x1f, 0x3f, 0x36, 0xc6, 0x14, 0xbd, 0x51, 0x26, 0x54,
                            0x6d, 0xd8, 0x6b, 0x7c, 0x60, 0xbd, 0x85, 0x1f, 0x3f, 0x36, 0xc6, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data30);
                        aBuffer.Send(client);

                        var data31 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x35, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data31);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416544);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416541);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416542);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416548);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x004165f1);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x004165ef);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416546);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416545);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x004165f0);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416547);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416543);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000012);
                        aBuffer.WriteUInt32(0xa8bbeb7f);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt16(0x0001);
                        aBuffer.WriteUInt32(0x00416549);
                        aBuffer.Send(client);

                        var data32 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x39, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data32);
                        aBuffer.Send(client);

                        var data33 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0xe5, 0xf0, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data33);
                        aBuffer.Send(client);

                        var data34 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0xdb, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data34);
                        aBuffer.Send(client);

                        var data35 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0xdd, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data35);
                        aBuffer.Send(client);

                        var data36 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x03, 0xfc, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data36);
                        aBuffer.Send(client);

                        var data37 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x04, 0xc4, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data37);
                        aBuffer.Send(client);

                        var data38 = new byte[]
                        {
                            0x00, 0x00, 0x04, 0xe6, 0x00, 0x4b, 0x3e, 0xc8, 0x00, 0x4b, 0x3e, 0xc8, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x31, 0x44, 0x53, 0xb3, 0xae, 0xe6, 0xaa, 0x50, 0x9c,
                            0xcd, 0x1e, 0xb4, 0xe9, 0xf4, 0xb4, 0x7e, 0xcf, 0xe7, 0xd2, 0xb3, 0xae, 0xe6, 0xaa, 0x50,
                            0x9c, 0xcd, 0x1e, 0xb4, 0xe9, 0xf4, 0xb4, 0x7e, 0xcf, 0xe7, 0xd2, 0xb2, 0x90, 0x80, 0x5c,
                            0x29, 0xe6, 0x27, 0xca, 0xb2, 0x90, 0x80, 0x5c, 0x29, 0xe6, 0x27, 0xca, 0x00, 0x00, 0x00,
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x01
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2050, true);
                        aBuffer.WriteArray(data38);
                        aBuffer.Send(client);

                        var data39 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x0e, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002d);
                        aBuffer.WriteUInt32(0x6b6fd368);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data39);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x00000011);
                        aBuffer.WriteUInt32(0x40749b8a);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteByte(1);
                        aBuffer.WriteUInt32(0x0000003d);
                        aBuffer.Send(client);

                        var data40 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xcf, 0x3d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                            0x00
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000003a);
                        aBuffer.WriteUInt32(0xf508f4c1);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(data40);
                        aBuffer.Send(client);

                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x201b, true);
                        aBuffer.Send(client);


                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteUInt32(0x0000002b);
                        aBuffer.WriteUInt32(0x10d27bc0);
                        aBuffer.WriteUInt32(0x0000c350);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteByte(0x01);
                        aBuffer.WriteUInt32(0x62);
                        aBuffer.WriteUInt32(0x0000c79c);
                        aBuffer.WriteUInt32(0x00000faa); //map
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteUInt32(0);
                        aBuffer.WriteByte(0);
                        aBuffer.Send(client);

                        account.charInfo.Level = 1;
                        account.charInfo.Map = 0x00000faa;
                        //MELVIN 
                        /*if (!Database.updateCharacter(&account.charInfo))
                        {
                            Logger.WriteLine("Critical error at saving new character " + account.charInfo.Name);
                        }*/

                        break;
                    }
                    case 0xa36d3b74:
                    {
                        var recObjTyp = packet.Data.ReadUInt32();
                        var recClientInst = packet.Data.ReadUInt32();
                        UInt16 unk0 = packet.Data.ReadByte();
                        var recDataLength = packet.Data.ReadUInt32();
                        var unk2 = packet.Data.ReadUInt32();
                        var unk3 = packet.Data.ReadByte();
                        var runByte = packet.Data.ReadByte();
                        UInt32 recVal = 0;
                        var runId = 0;
                        while (runByte != 0x22)
                        {
                            if (runId == 0)
                                recVal = runByte;
                            if (runId == 1)
                                recVal += (runByte*0x100u);
                            if (runId == 2)
                                recVal += (runByte*0x10000u);
                            if (runId == 3)
                                recVal += (runByte*0x1000000u);

                            runByte = packet.Data.ReadByte();
                            runId++;
                        }
                        var recCmd = packet.Data.ReadString();
                        var strend = packet.Data.ReadByte();
                        var recSecCmd = packet.Data.ReadString();
                        if (recCmd == "IntroDone")
                        {
                            var sender1 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
                            var receiver1 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};
                            var aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            aBuffer.WriteUInt32(0x00000019);
                            aBuffer.WriteUInt32(0x96b8dc59);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteByte(0);
                            aBuffer.WriteUInt32(0x00000001);
                            aBuffer.WriteUInt32(0x000000ba);
                            aBuffer.WriteUInt32(0);
                            aBuffer.Send(client);
                        }
                        else if (recCmd == "ChangeSex")
                        {
                            account.charInfo.Sex = (byte)recVal;
                        }
                        else if (recCmd == "ChangeRace")
                        {
                            account.charInfo.Race = recVal;
                        }
                        else if (recCmd == "ChangeHeadMesh")
                        {
                            account.charInfo.HeadMesh = recVal;
                        }
                        else if (recCmd == "ChangeSize")
                        {
                            account.charInfo.Size = (byte)recVal;
                        }
                        else if (recCmd == "ChangeClass")
                        {
                            account.charInfo.Class = recVal;
                        }
                        else if (recCmd == "ChangeVoice")
                        {
                            account.charInfo.Voice = recVal;
                        }
                        else if (recCmd == "SetMorphValue")
                        {
                            Logger.WriteLine("Second Command: " + recSecCmd +
                                             " with value " + recVal);
                            if (recSecCmd.Length > 0)
                            {
                                //vector<string> scriptData = String::splitString(recSecCmd, "_");
                                //Logger.WriteLine("Splitted String: %s - " + scriptData[0], scriptData[1]);
                                account.counter++;
                                account.state = 1;

                                var sender1 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
                                var receiver1 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};
                                var aBuffer = new PacketBuffer();
                                aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                                var size = (UInt32) recSecCmd.Length + (5*4) + (1*2) + (2*1);
                                aBuffer.WriteUInt32(size);
                                aBuffer.WriteUInt32(0xbadf5a4b);
                                aBuffer.WriteUInt32(0x0000c350);
                                aBuffer.WriteUInt32(account.nClientInst);
                                aBuffer.WriteByte(0);
                                aBuffer.WriteString(recSecCmd);
                                aBuffer.WriteUInt32(0x3f800000);
                                aBuffer.WriteByte(0);
                                aBuffer.WriteUInt32(account.counter + 1);
                                //Logger.WriteLine("Send packet:\n " + aBuffer.ToString());
                                aBuffer.Send(client);
                            }
                            else
                            {
                                account.counter = 0;
                            }
                        }
                        else if (recCmd == "TheNameIs")
                        {
                            Logger.WriteLine("Attempt to create Char with the name: " + recSecCmd);
                            account.charInfo.Name = recSecCmd;

                            //MELVIN 
                            /*if (!Database.isValidCharName(account.charInfo.Name))
                            {
                                //TODO add missing return packet
                                Logger.WriteLine("Charname is incorrect !");
                                closesocket(account.clientSocket);
                                break;
                            */
                            var sender1 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14};
                            var receiver1 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08};
                            var aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            var size = (UInt32) account.charInfo.Name.Length + (5*4) + (1*2) + (1*1);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0xadce0cda);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteUInt32(0);
                            aBuffer.WriteUInt32(0x03010000);
                            aBuffer.WriteByte(0);
                            aBuffer.WriteString(account.charInfo.Name);
                            aBuffer.Send(client);

                            var data = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x00, 0x1f, 0x08, 0x05, 0x10, 0x02, 0x18, 0x00, 0x22
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) account.charInfo.Name.Length + (10 + 1) + (UInt32) data.Length + (4*4) +
                                   (1*2) + (2*1);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0xa36d3b74);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(data);
                            aBuffer.WriteString("NicknameOk");
                            aBuffer.WriteByte(0x2a);
                            aBuffer.WriteString(account.charInfo.Name);
                            aBuffer.WriteUInt32(0x32040800);
                            aBuffer.WriteUInt16(0x1000);
                            
                            aBuffer.Send(client);

                            var data2 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xcf, 0xef, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00,
                                0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) data2.Length + (3*4);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0xf508f4c1);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(data2);
                            aBuffer.Send(client);

                            var data3 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x00, 0x24, 0x08, 0x05, 0x10, 0x02, 0x18, 0x00, 0x22, 0x14,
                                0x43, 0x68, 0x61, 0x72, 0x43, 0x72, 0x65, 0x61, 0x74, 0x69, 0x6f, 0x6e, 0x46, 0x69, 0x6e,
                                0x69, 0x73, 0x68, 0x65, 0x64,
                                0x2a, 0x00, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) data3.Length + (3*4);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0xa36d3b74);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(data3);
                            
                            aBuffer.Send(client);

                            var data4 = new byte[]
                            {
                                0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf6, 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00,
                                0x00, 0x00, 0x0a, 0x08, 0xc5, 0xc3, 0x02, 0x18, 0x01, 0x28, 0x18, 0x30, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) data4.Length + (1*4);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0x642cd3d6);
                            aBuffer.WriteArray(data4);
                            
                            aBuffer.Send(client);

                            var data5 = new byte[]
                            {
                                0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf7, 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00,
                                0x00, 0x00, 0x0a, 0x08, 0xc5, 0xc3, 0x02, 0x18, 0x01, 0x28, 0x18, 0x30, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) data5.Length + (1*4);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0x642cd3d6);
                            aBuffer.WriteArray(data5);
                            
                            aBuffer.Send(client);

                            var data6 = new byte[]
                            {
                                0x01, 0x00, 0x00, 0x00, 0x6a, 0x00, 0x00, 0x00, 0x14
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                            size = (UInt32) data6.Length + (3*4);
                            aBuffer.WriteUInt32(size);
                            aBuffer.WriteUInt32(0xf98e10b3);
                            aBuffer.WriteUInt32(0x0000c350);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(data6);
                            
                            aBuffer.Send(client);
                        }
                        else
                        {
                            Logger.WriteLine("Receive unknown command: {0} with value {1}", recCmd, recVal);
                        }
                        break;
                    }
                    case 0x4517aa81:
                    {
                        var dataobjdec = packet.Data.ReadUInt32();
                        var dataclientinst = packet.Data.ReadUInt32();
                        var movingType = packet.Data.ReadUInt16();
                        switch (movingType)
                        {
                            case 0x011a:
                            case 0x011e:
                            case 0x001e:
                            case 0x001a:
                            {
                                var charCoordX = packet.Data.ReadUInt32();
                                var charCoordY = packet.Data.ReadUInt32();
                                var charCoordZ = packet.Data.ReadUInt32();
                                var datadat = packet.Data.ReadArray(pbLength - ((6*4) + (1*2)));

                                var sender1 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
                                var receiver1 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

                                var aBuffer = new PacketBuffer();
                                aBuffer.WriteHeader(sender1, receiver1, null, 0x2000, true);
                                aBuffer.WriteUInt32(pbLength);
                                aBuffer.WriteUInt32(pbOpcode);
                                aBuffer.WriteUInt32(dataobjdec);
                                aBuffer.WriteUInt32(dataclientinst);
                                aBuffer.WriteUInt16(movingType);
                                aBuffer.WriteUInt32(charCoordX);
                                aBuffer.WriteUInt32(charCoordY);
                                aBuffer.WriteUInt32(charCoordZ);
                                //aBuffer.WriteArray(datadat, (pbLength - ((6 * 4) + (1 * 2))));
                                aBuffer.WriteArray(datadat.Take((int) pbLength - ((6*4) + (1*2))).ToArray());
                                
                                aBuffer.Send(client);
                                break;
                            }
                            default:
                            {
                                Logger.WriteLine(
                                   "Receive unknown movement Packet with opcode {0} and moving type: {1}:",
                                    packet.Opcode.ToHex(), movingType.ToHex());
                                break;
                            }
                        }
                        break;
                    }
                    case 0x864cfef8: //peer0_160
                    {
                        Logger.WriteLine("Ready for playScreen");
                        var sender1 = new byte[] {0x0d, 0x5d, 0xb9, 0xec, 0xa9, 0x10, 0x0b};
                        var receiver1 = new byte[] {0x0d, 0x91, 0xf7, 0x87, 0x8b, 0x10, 0xe6, 0x8f, 0x80, 0x08};
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

                        var aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender1, receiver1, null, 0x2024, true);
                        aBuffer.WriteArray(blobData1);
                        aBuffer.Send(client);

                        var sender2 = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
                        var receiver2 = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};
                        var blobData2 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x0e, 0x06, 0xec, 0x12, 0x55, 0x00, 0x00, 0xc3, 0x50
                        };
                        var blobData2_1 = new byte[]
                        {
                            0x00, 0x09
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteArray(blobData2);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteArray(blobData2_1);
                        aBuffer.Send(client);

                        var blobData3 = new byte[]
                        {
                            0x00, 0x00, 0x00, 0x0d, 0x86, 0x4c, 0xfe, 0xf8, 0x00, 0x00, 0xc3, 0x50
                        };
                        aBuffer = new PacketBuffer();
                        aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                        aBuffer.WriteArray(blobData3);
                        aBuffer.WriteUInt32(account.nClientInst);
                        aBuffer.WriteByte(0);
                        aBuffer.Send(client);

                        if (account.charInfo.Level == 0)
                        {
                            var blobData7 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x89, 0xf5, 0x08, 0xf4, 0xc1, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData7_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xd0, 0x0c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x39, 0xbf, 0x93
                                //0x00, 0x4d, 
                                //0x4d, 0x61, 0x6c, 0x65, 0x54, 0x65, 0x6d, 0x70, 0x6c, 0x61, 0x74, 0x65, 0x3d, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3a, 0x31, 0x30, 0x32, 0x33, 0x32, 0x3b, 0x46, 0x65, 0x6d, 0x61, 0x6c, 0x65, 0x54, 0x65, 0x6d, 0x70, 0x6c, 0x61, 0x74, 0x65, 0x3d, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3a, 0x31, 0x30, 0x32, 0x33, 0x33, 0x3b, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6e, 0x53, 0x6c, 0x61, 0x76, 0x65, 0x72, 0x3d, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3a, 0x31, 0x30, 0x32, 0x33, 0x34, 
                                //0x00, 0x00, 0x00, 0x00, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData7);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData7_1);
                            aBuffer.WriteString(
                               "MaleTemplate=50000:10232;FemaleTemplate=50000:10233;StygianSlaver=50000:10234");
                            aBuffer.WriteByte(0);
                            aBuffer.WriteUInt32(0);
                            aBuffer.Send(client);

                            var blobData8 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x89, 0xf5, 0x08, 0xf4, 0xc1, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData8_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xd0, 0x0c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x01, 0x9d, 0xd1, 0x00, 0x4d, 0x4d, 0x61,
                                0x6c, 0x65, 0x54, 0x65, 0x6d, 0x70, 0x6c, 0x61, 0x74, 0x65, 0x3d, 0x35, 0x30, 0x30, 0x30,
                                0x30, 0x3a, 0x31, 0x30, 0x32, 0x33, 0x32, 0x3b, 0x46, 0x65, 0x6d, 0x61, 0x6c, 0x65, 0x54,
                                0x65, 0x6d, 0x70, 0x6c, 0x61, 0x74, 0x65, 0x3d, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3a, 0x31,
                                0x30, 0x32, 0x33, 0x33, 0x3b, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6e, 0x53, 0x6c, 0x61,
                                0x76, 0x65, 0x72, 0x3d, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3a, 0x31, 0x30, 0x32, 0x33, 0x34,
                                0x00, 0x00, 0x00, 0x00, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData8);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData8_1);
                            aBuffer.Send(client);
                            //*/

                            var blobData4 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x15, 0xf9, 0x8e, 0x10, 0xb3, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData4_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x03, 0x60, 0x00, 0x00, 0x00, 0x02
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData4);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData4_1);
                            aBuffer.Send(client);

                            var blobData5 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x4c, 0x6b, 0x87, 0xe3, 0x16, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData5_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x0b, 0x90, 0x00, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData5_2 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x27, 0x10,
                                0x00, 0x01,
                                0x00, 0x00, 0x00, 0x01,
                                0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x0b, 0x90,
                                0x00, 0x00, 0x00, 0x01,
                                0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x00, 0x00,
                                0x00, 0x00, 0x00, 0x01,
                                0x00, 0x00, 0x00, 0x00,
                                0x02, 0x3c, 0x4b, 0xa3
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData5);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData5_1);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData5_2);
                            aBuffer.Send(client);

                            var blobData6 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x11, 0x40, 0x74, 0x9b, 0x8a, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData6_1 = new byte[]
                            {
                                0x01, 0x00, 0x00, 0x00, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData6);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData6_1);
                            aBuffer.Send(client);


                            var blobData9 = new byte[]
                            {
                                0x00, 0x00, 0x03, 0x06, 0xa3, 0x6d, 0x3b, 0x74, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData9_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x02, 0xf5, 0x08, 0x05, 0x10, 0x07, 0x18, 0x00, 0x22, 0xb9, 0x01, 0x3c,
                                0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x74, 0x6f,
                                0x6b, 0x65, 0x6e, 0x3d, 0x22, 0x70, 0x6c, 0x61, 0x79, 0x6f, 0x72, 0x73, 0x6b, 0x69, 0x70,
                                0x28, 0x37, 0x32, 0x29, 0x7c, 0x6f, 0x6e, 0x63, 0x6c, 0x69, 0x65, 0x6e, 0x74, 0x63, 0x6f,
                                0x6e, 0x6e, 0x65, 0x63, 0x74, 0x65, 0x64, 0x28, 0x31, 0x29, 0x7c, 0x4e, 0x45, 0x57, 0x5f,
                                0x63, 0x68, 0x61, 0x72, 0x61, 0x63, 0x74, 0x65, 0x72, 0x5f, 0x63, 0x72, 0x65, 0x61, 0x74,
                                0x69, 0x6f, 0x6e, 0x20, 0x53, 0x6c, 0x61, 0x76, 0x65, 0x20, 0x47, 0x61, 0x6c, 0x6c, 0x65,
                                0x79, 0x20, 0x28, 0x73, 0x65, 0x72, 0x76, 0x65, 0x72, 0x2d, 0x73, 0x69, 0x64, 0x65, 0x29,
                                0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x30,
                                0x32, 0x33, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x26, 0x71, 0x75, 0x6f, 0x74, 0x3b,
                                0x42, 0x5d, 0x53, 0x5a, 0x2e, 0x41, 0x47, 0x52, 0x47, 0x72, 0x41, 0x79, 0x4f, 0x47, 0x65,
                                0x2a, 0x42, 0x47, 0x2d, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30,
                                0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72,
                                0x6d, 0x61, 0x74, 0x3e, 0x2a, 0xa6, 0x04, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a,
                                0x37, 0x39, 0x33, 0x37, 0x31, 0x33, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a,
                                0x37, 0x39, 0x33, 0x37, 0x31, 0x34, 0x3b, 0x31, 0x30, 0x37, 0x30, 0x30, 0x30, 0x33, 0x3a,
                                0x34, 0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x39, 0x37, 0x3a, 0x34, 0x30,
                                0x30, 0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x33, 0x30, 0x3a, 0x31, 0x35, 0x36, 0x32,
                                0x31, 0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x33, 0x30, 0x3a, 0x32, 0x33, 0x39, 0x34,
                                0x34, 0x38, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x33, 0x30, 0x3a, 0x35, 0x36, 0x35, 0x39,
                                0x31, 0x33, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x37, 0x32, 0x38,
                                0x35, 0x33, 0x33, 0x3b, 0x31, 0x30, 0x36, 0x30, 0x36, 0x36, 0x38, 0x3a, 0x34, 0x30, 0x30,
                                0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x35, 0x39, 0x3a, 0x34, 0x30, 0x30, 0x30, 0x3b,
                                0x31, 0x30, 0x36, 0x30, 0x36, 0x36, 0x37, 0x3a, 0x31, 0x36, 0x37, 0x33, 0x36, 0x38, 0x3b,
                                0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x30, 0x37, 0x35, 0x34, 0x34, 0x35,
                                0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x31, 0x38, 0x30, 0x30, 0x32,
                                0x35, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x32, 0x32, 0x31, 0x32, 0x38,
                                0x35, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x36, 0x36, 0x33, 0x31,
                                0x39, 0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x32, 0x30, 0x38, 0x39,
                                0x32, 0x33, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x32, 0x30, 0x38, 0x32,
                                0x36, 0x36, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x32, 0x32, 0x36, 0x38,
                                0x39, 0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x36, 0x36, 0x33,
                                0x31, 0x39, 0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x36, 0x35,
                                0x36, 0x31, 0x30, 0x39, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x33, 0x30, 0x3a, 0x32, 0x31,
                                0x30, 0x30, 0x36, 0x39, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x31, 0x3a, 0x32, 0x31,
                                0x30, 0x30, 0x37, 0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33, 0x30,
                                0x37, 0x35, 0x34, 0x35, 0x32, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a, 0x33,
                                0x30, 0x37, 0x35, 0x34, 0x35, 0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x30, 0x30, 0x34, 0x3a,
                                0x33, 0x30, 0x37, 0x35, 0x34, 0x35, 0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x33, 0x30, 0x30,
                                0x3a, 0x34, 0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x34, 0x39, 0x3a, 0x34,
                                0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x36, 0x36, 0x36, 0x31, 0x31, 0x3a, 0x34, 0x30, 0x30,
                                0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x31, 0x33, 0x3a, 0x34, 0x30, 0x30, 0x30, 0x30,
                                0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x31, 0x35, 0x3a, 0x34, 0x30, 0x30,
                                0x30, 0x30, 0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x35, 0x30, 0x36, 0x3a, 0x34,
                                0x30, 0x30, 0x30, 0x3b, 0x31, 0x30, 0x30, 0x30, 0x30, 0x35, 0x38, 0x3a, 0x34, 0x30, 0x30,
                                0x30, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x32, 0x30, 0x37, 0x3a, 0x31, 0x31, 0x30, 0x30, 0x30,
                                0x36, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x32, 0x30, 0x37, 0x3a, 0x32, 0x36, 0x32, 0x32, 0x34,
                                0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x32, 0x30, 0x37, 0x3a, 0x35, 0x36, 0x38, 0x38, 0x30,
                                0x36, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x32, 0x30, 0x37, 0x3a, 0x33, 0x34, 0x32, 0x30, 0x33,
                                0x33, 0x31, 0x3b, 0x31, 0x30, 0x31, 0x30, 0x32, 0x30, 0x37, 0x3a, 0x31, 0x36, 0x37, 0x31,
                                0x38, 0x33, 0x32, 0x08, 0x08, 0x95, 0xb1, 0x27, 0x10, 0x99, 0xf7, 0x31
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData9);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData9_1);
                            aBuffer.Send(client);


                            var blobData10 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x2b, 0xa3, 0x6d, 0x3b, 0x74, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData10_1 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x00, 0x1a, 0x08, 0x05, 0x10, 0x02, 0x18, 0x00, 0x22, 0x0a, 0x50, 0x6c,
                                0x61, 0x79, 0x4f, 0x72, 0x53, 0x6b, 0x69, 0x70, 0x2a, 0x00, 0x32, 0x04, 0x08, 0x00, 0x10,
                                0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData10);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteArray(blobData10_1);
                            aBuffer.Send(client);


                            var blobData11 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x28, 0xba, 0xdf, 0x5a, 0x4b,
                                0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf8,
                                0x00, 0x00, 0x10, 0x2a, 0x68, 0x61, 0x69, 0x72, 0x6d, 0x65, 0x73, 0x68, 0x5f, 0x32, 0x30,
                                0x37, 0x39, 0x32, 0x30, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData11);
                            aBuffer.Send(client);


                            var blobData12 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x28, 0xba, 0xdf, 0x5a, 0x4b, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27,
                                0xf9, 0x00, 0x00, 0x10, 0x2a, 0x68, 0x61, 0x69, 0x72, 0x6d, 0x65, 0x73, 0x68, 0x5f, 0x32,
                                0x30, 0x33, 0x36, 0x36, 0x36, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData12);
                            aBuffer.Send(client);


                            var blobData13 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x20, 0x64, 0x2c, 0xd3, 0xd6,
                                0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf8,
                                0x00, 0x04, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x0a, 0x08, 0xd3, 0x9e, 0x05, 0x18,
                                0x02, 0x28, 0x18, 0x30, 0x00
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData13);
                            aBuffer.Send(client);


                            var blobData14 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x29, 0xba, 0xdf, 0x5a, 0x4b, 0x00, 0x00, 0xc3, 0x50
                            };
                            var blobData14_1 = new byte[]
                            {
                                //0x00, 
                                //0x00, 0x11, 
                                //0x2a, 0x74, 0x65, 0x78, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x5f, 0x31, 0x38, 0x33, 0x38, 0x37, 0x35, 
                                0x3f, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData14);
                            aBuffer.WriteUInt32(account.nClientInst);
                            aBuffer.WriteByte(0);
                            aBuffer.WriteString("*texscript_183875");
                            aBuffer.WriteArray(blobData14_1);
                            aBuffer.Send(client);


                            var blobData15 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x29, 0xba, 0xdf, 0x5a, 0x4b, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27,
                                0xf9, 0x00, 0x00,
                                0x11, 0x2a, 0x74, 0x65, 0x78, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x5f, 0x31, 0x38, 0x33,
                                0x38, 0x37, 0x35, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData15);
                            aBuffer.Send(client);


                            var blobData16 = new byte[]
                            {
                                0x00, 0x00, 0x00, 0x29, 0xba, 0xdf, 0x5a, 0x4b, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27,
                                0xf8, 0x00, 0x00,
                                0x11, 0x2a, 0x74, 0x65, 0x78, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x5f, 0x31, 0x38, 0x33,
                                0x38, 0x37, 0x35, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02
                            };
                            aBuffer = new PacketBuffer();
                            aBuffer.WriteHeader(sender2, receiver2, null, 0x2000, true);
                            aBuffer.WriteArray(blobData16);
                            aBuffer.Send(client);
                        }
                        //*/
                        break;
                    }
                    case 0x642CD3D6:
                    {
                        var cInstPreId = packet.Data.ReadUInt32();
                        var clientInst = packet.Data.ReadUInt32();
                        var unk0 = packet.Data.ReadUInt16();
                        var unk1 = packet.Data.ReadUInt32();
                        var unklength0 = packet.Data.ReadUInt32();
                        var unkdata = packet.Data.ReadArray(unklength0);
                        var objPreId = packet.Data.ReadUInt32();
                        var objId = packet.Data.ReadUInt32();
                        var unk2 = packet.Data.ReadUInt32();

                        if (objId == 0x00004cc9 && unk2 == 6)
                        {
                            //MELVIN GTable->account.charInfo.map = 500;

                            //MELVIN teleportChar("GM Island", GTable, clientList);
                        }
                        break;
                    }
                case 0x6B6FD368:
                    {
                        Logger.WriteLine("Received close game! (x button)");
                        break;
                    }
                    default:
                    {
                        Logger.WriteLine("Unknown data Opcode: " + pbOpcode.ToHex());
                        break;
                    }
                }
            }
        }
    }
}