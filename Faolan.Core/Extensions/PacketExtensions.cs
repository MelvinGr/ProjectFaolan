using System.IO;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;

namespace Faolan.Core.Extensions
{
	public static class PacketExtensions
	{
		extension(ConanPacket packet)
		{
			public void PacketDataToFile(string path)
			{
				File.WriteAllBytes(path, packet.Body.ToArray());
			}
			/*public string PacketToString()
			{
				return $"UInt32\tlength\t\t=\t{packet.Length}\r\n" + $"UInt32\tcrc32\t\t=\t0x{packet.Crc32:X4}\r\n" + $"UInt32\theaderSize\t=\t{packet.HeaderLength}\r\n" + $"byte\tsender\t\t=\t0x{packet.Sender:X}\r\n" +
				       $"byte\tsLength\t\t=\t{packet.SenderBytes.Length}\r\n" + $"byte[]\tsenderInt\t=\t{packet.SenderBytes.ToHex()}\r\n" + $"byte\treceiver\t=\t0x{packet.Receiver:X}\r\n" +
				       $"byte\trLength\t\t=\t{packet.ReceiverBytes.Length}\r\n" + $"byte[]\treceiverInt\t=\t{packet.ReceiverBytes.ToHex()}\r\n" + $"UInt32\topcode\t\t=\t0x{packet.Opcode:X8}\r\n" + (packet.HeaderData?.Length > 0
					       ? $"UInt32\thdrDataLen\t=\t{packet.HeaderData.Length}\r\n" + $"byte[]\theaderData\t=\t{packet.HeaderData.ToArray().ToHex()}\r\n"
					       : "") + (packet.Data?.Length > 0
					       ? $"UInt32\tdataLength\t=\t{packet.Data.Length}\r\n" + $"byte[]\tdata\t\t=\t{packet.Data.ToArray().ToHex()}\r\n"
					       : "") + ""; //"$"byte[]\tremainingData\t=\t{packet.RemainingData.ToHex()}\r\n";
			}*/

			public string PacketToCsCode(string index = null, bool useIndexForSr = true)
			{
				if (packet == null)
					return "";

				var headerData = packet.HeaderData?.Length > 0
					? packet.HeaderData.ToHex(true)
					: "null";

				// TODO: FIX THIS (dont treat as byte[])
				return $"new PacketStream()\r\n\t.WriteHeader({packet.SenderBytes.ToArray().ToHex(true)}, {packet.ReceiverBytes.ToArray().ToHex(true)}, true, {packet.Opcode.ToHex()}, {headerData})\r\n" + (packet.Body != null
					? $"\t.WriteArray({packet.Body.ToArray().ToHex(true)})\r\n"
					: "") + ".Send(client);\r\n";
			}
		}

		extension(AgentServerPacket packet)
		{
			public string PacketToCsCode()
			{
				if (packet == null)
					return "";

				return $"new FaolanStream()\r\n\t.WriteArray({packet.Bytes.ToHex(true)})\r\n\t.Send(client);\r\n";
			}
		}
	}
}
