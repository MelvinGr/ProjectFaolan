using System;
using Faolan.PacketAnalyzer;
using Faolan.PacketAnalyzer.Models;
using Faolan.PacketAnalyzer.ViewModels;
using Xunit;

namespace Faolan.Tests
{
	public class PacketDumpFileServiceTests
	{
		private readonly PacketDumpFileService _service = new PacketDumpFileService();

		[Fact]
		public void Deserialize_RejectsLegacyPacketWrapperArrays()
		{
			const string json = "[]";

			Assert.ThrowsAny<Exception>(() => _service.Deserialize(json, "legacy.json"));
		}

		[Fact]
		public void Serialize_AndDeserialize_LengthPrefixedPackets_DoesNotCrash()
		{
			var packet = AnalyzedPacket.FromBytes("638741156624558160_4", "37.18.192.43:7002", PacketProtocolInfo.CsPlayerAgentProtocolName, false, [
				0x00, 0x00, 0x00, 0x08,
				0x00, 0x00, 0x20, 0x00,
				0x01, 0x02, 0x03, 0x04
			], "capture.pcapng", "192.168.1.10:5000", "37.18.192.43:7002", "192.168.1.10", "37.18.192.43", 5000, 7002);

			var json = _service.Serialize("capture.pcapng", new PacketAnalyzerPortSettings(7010, 7011, 7002, 7013, 7012), [packet]);
			var loadResult = _service.Deserialize(json, "fallback.json");

			var roundTrippedPacket = Assert.Single(loadResult.PacketRows);
			Assert.Equal(PacketProtocolInfo.CsPlayerAgentProtocolName, roundTrippedPacket.ProtocolName);
			Assert.Equal(packet.Bytes, roundTrippedPacket.Bytes);
		}

		[Fact]
		public void Serialize_AndDeserialize_RoundTripsVersionedDocument()
		{
			var packet = CreatePacketWrapper("638741156624558160_3", 0x2001, [0x01, 0x02, 0x03]);
			packet.Notes = "important";

			var settings = new PacketAnalyzerPortSettings(7010, 7011, 7001, 7013, 7012);
			var json = _service.Serialize("capture.pcapng", settings, [packet]);
			var loadResult = _service.Deserialize(json, "fallback.json");

			Assert.Equal(PacketDumpFileService.CurrentSchemaVersion, loadResult.SchemaVersion);
			Assert.Equal("capture.pcapng", loadResult.CaptureName);
			Assert.Equal(settings.GameServerPort, loadResult.PortSettings.GameServerPort);

			var roundTrippedPacket = Assert.Single(loadResult.PacketRows);
			Assert.Equal(packet.Ticks, roundTrippedPacket.Ticks);
			Assert.Equal(packet.ServiceName, roundTrippedPacket.ServiceName);
			Assert.Equal(packet.ProtocolName, roundTrippedPacket.ProtocolName);
			Assert.Equal(packet.Notes, roundTrippedPacket.Notes);
			Assert.Equal(packet.Bytes, roundTrippedPacket.Bytes);
			Assert.Equal(packet.SourceAddress, roundTrippedPacket.SourceAddress);
			Assert.Equal(packet.DestinationAddress, roundTrippedPacket.DestinationAddress);
		}

		private static PacketRowViewModel CreatePacketWrapper(string ticks, ushort opcode, byte[] body)
		{
			var packet = TestPacketFactory.CreateAnalyzedPacket(ticks, "37.18.192.43:7010", PacketProtocolInfo.UniverseAgentProtocolName, false, opcode, body);
			return new PacketRowViewModel(packet);
		}
	}
}
