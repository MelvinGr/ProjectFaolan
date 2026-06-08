using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Faolan.PacketAnalyzer.Models;
using Faolan.PacketAnalyzer.ViewModels;
using Newtonsoft.Json;

namespace Faolan.PacketAnalyzer
{
	public sealed class PacketDumpFileService
	{
		public const string CurrentSchemaVersion = "faolan.packetdump/v1";

		private readonly PacketSummaryService _summaryService = new PacketSummaryService();

		public PacketDumpLoadResult Deserialize(string json, string fallbackCaptureName = "packet-dump.json")
		{
			if (string.IsNullOrWhiteSpace(json))
				throw new InvalidDataException("Packet dump JSON is empty.");

			var document = JsonConvert.DeserializeObject<PacketDumpDocument>(json);
			if (document == null)
				throw new InvalidDataException("Packet dump JSON could not be parsed.");

			if (!string.Equals(document.SchemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException($"Unsupported packet dump schema '{document.SchemaVersion}'.");

			var captureName = !string.IsNullOrWhiteSpace(document.Capture?.Name)
				? document.Capture.Name
				: fallbackCaptureName;

			var portSettings = document.Capture?.Ports?.ToPacketAnalyzerPortSettings() ?? PacketAnalyzerPortSettings.Empty;
			var packetsFromDocument = document.Packets?.Select(packet => packet.ToPacketRowViewModel()).ToArray() ?? [];
			return new PacketDumpLoadResult(captureName, portSettings, packetsFromDocument, document.SchemaVersion);
		}

		public PacketDumpLoadResult Load(string path)
		{
			var json = File.ReadAllText(path);
			var fallbackCaptureName = Path.GetFileName(path);
			return Deserialize(json, fallbackCaptureName);
		}

		public void Save(string path, string captureName, PacketAnalyzerPortSettings portSettings, IReadOnlyCollection<PacketRowViewModel> packets, Formatting formatting = Formatting.Indented)
		{
			File.WriteAllText(path, Serialize(captureName, portSettings, packets, formatting));
		}

		public void SaveSelectedPacketBytes(string jsonPath, AnalyzedPacket packet)
		{
			var selectedPacketPath = Path.Combine(Path.GetDirectoryName(jsonPath) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(jsonPath)}.selected.bin");
			File.WriteAllBytes(selectedPacketPath, packet.Bytes);
		}

		public string Serialize(string captureName, PacketAnalyzerPortSettings portSettings, IReadOnlyCollection<AnalyzedPacket> packets, Formatting formatting = Formatting.Indented)
		{
			var packetArray = packets.ToArray();
			var summary = _summaryService.Compute(packetArray);
			var document = new PacketDumpDocument
			{
				SchemaVersion = CurrentSchemaVersion,
				Capture = new PacketDumpCaptureInfo
				{
					Name = captureName,
					ExportedAtUtc = DateTimeOffset.UtcNow,
					Ports = PacketDumpPortSettings.From(portSettings)
				},
				Summary = PacketDumpSummary.From(summary),
				Packets = packetArray.Select(packet => PacketDumpPacket.From(packet)).ToArray()
			};

			return JsonConvert.SerializeObject(document, formatting);
		}

		public string Serialize(string captureName, PacketAnalyzerPortSettings portSettings, IReadOnlyCollection<PacketRowViewModel> packets, Formatting formatting = Formatting.Indented)
		{
			return Serialize(captureName, portSettings, packets.Select(packet => packet.Packet).ToArray(), packets, formatting);
		}

		private string Serialize(string captureName, PacketAnalyzerPortSettings portSettings, IReadOnlyCollection<AnalyzedPacket> packets, IReadOnlyCollection<PacketRowViewModel> wrappers, Formatting formatting)
		{
			var packetArray = packets.ToArray();
			var wrapperByTicks = wrappers.ToDictionary(wrapper => wrapper.Ticks, StringComparer.Ordinal);
			var summary = _summaryService.Compute(packetArray);
			var document = new PacketDumpDocument
			{
				SchemaVersion = CurrentSchemaVersion,
				Capture = new PacketDumpCaptureInfo
				{
					Name = captureName,
					ExportedAtUtc = DateTimeOffset.UtcNow,
					Ports = PacketDumpPortSettings.From(portSettings)
				},
				Summary = PacketDumpSummary.From(summary),
				Packets = packetArray.Select(packet => PacketDumpPacket.From(packet, wrapperByTicks.TryGetValue(packet.Ticks, out var wrapper)
					? wrapper?.Notes
					: null)).ToArray()
			};

			return JsonConvert.SerializeObject(document, formatting);
		}
	}

	public sealed class PacketDumpDocument
	{
		[JsonPropertyOrder(0)]
		public PacketDumpCaptureInfo Capture { get; init; } = new PacketDumpCaptureInfo();

		[JsonPropertyOrder(2)]
		public PacketDumpPacket[] Packets { get; init; } = [];

		[JsonPropertyOrder(1)]
		public PacketDumpSummary Summary { get; init; } = new PacketDumpSummary();

		[JsonPropertyOrder(3)]
		public string SchemaVersion { get; init; } = PacketDumpFileService.CurrentSchemaVersion;
	}

	public sealed class PacketDumpCaptureInfo
	{
		public DateTimeOffset ExportedAtUtc { get; init; }
		public PacketDumpPortSettings Ports { get; init; } = new PacketDumpPortSettings();
		public string Name { get; init; } = string.Empty;
	}

	public sealed class PacketDumpPortSettings
	{
		public ushort AgentServerPort { get; init; }
		public ushort CsPlayerAgentPort { get; init; }
		public ushort GameServerPort { get; init; }
		public ushort PlayerAgentPort { get; init; }
		public ushort UniverseAgentPort { get; init; }

		public PacketAnalyzerPortSettings ToPacketAnalyzerPortSettings()
		{
			return new PacketAnalyzerPortSettings(UniverseAgentPort, PlayerAgentPort, CsPlayerAgentPort, GameServerPort, AgentServerPort);
		}

		public static PacketDumpPortSettings From(PacketAnalyzerPortSettings settings)
		{
			return new PacketDumpPortSettings
			{
				UniverseAgentPort = settings.UniverseAgentPort,
				PlayerAgentPort = settings.PlayerAgentPort,
				CsPlayerAgentPort = settings.CsPlayerAgentPort,
				AgentServerPort = settings.AgentServerPort,
				GameServerPort = settings.GameServerPort
			};
		}
	}

	public sealed class PacketDumpSummary
	{
		public int AgentServerPackets { get; init; }
		public int CsPlayerAgentPackets { get; init; }
		public int DownloadPackets { get; init; }
		public int GameServerPackets { get; init; }
		public int PlayerAgentPackets { get; init; }
		public int TotalPackets { get; init; }
		public int UniqueOpcodes { get; init; }
		public int UniqueServices { get; init; }
		public int UniverseAgentPackets { get; init; }
		public int UnknownPackets { get; init; }
		public int UploadPackets { get; init; }
		public long TotalBytes { get; init; }

		public static PacketDumpSummary From(PacketSummary summary)
		{
			return new PacketDumpSummary
			{
				TotalPackets = summary.TotalPackets,
				DownloadPackets = summary.DownloadPackets,
				UploadPackets = summary.UploadPackets,
				UnknownPackets = summary.UnknownPackets,
				UniqueServices = summary.UniqueServices,
				UniqueOpcodes = summary.UniqueOpcodes,
				TotalBytes = summary.TotalBytes,
				UniverseAgentPackets = summary.UniverseAgentPackets,
				PlayerAgentPackets = summary.PlayerAgentPackets,
				CsPlayerAgentPackets = summary.CsPlayerAgentPackets,
				AgentServerPackets = summary.AgentServerPackets,
				GameServerPackets = summary.GameServerPackets
			};
		}
	}

	public sealed class PacketDumpPacket
	{
		public bool IsDownload { get; init; }
		public int PacketIndex { get; init; }
		public long TimestampTicks { get; init; }
		public PacketDumpEndpoint Destination { get; init; } = new PacketDumpEndpoint();
		public PacketDumpEndpoint Source { get; init; } = new PacketDumpEndpoint();
		public PacketDumpLengthInfo Length { get; init; } = new PacketDumpLengthInfo();
		public PacketDumpOpcodeInfo Opcode { get; init; } = new PacketDumpOpcodeInfo();
		public PacketDumpOpcodeInfo SubOpcode { get; init; } = new PacketDumpOpcodeInfo();
		public string Notes { get; init; } = string.Empty;
		public string ProtocolName { get; init; } = string.Empty;
		public string RawBytesBase64 { get; init; } = string.Empty;
		public string ServiceName { get; init; } = string.Empty;

		public PacketRowViewModel ToPacketRowViewModel()
		{
			var ticks = PacketIndex > 0
				? $"{TimestampTicks}_{PacketIndex}"
				: TimestampTicks.ToString();
			var bytes = string.IsNullOrWhiteSpace(RawBytesBase64)
				? []
				: Convert.FromBase64String(RawBytesBase64);
			var packet = AnalyzedPacket.FromBytes(ticks, ServiceName, ProtocolName, IsDownload, bytes, Source.Name, Source.Address, Destination.Address, Source.IpAddress, Destination.IpAddress, Source.Port, Destination.Port);
			var packetWrapper = new PacketRowViewModel(packet)
			{
				Notes = string.IsNullOrWhiteSpace(Notes)
					? null
					: Notes
			};
			return packetWrapper;
		}

		public static PacketDumpPacket From(AnalyzedPacket packet, string? notes = null)
		{
			ParseTicks(packet.Ticks, out var timestampTicks, out var packetIndex);
			var opcodeName = packet.ConanPacket != null
				? PacketProtocolInfo.GetOpcodeName(packet.ProtocolName, packet.IsDownload, packet.ConanPacket)
				: PacketProtocolInfo.GetOpcodeName(packet.ProtocolName, packet.IsDownload, packet.OpcodeValue);

			return new PacketDumpPacket
			{
				TimestampTicks = timestampTicks,
				PacketIndex = packetIndex,
				IsDownload = packet.IsDownload,
				ServiceName = packet.ServiceName,
				ProtocolName = packet.ProtocolName,
				Notes = notes ?? string.Empty,
				Source = new PacketDumpEndpoint
				{
					Name = packet.SourceName,
					Address = packet.SourceAddress,
					IpAddress = packet.SourceIpAddress,
					Port = packet.SourcePort
				},
				Destination = new PacketDumpEndpoint
				{
					Address = packet.DestinationAddress,
					IpAddress = packet.DestinationIpAddress,
					Port = packet.DestinationPort
				},
				Opcode = new PacketDumpOpcodeInfo
				{
					Value = packet.OpcodeValue,
					Hex = packet.OpcodeHex,
					Name = opcodeName ?? string.Empty
				},
				SubOpcode = new PacketDumpOpcodeInfo
				{
					Value = packet.SubOpcodeValue ?? 0,
					Hex = packet.SubOpcodeHex,
					Name = packet.SubOpcodeName
				},
				Length = new PacketDumpLengthInfo
				{
					Packet = packet.PacketLength,
					Payload = packet.PayloadLength ?? 0
				},
				RawBytesBase64 = Convert.ToBase64String(packet.Bytes)
			};
		}

		private static void ParseTicks(string ticks, out long timestampTicks, out int packetIndex)
		{
			timestampTicks = 0;
			packetIndex = 0;

			if (string.IsNullOrWhiteSpace(ticks))
				return;

			var separatorIndex = ticks.LastIndexOf('_');
			if (separatorIndex <= 0)
			{
				long.TryParse(ticks, out timestampTicks);
				return;
			}

			long.TryParse(ticks[..separatorIndex], out timestampTicks);
			int.TryParse(ticks[(separatorIndex + 1)..], out packetIndex);
		}
	}

	public sealed class PacketDumpEndpoint
	{
		public string Address { get; init; } = string.Empty;
		public string IpAddress { get; init; } = string.Empty;
		public string Name { get; init; } = string.Empty;
		public ushort Port { get; init; }
	}

	public sealed class PacketDumpOpcodeInfo
	{
		public string Hex { get; init; } = string.Empty;
		public string Name { get; init; } = string.Empty;
		public uint Value { get; init; }
	}

	public sealed class PacketDumpLengthInfo
	{
		public uint Packet { get; init; }
		public uint Payload { get; init; }
	}

	public readonly record struct PacketDumpLoadResult(string CaptureName, PacketAnalyzerPortSettings PortSettings, IReadOnlyList<PacketRowViewModel> PacketRows, string SchemaVersion);
}
