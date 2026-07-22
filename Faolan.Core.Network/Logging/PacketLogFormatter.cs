using System;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The SINGLE place the packet-trace log line is shaped, shared by the inbound path (Server.LogPacketReceived) and
	// the outbound loggers (OutgoingPacketLogger) so both directions render identically. Direction is UL (uplink =
	// client -> server, inbound) or DL (downlink = server -> client, outbound). The raw byte dump is appended via
	// PacketLogOptions (gated by the LogPacketBytes switch), so disabling packet bytes silences it in both directions.
	public static class PacketLogFormatter
	{
		public const string Downlink = "DL"; // server -> client (outbound)
		public const string Uplink = "UL"; // client -> server (inbound)

		// "Name (0xHEX)" when the opcode is a defined member of enumType, else the bare "0xHEX".
		public static string OpcodeName(Type enumType, ushort opcode)
		{
			return Enum.IsDefined(enumType, opcode)
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: opcode.ToHex();
		}

		public static string OpcodeName(Type enumType, uint opcode)
		{
			return Enum.IsDefined(enumType, opcode)
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: opcode.ToHex();
		}

		// The inbound path already holds the typed opcode (the listener dispatched on it), so name it straight off.
		public static string OpcodeName(Enum opcode)
		{
			return Enum.IsDefined(opcode.GetType(), opcode)
				? $"{opcode} ({opcode.ToHex()})"
				: $"Unknown ({opcode.ToHex()})";
		}

		extension(ILogger logger)
		{
			public void LogPacket(string direction, string channel, string opcodeName, long bodyLength, long packetLength, params (string Label, byte[]? Data)[] byteSegments)
			{
				logger.LogInformation("{Direction} [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					direction, channel, opcodeName, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}

			// The PassBlob (IIR) variant that also carries a 32-bit sub-opcode + its declared payload length.
			public void LogPacketWithSubOpcode(string direction, string channel, string opcodeName, string subOpcodeName, uint payloadLength, long bodyLength, long packetLength,
				params (string Label, byte[]? Data)[] byteSegments)
			{
				logger.LogInformation("{Direction} [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					direction, channel, opcodeName, subOpcodeName, payloadLength, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}
		}
	}
}
