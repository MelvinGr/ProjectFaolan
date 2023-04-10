using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network
{
	public static class PacketUtils
	{
		private static readonly Dictionary<string, string> Senders = new();
		private static readonly Dictionary<string, string> Receivers = new();

		public static string ChainedPacketToCsCode(ConanPacket packet, uint id)
		{
			var hasHeaderData = packet.HeaderData != null && packet.HeaderData.Length > 0;
			var headerDataName = hasHeaderData ? $"headerdata{id}" : "null";

			string senderName;
			var addSenderData = false;

			var senderData = packet.SenderBytes.ToHex();
			if (Senders.ContainsValue(senderData))
				senderName = Senders.FirstOrDefault(s => s.Value == senderData).Key;
			else
			{
				senderName = $"sender{Senders.Count}";
				Senders[senderName] = senderData;
				addSenderData = true;
			}

			string receiverName;
			var addReceiverData = false;

			var receiverData = packet.ReceiverBytes.ToHex();
			if (Receivers.ContainsValue(receiverData))
				receiverName = Receivers.FirstOrDefault(s => s.Value == receiverData).Key;
			else
			{
				receiverName = $"receiver{Receivers.Count}";
				Receivers[receiverName] = receiverData;
				addReceiverData = true;
			}

			return
				(addSenderData ? $"var {senderName} = new byte[]{senderData};\r\n" : "") +
				(addReceiverData ? $"var {receiverName} = new byte[]{receiverData};\r\n" : "") +
				(hasHeaderData ? $"var {headerDataName} = new byte[]{packet.HeaderData.ToArray().ToHex()};\r\n" : "") +
				(packet.Data != null ? $"var packetData{id} = new byte[]{packet.Data.ToArray().ToHex()};\r\n" : "") +
				$"new PacketStream()\r\n\t.WriteHeader({senderName}, {receiverName}, {headerDataName}, {packet.Opcode.ToHex()}, true)\r\n" +
				(packet.Data != null ? $"\t.WriteArray(packetData{id})\r\n" : "") +
				"\t.Send(client);\r\n";
		}

		public static string PacketToString(ConanPacket packet)
		{
			return
				$"UInt32\tlength\t\t=\t{packet.Length}\r\n" +
				$"UInt32\tcrc32\t\t=\t0x{packet.Crc32:X4}\r\n" +
				$"UInt32\theaderSize\t=\t{packet.HeaderLength}\r\n" +
				$"byte\tsender\t\t=\t0x{packet.Sender:X}\r\n" +
				$"byte\tsLength\t\t=\t{packet.SenderBytes.Length}\r\n" +
				$"byte[]\tsenderInt\t=\t{packet.SenderBytes.ToHex()}\r\n" +
				$"byte\treceiver\t=\t0x{packet.Receiver:X}\r\n" +
				$"byte\trLength\t\t=\t{packet.ReceiverBytes.Length}\r\n" +
				$"byte[]\treceiverInt\t=\t{packet.ReceiverBytes.ToHex()}\r\n" +
				$"UInt32\topcode\t\t=\t0x{packet.Opcode:X8}\r\n" +
				(packet.HeaderData?.Length > 0
					? $"UInt32\thdrDataLen\t=\t{packet.HeaderData.Length}\r\n" +
					  $"byte[]\theaderData\t=\t{packet.HeaderData.ToArray().ToHex()}\r\n"
					: "") +
				(packet.Data?.Length > 0
					? $"UInt32\tdataLength\t=\t{packet.Data.Length}\r\n" +
					  $"byte[]\tdata\t\t=\t{packet.Data.ToArray().ToHex()}\r\n"
					: "") +
				""; //"$"byte[]\tremainingData\t=\t{packet.RemainingData.ToHex()}\r\n";
		}

		public static string PacketToCsCode(ConanPacket packet, long? index = null, bool useIndexForSr = true)
		{
			var indexStr = useIndexForSr ? index?.ToString() : null;
			var indexStr2 = index?.ToString();

			return
				//(comment != null ? "// " + comment + "\r\n" : "") +
				(packet.HeaderData != null && packet.HeaderData.Length > 0
					? $"var headerData{indexStr} = new byte[]{packet.HeaderData.ToArray().ToHex()};\r\n"
					: "") +
				$"var sender{indexStr} = new byte[]{packet.SenderBytes.ToHex()};\r\n" +
				$"var receiver{indexStr} = new byte[]{packet.ReceiverBytes.ToHex()};\r\n" +
				(packet.Data != null
					? $"var packetData{indexStr2} = new byte[]{packet.Data.ToArray().ToHex()};\r\n"
					: "") +
				$"new PacketStream()\r\n\t.WriteHeader(sender{indexStr}, receiver{indexStr}, "
				+ $"{(packet.HeaderData?.Length > 0 ? $"headerData{indexStr}" : "null")}, {packet.Opcode.ToHex()}, true)\r\n" +
				(packet.Data != null ? $"\t.WriteArray(packetData{indexStr2})\r\n" : "") +
				".Send(client);\r\n";
		}

		public static void PacketDataToFile(ConanPacket packet, string path)
		{
			File.WriteAllBytes(path, packet.Data.ToArray());
		}
	}
}
