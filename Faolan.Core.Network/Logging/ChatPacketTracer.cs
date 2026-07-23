using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The chat-tier packet tracer. The chat wire frame ([u16 opcode][u16 bodyLen][body]) has no Rpc sender/receiver
	// model, so both directions work off the typed ChatServerPacket - inbound the listener already holds one and the
	// opcode it dispatched on, outbound the transport hands over the bytes it wrote. The rendered LINE is the same shape
	// as every Conan line (PacketLogFormatter), which is the point: one trace format across protocols.
	public sealed class ChatPacketTracer : PacketTracer
	{
		public ChatPacketTracer(ILogger logger) : base(logger, "ChatServer")
		{
		}

		// Inbound (RX): the listener dispatched this packet on `opcode` (its typed enum), so the name comes straight off
		// that instead of a receiver-class lookup - the chat protocol has a single opcode space.
		public void Received(ChatServerPacket packet, Enum opcode)
		{
			try
			{
				Logger.LogPacket(PacketLogFormatter.Received, ChannelName, PacketLogFormatter.OpcodeName(opcode), packet.Body?.Length ?? 0, packet.Bytes?.Length ?? 0, ("body", Body(packet)));
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// Inbound (RX) with no handler: the same line, at Warning, marked UNHANDLED.
		public void Unhandled(ChatServerPacket packet, Enum opcode)
		{
			try
			{
				Logger.LogUnknownPacket(ChannelName, PacketLogFormatter.OpcodeName(opcode), packet.Body?.Length ?? 0, packet.Bytes?.Length ?? 0, ("body", Body(packet)));
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		protected override void RenderTransmitted(byte[] frame)
		{
			var packet = new ChatServerPacket(frame);
			Logger.LogPacket(PacketLogFormatter.Transmitted, ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerInterfaceOpcodes), (ushort)packet.Opcode), packet.Body?.Length ?? 0,
				packet.Bytes.Length, ("body", Body(packet)));
		}

		// The chat body is a stream cursor, so unlike the Conan tiers (whose body is already memory over the frame) it has
		// to be copied out to be dumped - which is worth doing ONLY when a dump will actually be rendered.
		private ReadOnlyMemory<byte> Body(ChatServerPacket packet)
		{
			return packet.Body is { } body && PacketLogOptions.ShouldLogBytes(Logger)
				? body.ToArray()
				: ReadOnlyMemory<byte>.Empty;
		}
	}
}
