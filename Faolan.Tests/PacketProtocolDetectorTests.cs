using System.Text;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.PacketAnalyzer;
using Xunit;

namespace Faolan.Tests
{
	public class PacketProtocolDetectorTests
	{
		[Fact]
		public void ObservePacket_DetectsPlayerAgentPort_FromUniverseAdvertisement()
		{
			var detector = new PacketProtocolDetector(PacketAnalyzerPortSettings.Empty);
			var packetBytes = TestPacketFactory.BuildPacketBytes((ushort)UniverseAgentResponseOpcodes.SendPlayerAgentRealm, Encoding.ASCII.GetBytes("37.18.192.43:7011"));
			var packet = new ConanPacket(packetBytes);

			detector.ObservePacket("37.18.192.43:7010", "192.168.1.10:5000", "37.18.192.43", "192.168.1.10", 7010, 5000, false, true, true, packet);

			Assert.True(detector.TryGetPort(PacketProtocolInfo.UniverseAgentProtocolName, out var universeAgentPort));
			Assert.Equal((ushort)7010, universeAgentPort);
			Assert.True(detector.TryGetPort(PacketProtocolInfo.PlayerAgentProtocolName, out var playerAgentPort));
			Assert.Equal((ushort)7011, playerAgentPort);
		}

		[Fact]
		public void ObservePacket_DoesNotRemapAuthoritativeCsPlayerAgentPort_WhenLaterAdvertisementsConflict()
		{
			var detector = new PacketProtocolDetector(new PacketAnalyzerPortSettings(7010, 7011, 7002, 7029, 7013));
			var openEndpointsPacket = new ConanPacket(TestPacketFactory.BuildPacketBytes((ushort)PlayerAgentOpcodes.Ox20EC, []));
			var advertisedEndpointPacket = new ConanPacket(TestPacketFactory.BuildPacketBytes((ushort)PlayerAgentResponseOpcodes.Ox1A07, [1, 2, 3, 4, 0x1B, 0x5A]));

			detector.ObservePacket("192.168.1.10:5000", "37.18.192.43:7011", "192.168.1.10", "37.18.192.43", 5000, 7011, true, false, false, openEndpointsPacket);

			detector.ObservePacket("37.18.192.43:7011", "192.168.1.10:5000", "37.18.192.43", "192.168.1.10", 7011, 5000, false, true, true, advertisedEndpointPacket);

			Assert.True(detector.TryGetPort(PacketProtocolInfo.CsPlayerAgentProtocolName, out var csPlayerAgentPort));
			Assert.Equal((ushort)7002, csPlayerAgentPort);
			Assert.True(detector.TryGetPort(PacketProtocolInfo.AgentServerProtocolName, out var agentServerPort));
			Assert.Equal((ushort)7013, agentServerPort);

			var detectedService = detector.ResolveService("192.168.1.10:5000", "37.18.192.43:7002", "192.168.1.10", "37.18.192.43", 5000, 7002, true, false, false);

			Assert.Equal(PacketProtocolInfo.CsPlayerAgentProtocolName, detectedService.ProtocolName);
		}

		[Fact]
		public void ResolveService_PreservesRawPacketsOnCsPlayerAgentPort_AsCsPlayerAgent()
		{
			var detector = new PacketProtocolDetector(new PacketAnalyzerPortSettings(7010, 7011, 7002, 7029, 7013));

			var detectedService = detector.ResolveService("192.168.1.10:5000", "37.18.192.43:7002", "192.168.1.10", "37.18.192.43", 5000, 7002, true, false, false);

			Assert.Equal("37.18.192.43:7002", detectedService.ServiceName);
			Assert.Equal(PacketProtocolInfo.CsPlayerAgentProtocolName, detectedService.ProtocolName);
		}
	}
}
