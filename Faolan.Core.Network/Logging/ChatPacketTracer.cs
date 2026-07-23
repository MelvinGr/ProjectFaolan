using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The chat-tier packet tracer. Both directions render from the structured ChatServerMessage, the same way the Conan
	// tiers render from an Rpc, and through the same line shaper (PacketLogFormatter) - one trace format across
	// protocols. The chat wire has a single opcode space per direction (no receiver class to disambiguate), so naming is
	// just which enum the direction implies.
	public sealed class ChatPacketTracer : PacketTracer
	{
		public ChatPacketTracer(ILogger logger) : base(logger, "ChatServer")
		{
		}

		// Inbound (RX): the listener dispatched this parsed frame.
		public void Received(in ChatServerMessage packet)
		{
			try
			{
				Logger.LogPacket(PacketLogFormatter.Received, ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength,
					("body", packet.Body));
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// Inbound (RX) with no handler: the same line, at Warning, marked UNHANDLED.
		public void Unhandled(in ChatServerMessage packet)
		{
			try
			{
				Logger.LogUnknownPacket(ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength, ("body", packet.Body));
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// The chat transport writes bytes (its frame is 4 bytes plus the body, so a send serializes straight through),
		// which are decoded back to the message here - the same render either way.
		protected override void RenderTransmitted(byte[] frame)
		{
			var packet = ChatServerMessage.Parse(frame);
			Logger.LogPacket(PacketLogFormatter.Transmitted, ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerInterfaceOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength,
				("body", packet.Body));
		}
	}
}
