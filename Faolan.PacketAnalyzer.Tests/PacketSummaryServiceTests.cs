using Faolan.PacketAnalyzer.Models;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	public class PacketSummaryServiceTests
	{
		[Fact]
		public void Compute_ReturnsExpectedCounts_ForMixedPackets()
		{
			var service = new PacketSummaryService();
			AnalyzedPacket[] packets =
			[
				TestPacketFactory.CreateAnalyzedPacket("1_0", "37.18.192.43:7010", PacketProtocolInfo.UniverseAgentProtocolName, false, 0x2000, [0x01]),
				TestPacketFactory.CreateAnalyzedPacket("2_0", "37.18.192.43:7010", PacketProtocolInfo.UniverseAgentProtocolName, true, 0x2000, [0x02, 0x03]),
				TestPacketFactory.CreateAnalyzedPacket("3_0", "37.18.192.43:7011", PacketProtocolInfo.PlayerAgentProtocolName, true, 0x1A07, [0x04, 0x05, 0x06], destinationPort: 5000, sourcePort: 7011)
			];

			var summary = service.Compute(packets);

			Assert.Equal(3, summary.TotalPackets);
			Assert.Equal(2, summary.DownloadPackets);
			Assert.Equal(1, summary.UploadPackets);
			Assert.Equal(0, summary.UnknownPackets);
			Assert.Equal(2, summary.UniqueServices);
			Assert.Equal(2, summary.UniqueOpcodes);
			Assert.Equal(packets[0].Bytes.Length + packets[1].Bytes.Length + packets[2].Bytes.Length, summary.TotalBytes);
			Assert.Equal(2, summary.UniverseAgentPackets);
			Assert.Equal(1, summary.PlayerAgentPackets);
		}
	}
}
