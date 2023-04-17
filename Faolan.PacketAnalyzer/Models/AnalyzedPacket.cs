using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;

namespace Faolan.PacketAnalyzer.Models
{
	public sealed class AnalyzedPacket
	{
		public bool IsDownload { get; }
		public byte[] Bytes => Packet.Bytes;
		public ConanPacket? ConanPacket => Packet as ConanPacket;
		public Packet Packet { get; }
		public string DestinationAddress { get; }
		public string DestinationIpAddress { get; }

		public string OpcodeHex => OpcodeValue.ToHex();
		public string ProtocolName { get; }
		public string ServiceName { get; }
		public string SourceAddress { get; }
		public string SourceIpAddress { get; }
		public string SourceName { get; }
		public string SubOpcodeHex { get; }
		public string SubOpcodeName { get; }
		public string Ticks { get; }

		public uint OpcodeValue =>
			Packet switch
			{
				ConanPacket conanPacket             => conanPacket.Opcode,
				AgentServerPacket agentServerPacket => (ushort)agentServerPacket.Opcode,
				_                                   => 0
			};

		public uint PacketLength => Packet.Length;
		public uint? PayloadLength => Packet.Body?.Length;
		public uint? SubOpcodeValue { get; }
		public ushort DestinationPort { get; }
		public ushort SourcePort { get; }

		public AnalyzedPacket(string ticks, string serviceName, string protocolName, bool isDownload, Packet packet, string sourceName, string sourceAddress, string destinationAddress, string sourceIpAddress,
			string destinationIpAddress, ushort sourcePort, ushort destinationPort)
		{
			Ticks = ticks;
			ServiceName = serviceName;
			ProtocolName = protocolName;
			IsDownload = isDownload;
			Packet = packet;
			SourceName = sourceName;
			SourceAddress = sourceAddress;
			DestinationAddress = destinationAddress;
			SourceIpAddress = sourceIpAddress;
			DestinationIpAddress = destinationIpAddress;
			SourcePort = sourcePort;
			DestinationPort = destinationPort;

			if (ConanPacket != null && PacketProtocolInfo.TryGetSubOpcode(ProtocolName, IsDownload, ConanPacket, out var subOpcode, out var subOpcodeName))
			{
				SubOpcodeValue = subOpcode;
				SubOpcodeHex = subOpcode.ToHex();
				SubOpcodeName = subOpcodeName ?? string.Empty;
			}
			else
			{
				SubOpcodeHex = string.Empty;
				SubOpcodeName = string.Empty;
			}
		}

		public static AnalyzedPacket FromBytes(string ticks, string serviceName, string protocolName, bool isDownload, byte[] bytes, string sourceName, string sourceAddress, string destinationAddress, string sourceIpAddress,
			string destinationIpAddress, ushort sourcePort, ushort destinationPort)
		{
			return new AnalyzedPacket(ticks, serviceName, protocolName, isDownload, ParsePacket(bytes), sourceName, sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort, destinationPort);
		}

		public static AnalyzedPacket FromPacket(string ticks, string serviceName, string protocolName, bool isDownload, Packet packet, string sourceName, string sourceAddress, string destinationAddress, string sourceIpAddress,
			string destinationIpAddress, ushort sourcePort, ushort destinationPort)
		{
			var resolvedProtocolName = string.IsNullOrWhiteSpace(protocolName)
				? packet is ConanPacket conanPacket
					? PacketProtocolInfo.GetProtocolName(serviceName, isDownload, conanPacket)
					: PacketProtocolInfo.GetProtocolName(PacketProtocolInfo.GetServicePort(serviceName), isDownload, OpcodeValueFrom(packet))
				: protocolName;
			return new AnalyzedPacket(ticks, serviceName, resolvedProtocolName, isDownload, packet, sourceName, sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort, destinationPort);
		}

		private static uint OpcodeValueFrom(Packet packet)
		{
			return packet switch
			{
				ConanPacket conanPacket             => conanPacket.Opcode,
				AgentServerPacket agentServerPacket => (ushort)agentServerPacket.Opcode,
				_                                   => 0
			};
		}

		private static Packet ParsePacket(byte[] bytes)
		{
			try
			{
				var conanPacket = new ConanPacket(bytes);
				if (conanPacket.IsValid)
					return conanPacket;
			}
			catch
			{
				// fall through
			}

			try
			{
				var agentServerPacket = new AgentServerPacket(bytes);
				if (agentServerPacket.IsValid)
					return agentServerPacket;
			}
			catch
			{
				// fall through
			}

			throw new InvalidOperationException("Failed to parse packet");
		}
	}
}
