using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The SINGLE place the packet-trace log line is shaped, shared by the inbound path (Server.LogPacketReceived) and
	// the outbound loggers (OutgoingPacketLogger) so both directions render identically. Direction is from the SERVER's
	// perspective: RX (received = client -> server, inbound) or TX (transmitted = server -> client, outbound). The raw
	// byte dump is appended via PacketLogOptions (gated by the LogPacketBytes switch), so disabling packet bytes
	// silences it in both directions.
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

			// An inbound (RX) packet whose opcode the listener didn't handle: same line shape as LogPacket, at Warning
			// level with an UNHANDLED marker. `opcodeName` already carries its "(0xHEX)" (via OpcodeName).
			public void LogUnknownPacket(string channel, string opcodeName, long bodyLength, long packetLength, params (string Label, byte[]? Data)[] byteSegments)
			{
				logger.LogWarning("{Direction} [{Channel} {OpcodeName}] UNHANDLED, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
					Received, channel, opcodeName, bodyLength, packetLength, PacketLogOptions.BytesSuffix(logger, byteSegments));
			}

			// Inbound (RX) trace: the Server forwards the raw packet + the typed opcode the listener dispatched on, and
			// these shape the line - so ALL packet-trace formatting (and the byte-dump gate) lives here, not in Server.
			public void LogReceived(string channel, Packet packet, Enum opcode)
			{
				logger.LogPacket(Received, channel, OpcodeName(opcode), packet.Body?.Length ?? 0, packet.Bytes?.Length ?? 0, ("body", packet.Body?.ToArray()));
			}

			public void LogUnhandled(string channel, Packet packet, Enum opcode)
			{
				logger.LogUnknownPacket(channel, OpcodeName(opcode), packet.Body?.Length ?? 0, packet.Bytes?.Length ?? 0, ("body", packet.Body?.ToArray()));
			}

			// Rpc-model RX variants (Gitea #407): the listener dispatches on the parsed Rpc; both the method NAME and the
			// FrameLength ride on the Rpc itself. The name comes from the (Receiver.Type, Opcode) pair (ProtocolCatalog),
			// so the listener no longer threads a separate typed opcode enum just for the trace.
			public void LogReceived(string channel, in Rpc packet)
			{
				logger.LogPacket(Received, channel, ProtocolCatalog.ResolveName(packet.Receiver.Type, packet.Opcode), packet.Body.Length, packet.FrameLength, ("body", packet.Body.ToArray()));
			}

			public void LogUnhandled(string channel, in Rpc packet)
			{
				logger.LogUnknownPacket(channel, ProtocolCatalog.ResolveName(packet.Receiver.Type, packet.Opcode), packet.Body.Length, packet.FrameLength, ("body", packet.Body.ToArray()));
			}

			// An extra opcode-specific body dump some inbound handlers emit on their own line, gated on the byte-dump
			// switch (PacketLogOptions.ShouldLogBytes) so disabling packet bytes silences these too.
			public void LogBodyDump(string label, byte[]? bytes)
			{
				if (bytes?.Length > 0 && PacketLogOptions.ShouldLogBytes(logger))
					logger.LogDebug("{Label} ({Length} bytes): {Bytes}", label, bytes.Length, bytes.ToHex());
			}
		}
	}
}
