using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.PacketAnalyzer.Models;

namespace Faolan.PacketAnalyzer
{
	public sealed class PacketSummaryService
	{
		public PacketSummary Compute(IEnumerable<AnalyzedPacket> packets)
		{
			var packetArray = packets.ToArray();
			return new PacketSummary(packetArray.Length, packetArray.Count(p => p.IsDownload), packetArray.Count(p => !p.IsDownload), packetArray.Count(IsUnknownProtocol),
				packetArray.Select(p => p.ServiceName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
				packetArray.Select(CreateOpcodeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(), packetArray.Sum(p => (long)(p.Bytes?.Length ?? 0)),
				packetArray.Count(p => IsProtocol(p, PacketProtocolInfo.UniverseAgentProtocolName)), packetArray.Count(p => IsProtocol(p, PacketProtocolInfo.PlayerAgentProtocolName)),
				packetArray.Count(p => IsProtocol(p, PacketProtocolInfo.CsPlayerAgentProtocolName)), packetArray.Count(p => IsProtocol(p, PacketProtocolInfo.AgentServerProtocolName)),
				packetArray.Count(p => IsProtocol(p, PacketProtocolInfo.GameServerProtocolName)));
		}

		private static string CreateOpcodeKey(AnalyzedPacket packet)
		{
			return packet.SubOpcodeValue.HasValue
				? $"{packet.ProtocolName}|{packet.OpcodeValue:X8}|{packet.SubOpcodeValue.Value:X8}"
				: $"{packet.ProtocolName}|{packet.OpcodeValue:X8}";
		}

		private static bool IsProtocol(AnalyzedPacket packet, string protocolName)
		{
			return string.Equals(packet.ProtocolName, protocolName, StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsUnknownProtocol(AnalyzedPacket packet)
		{
			return string.IsNullOrWhiteSpace(packet.ProtocolName) || string.Equals(packet.ProtocolName, PacketProtocolInfo.UnknownProtocolName, StringComparison.OrdinalIgnoreCase);
		}
	}

	public readonly record struct PacketSummary(
		int TotalPackets,
		int DownloadPackets,
		int UploadPackets,
		int UnknownPackets,
		int UniqueServices,
		int UniqueOpcodes,
		long TotalBytes,
		int UniverseAgentPackets,
		int PlayerAgentPackets,
		int CsPlayerAgentPackets,
		int AgentServerPackets,
		int GameServerPackets);
}
