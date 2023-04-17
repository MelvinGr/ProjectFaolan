using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;

namespace Faolan.PacketAnalyzer
{
	public static class PacketCapturePortDetector
	{
		// UniverseAgent is the only port that is constant across every capture/deployment; every other
		// service (PlayerAgent, CsPlayerAgent, AgentServer, GameServer) is assigned per session and is
		// discovered from the capture by classifying each port's traffic by content.
		private const ushort UniverseAgentPort = 7010;

		// The GameServer download stream becomes zlib-compressed after this 4-byte marker. Compressed
		// bytes would mis-frame into junk packets, so we stop feeding a flow to the classifier once it
		// appears - the uncompressed packets seen beforehand are already enough to identify the port.
		private static readonly byte[] CompressionHeaderPrefix = [0x80, 0x00, 0x00, 0x05];

		internal static PortProtocolClassifier.ClassificationResult Detect(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("A capture file path is required.", nameof(path));

			if (!File.Exists(path))
				throw new FileNotFoundException("Capture file was not found.", path);

			var classifier = new PortProtocolClassifier(UniverseAgentPort);
			var compressedFlows = new HashSet<string>();

			new PacketCaptureScanner().Process(path, _ => true, (context, bytes) =>
			{
				if (compressedFlows.Contains(context.FlowKey))
					return null;

				if (StartsWithCompressionPrefix(bytes))
				{
					compressedFlows.Add(context.FlowKey);
					return null;
				}

				return bytes;
			}, (context, _, packet) =>
			{
				// ConanPacket covers Universe/Player/CsPlayer/GameServer; AgentServer uses its own
				// inline framing and decodes as AgentServerPacket. Both expose an opcode we can vote on.
				uint? opcode = packet switch
				{
					ConanPacket conanPacket             => conanPacket.Opcode,
					AgentServerPacket agentServerPacket => (uint)agentServerPacket.Opcode,
					_                                   => null
				};

				if (opcode is null)
					return;

				var serverIp = context.IsDownload
					? context.SourceIpAddress
					: context.DestinationIpAddress;
				var serverPort = context.IsDownload
					? context.SourcePort
					: context.DestinationPort;
				classifier.Observe(serverIp, serverPort, opcode.Value);
			});

			return classifier.Resolve();
		}

		private static bool StartsWithCompressionPrefix(byte[] bytes)
		{
			if (bytes.Length < CompressionHeaderPrefix.Length)
				return false;

			for (var i = 0; i < CompressionHeaderPrefix.Length; i++)
			{
				if (bytes[i] != CompressionHeaderPrefix[i])
					return false;
			}

			return true;
		}
	}
}
