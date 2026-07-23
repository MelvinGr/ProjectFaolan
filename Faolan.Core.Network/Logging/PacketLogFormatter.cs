using System;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The SINGLE place the packet-trace log line is SHAPED. Both directions reach it through the channel's PacketTracer,
	// so an RX line and a TX line differ only in their direction tag and in what the wire actually carried. Direction is
	// from the SERVER's perspective: RX (received = client -> server, inbound), TX (transmitted = server -> client,
	// outbound). The raw byte dump is appended via PacketLogOptions (gated by the LogPacketBytes switch), so disabling
	// packet bytes silences it in both directions.
	//
	// This type shapes lines; it does NOT decide what a packet is called or which line shape it gets - that is the
	// tracer's job (see PacketTracer / ConanPacketTracer / ChatPacketTracer), which is why there are no direction-specific
	// helpers here anymore.
	public static class PacketLogFormatter
	{
		// Direction tags are from the SERVER's perspective (the node writing the log): TX = the server transmitted it
		// (server -> client, outbound), RX = the server received it (client -> server, inbound).
		public const string Transmitted = "TX"; // server -> client (outbound, sent by the server)
		public const string Received = "RX"; // client -> server (inbound, received by the server)

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

		// The chat tier already holds the typed opcode (its listener dispatched on it), so it names it straight off.
		public static string OpcodeName(Enum opcode)
		{
			return Enum.IsDefined(opcode.GetType(), opcode)
				? $"{opcode} ({opcode.ToHex()})"
				: $"Unknown ({opcode.ToHex()})";
		}

		extension(ILogger logger)
		{
			public void LogPacket(string direction, string channel, string opcodeName, long bodyLength, long packetLength, params (string Label, ReadOnlyMemory<byte> Data)[] byteSegments)
			{
				logger.LogInformation("{Direction} [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					direction, channel, opcodeName, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}

			// The PassBlob (IIR) variant that also carries a 32-bit sub-opcode + its declared payload length.
			public void LogPacketWithSubOpcode(string direction, string channel, string opcodeName, string subOpcodeName, uint payloadLength, long bodyLength, long packetLength,
				params (string Label, ReadOnlyMemory<byte> Data)[] byteSegments)
			{
				logger.LogInformation("{Direction} [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					direction, channel, opcodeName, subOpcodeName, payloadLength, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}

			// An inbound (RX) packet whose opcode the listener didn't handle: same line shape as LogPacket, at Warning
			// level with an UNHANDLED marker. `opcodeName` already carries its "(0xHEX)" (via OpcodeName).
			public void LogUnknownPacket(string channel, string opcodeName, long bodyLength, long packetLength, params (string Label, ReadOnlyMemory<byte> Data)[] byteSegments)
			{
				logger.LogWarning("{Direction} [{Channel} {OpcodeName}] UNHANDLED, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					Received, channel, opcodeName, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}

			// An extra opcode-specific body dump some inbound handlers emit on their own line, gated on the byte-dump
			// switch (PacketLogOptions.ShouldLogBytes) so disabling packet bytes silences these too.
			public void LogBodyDump(string label, ReadOnlyMemory<byte> bytes)
			{
				if (bytes.Length > 0 && PacketLogOptions.ShouldLogBytes(logger))
					logger.LogDebug("{Label} ({Length} bytes): {Bytes}", label, bytes.Length, bytes.ToHex());
			}
		}
	}
}
