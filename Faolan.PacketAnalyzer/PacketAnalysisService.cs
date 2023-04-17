using System.Collections.Generic;
using Faolan.PacketAnalyzer.Models;

namespace Faolan.PacketAnalyzer
{
	public sealed class PacketAnalysisService
	{
		public PacketAnalysisResult AnalyzeCapture(string path)
		{
			var detection = PacketCapturePortDetector.Detect(path);
			var packets = new PacketCaptureReader(detection.Settings, detection.EndpointProtocols).ReadDump(path);
			return new PacketAnalysisResult(detection.Settings, packets);
		}

		public IReadOnlyList<AnalyzedPacket> ReadDump(string path, PacketAnalyzerPortSettings portSettings)
		{
			return new PacketCaptureReader(portSettings).ReadDump(path);
		}
	}

	public readonly record struct PacketAnalysisResult(PacketAnalyzerPortSettings PortSettings, IReadOnlyList<AnalyzedPacket> Packets);
}
