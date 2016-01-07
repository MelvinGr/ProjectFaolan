using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Extentions;

namespace Faolan.Core.Network
{
    public static class PacketUtills
    {
        private static readonly Dictionary<string, string> Senders = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> Receivers = new Dictionary<string, string>();

        public static string ChainedPacketToCsCode(ConanPacket packet, int id)
        {
            var hasHeaderData = packet.HeaderData != null && packet.HeaderData.Length > 0;
            var headerDataName = hasHeaderData ? "headerdata" + id : "null";

            var addSenderData = false;
            string senderName;
            var senderData = packet.SenderInt.ToHex();
            if (Senders.ContainsValue(senderData))
            {
                senderName = Senders.FirstOrDefault(s => s.Value == senderData).Key;
            }
            else
            {
                senderName = "sender" + Senders.Count;
                Senders[senderName] = senderData;
                addSenderData = true;
            }

            var addReceiverData = false;
            var receiverName = "";
            var receiverData = packet.ReceiverInt.ToHex();
            if (Receivers.ContainsValue(receiverData))
            {
                receiverName = Receivers.FirstOrDefault(s => s.Value == receiverData).Key;
            }
            else
            {
                receiverName = "receiver" + Receivers.Count;
                Receivers[receiverName] = receiverData;
                addReceiverData = true;
            }

            var packetDataName = "packetData" + id;

            return
                (addSenderData ? $"var {senderName} = new byte[]{senderData};\r\n" : "") +
                (addReceiverData ? $"var {receiverName} = new byte[]{receiverData};\r\n" : "") +
                (hasHeaderData ? $"var {headerDataName} = new byte[]{packet.HeaderData.ToArray().ToHex()};\r\n" : "") +
                (packet.Data != null ? $"var {packetDataName} = new byte[]{packet.Data.ToArray().ToHex()};\r\n" : "") +
                $"new PacketBuffer()\r\n\t.WriteHeader({senderName}, {receiverName}, {headerDataName}, {packet.Opcode.ToHex()}, true)\r\n" +
                (packet.Data != null ? $"\t.WriteArray({packetDataName})\r\n" : "") +
                "\t.Send(client);\r\n";
        }

        public static string PacketToString(ConanPacket packet)
        {
            return
                $"UInt32\tlength\t\t=\t{packet.Length}\r\n" +
                $"UInt32\tcrc32\t\t=\t0x{packet.Crc32:X4}\r\n" +
                $"UInt32\theadersize\t=\t{packet.Headersize}\r\n" +
                $"byte\tsender\t\t=\t0x{packet.Sender:X}\r\n" +
                $"byte\tslength\t\t=\t{packet.SenderInt.Length}\r\n" +
                $"byte[]\tsenderInt\t=\t{packet.SenderInt.ToHex()}\r\n" +
                $"byte\treceiver\t=\t0x{packet.Receiver:X}\r\n" +
                $"byte\trlength\t\t=\t{packet.ReceiverInt.Length}\r\n" +
                $"byte[]\treceiverInt\t=\t{packet.ReceiverInt.ToHex()}\r\n" +
                $"UInt32\topcode\t\t=\t0x{packet.Opcode:X8}\r\n" +
                (packet.HeaderData?.Length > 0
                    ? $"UInt32\thdrDataLen\t=\t{packet.HeaderData.Length}\r\n" +
                      $"byte[]\theaderData\t=\t{packet.HeaderData.ToArray().ToHex()}\r\n"
                    : "") +
                (packet.Data?.Length > 0
                    ? $"UInt32\tdataLength\t=\t{packet.Data.Length}\r\n" +
                      $"byte[]\tdata\t\t=\t{packet.Data.ToArray().ToHex()}\r\n"
                    : "");
        }

        public static string PacketToCsCode(ConanPacket packet, string comment = null)
        {
            return
                (comment != null ? "// " + comment + "\r\n" : "") +
                (packet.HeaderData != null && packet.HeaderData.Length > 0
                    ? $"var headerData = new byte[]{packet.HeaderData.ToArray().ToHex()};\r\n"
                    : "") +
                $"var sender = new byte[]{packet.SenderInt.ToHex()};\r\n" +
                $"var receiver = new byte[]{packet.ReceiverInt.ToHex()};\r\n" +
                (packet.Data != null ? $"var packetData = new byte[]{packet.Data.ToArray().ToHex()};\r\n" : "") +
                "new PacketBuffer()\r\n\t.WriteHeader(sender, receiver, "
                + $"{(packet.HeaderData?.Length > 0 ? "headerData" : "null")}, {packet.Opcode.ToHex()}, true)\r\n" +
                (packet.Data != null ? "\t.WriteArray(packetData)\r\n" : "") +
                ".Send(client);\r\n";
        }

        public static void PacketDataToFile(ConanPacket packet, string path)
        {
            File.WriteAllBytes(path, packet.Data.ToArray());
        }
    }
}