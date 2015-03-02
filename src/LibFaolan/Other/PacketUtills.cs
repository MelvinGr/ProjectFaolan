using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace LibFaolan.Other
{
    public static class PacketUtills
    {
        private static readonly Dictionary<string, string> _senders = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _receivers = new Dictionary<string, string>();

        public static string ChainedPacketToCsCode(Packet packet, int id)
        {
            var hasHeaderData = (packet.HeaderData != null && packet.HeaderData.Length > 0);
            var headerDataName = (hasHeaderData ? "headerdata" + id : "null");

            var addSenderData = false;
            var senderName = "";
            var senderData = packet.SenderInt.Data.ToHexString();
            if (_senders.ContainsValue(senderData))
                senderName = _senders.FirstOrDefault(s => s.Value == senderData).Key;
            else
            {
                senderName = "sender" + _senders.Count;
                _senders[senderName] = senderData;
                addSenderData = true;
            }

            var addReceiverData = false;
            var receiverName = "";
            var receiverData = packet.ReceiverInt.Data.ToHexString();
            if (_receivers.ContainsValue(receiverData))
                receiverName = _receivers.FirstOrDefault(s => s.Value == receiverData).Key;
            else
            {
                receiverName = "receiver" + _receivers.Count;
                _receivers[receiverName] = receiverData;
                addReceiverData = true;
            }

            var packetDataName = "packetData" + id;

            var var =
                (addSenderData ? "var " + senderName + " = new byte[]" + senderData + ";\r\n" : "") +
                (addReceiverData ? "var " + receiverName + " = new byte[]" + receiverData + ";\r\n" : "") +
                (hasHeaderData
                    ? "var " + headerDataName + " = new byte[]" +
                      packet.HeaderData.ToArray().ToHexString() + ";\r\n"
                    : "") +
                (packet.Data != null
                    ? "var " + packetDataName + " = new byte[]" +
                      packet.Data.ToArray().ToHexString() + ";"
                    : "") + "\r\n" +
                "new PacketBuffer()" + "\r\n" +
                "    .WriteHeader(" + senderName + ", " + receiverName + ", " + headerDataName + ", " +
                packet.Opcode.ToHex() + ", true)\r\n" +
                (packet.Data != null ? "    .WriteArray(" + packetDataName + ")\r\n" : "") +
                "    .Send(client);\r\n";

            return var;
        }

        public static string PacketToString(Packet packet)
        {
            var var =
                string.Format("UInt32\tlength\t\t=\t{0}\r\n", packet.Length) +
                string.Format("UInt32\tcrc32\t\t=\t0x{0:X4}\r\n", packet.Crc32) +
                string.Format("UInt32\theadersize\t=\t{0}\r\n", packet.Headersize) +
                string.Format("byte\tsender\t\t=\t0x{0:X}\r\n", packet.Sender) +
                string.Format("byte\tslength\t\t=\t{0}\r\n", packet.SenderInt.Length) +
                string.Format("byte[]\tsenderInt\t=\t{0}\r\n", packet.SenderInt.Data.ToHexString()) +
                string.Format("byte\treceiver\t=\t0x{0:X}\r\n", packet.Receiver) +
                string.Format("byte\trlength\t\t=\t{0}\r\n", packet.ReceiverInt.Length) +
                string.Format("byte[]\treceiverInt\t=\t{0}\r\n", packet.ReceiverInt.Data.ToHexString()) +
                string.Format("UInt32\topcode\t\t=\t0x{0:X8}\r\n", packet.Opcode) +
                string.Format("UInt32\thdrDataLen\t=\t{0}\r\n", packet.HeaderData?.Length) +
                string.Format("byte[]\theaderData\t=\t{0}\r\n",
                    packet.HeaderData != null ? packet.HeaderData.ToArray().ToHexString() : "") +
                string.Format("UInt32\tdataLength\t=\t{0}\r\n", packet.Data?.Length) +
                string.Format("byte[]\tdata\t\t=\t{0}\r\n",
                    packet.Data != null ? packet.Data.ToArray().ToHexString() : "");

            return var;
        }

        public static string PacketToCsCode(Packet packet, string comment = null)
        {
            var var =
                (comment != null ? "// " + comment + "\r\n" : "") +
                (packet.HeaderData != null && packet.HeaderData.Length > 0
                    ? "var headerData = new byte[]" + packet.HeaderData.ToArray().ToHexString() + ";\r\n"
                    : "") +
                "var sender = new byte[]" + packet.SenderInt.Data.ToHexString() + ";\r\n" +
                "var receiver = new byte[]" + packet.ReceiverInt.Data.ToHexString() + ";\r\n" +
                (packet.Data != null
                    ? "var packetData = new byte[]" + packet.Data.ToArray().ToHexString() + ";\r\n"
                    : "") + "\r\n" +
                "new PacketBuffer()" + "\r\n" +
                "    .WriteHeader(sender, receiver, " +
                (packet.HeaderData != null && packet.HeaderData.Length > 0
                    ? "headerData"
                    : "null") + ", "
                + packet.Opcode.ToHex() + ", true)\r\n" +
                (packet.Data != null
                    ? "    .WriteArray(packetData)\r\n"
                    : "") +
                "    .Send(client);\r\n";

            return var;
        }

        public static void PacketDataToFile(Packet packet, string path)
        {
            File.WriteAllBytes(path, packet.Data.ToArray());
        }
    }
}