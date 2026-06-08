using Faolan.Core.Network.Packets;
using Faolan.PacketAnalyzer.Models;

namespace Faolan.Tests
{
	internal static class TestPacketFactory
	{
		public static byte[] BuildPacketBytes(ushort opcode, byte[] body)
		{
			var stream = new PacketStream();
			stream.WriteHeader(CreateEndpointMessage(1), CreateEndpointMessage(2), opcode);
			stream.WriteArray(body);
			stream.WriteLengthHash();
			return stream.ToArray();
		}

		public static AnalyzedPacket CreateAnalyzedPacket(string ticks, string serviceName, string protocolName, bool isDownload, ushort opcode, byte[] body, string sourceAddress = "192.168.1.10:5000",
			string destinationAddress = "37.18.192.43:7010", string sourceIpAddress = "192.168.1.10", string destinationIpAddress = "37.18.192.43", ushort sourcePort = 5000, ushort destinationPort = 7010,
			string sourceName = "capture.pcapng")
		{
			return AnalyzedPacket.FromBytes(ticks, serviceName, protocolName, isDownload, BuildPacketBytes(opcode, body), sourceName, sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort,
				destinationPort);
		}

		private static ProtoMessage CreateEndpointMessage(uint endpointId)
		{
			var message = new ProtoMessage();
			message.Fields.Add(new ProtoField
			{
				FieldNumber = 1,
				WireType = 0,
				VarintValue = endpointId
			});
			return message;
		}
	}
}
