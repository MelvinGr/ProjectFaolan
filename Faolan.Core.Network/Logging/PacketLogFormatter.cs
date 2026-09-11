using System;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     The single place the packet-trace log line is shaped. Both directions reach it through the channel's
	///     <see cref="PacketTracer" />, so an RX line and a TX line differ only in their direction tag and in what the
	///     wire carried.
	/// </summary>
	/// <remarks>
	///     Direction is from the server's perspective: RX (received = client to server, inbound), TX (transmitted =
	///     server to client, outbound). The raw byte dump is appended via <see cref="PacketLogOptions" />, gated by the
	///     <c>Log:PacketBytes</c> switch, so disabling packet bytes silences it in both directions.
	///     <para>
	///         This type shapes lines; it does not decide what a packet is called or which line shape it gets. That is
	///         the tracer's job - see <see cref="PacketTracer" />, <see cref="ConanPacketTracer" /> and
	///         <see cref="ChatPacketTracer" />.
	///     </para>
	/// </remarks>
	public static class PacketLogFormatter
	{
		public const string Received = "RX"; // client -> server (inbound, received by the server)

		// Direction tags are from the server's perspective (the node writing the log).
		public const string Transmitted = "TX"; // server -> client (outbound, sent by the server)

		extension(ILogger logger)
		{
			// An extra opcode-specific body dump some inbound handlers emit on their own line, gated on the byte-dump
			// switch (PacketLogOptions.ShouldLogBytes) so disabling packet bytes silences these too.
			public void LogBodyDump(string label, ReadOnlyMemory<byte> bytes)
			{
				if (bytes.Length > 0 && PacketLogOptions.ShouldLogBytes)
					logger.LogDebug("{Label} ({Length} bytes): {Bytes}", label, bytes.Length, bytes.ToHex());
			}

			public void LogPacket(string direction, string channel, string opcodeName, long bodyLength, long packetLength, ReadOnlyMemory<byte> body)
			{
				logger.LogDebug("{Direction} [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", direction, channel, opcodeName, bodyLength, packetLength,
					PacketLogOptions.BodySuffix(body));
			}

			// The PassBlob (IIR) variant that also carries a 32-bit sub-opcode + its declared payload length.
			public void LogPacketWithSubOpcode(string direction, string channel, string opcodeName, string subOpcodeName, uint payloadLength, long bodyLength, long packetLength,
				ReadOnlyMemory<byte> body)
			{
				logger.LogDebug("{Direction} [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", direction,
					channel, opcodeName, subOpcodeName, payloadLength, bodyLength, packetLength, PacketLogOptions.BodySuffix(body));
			}

			// An inbound packet whose opcode the listener did not handle: the same line shape as LogPacket, at Warning
			// level with an UNHANDLED marker. `opcodeName` already carries its "(0xHEX)" via OpcodeName.
			public void LogUnknownPacket(string channel, string opcodeName, long bodyLength, long packetLength, ReadOnlyMemory<byte> body)
			{
				logger.LogWarning("{Direction} [{Channel} {OpcodeName}] UNHANDLED, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", Received, channel, opcodeName, bodyLength,
					packetLength, PacketLogOptions.BodySuffix(body));
			}
		}

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
	}
}
