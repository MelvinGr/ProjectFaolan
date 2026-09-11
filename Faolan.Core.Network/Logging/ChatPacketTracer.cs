using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Microsoft.Extensions.Logging;


namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     The chat-tier packet tracer. Both directions render from the structured <see cref="ChatServerMessage" />, as
	///     the Conan tiers do from an <c>Rpc</c>, and through the same line shaper
	///     (<see cref="PacketLogFormatter" />), giving one trace format across protocols.
	/// </summary>
	/// <remarks>
	///     The chat wire has a single opcode space per direction (no receiver class to disambiguate), so naming is
	///     simply whichever enum the direction implies.
	/// </remarks>
	public sealed class ChatPacketTracer : PacketTracer
	{
		public ChatPacketTracer(ILogger logger) : base(logger, "ChatServer")
		{
		}

		// The chat transport writes bytes (its frame is 4 bytes plus the body, so a send serializes straight through),
		// which are decoded back to the message here, giving the same render either way.
		protected override void RenderTransmitted(byte[] frame)
		{
			var packet = ChatServerMessage.Parse(frame);
			Logger.LogPacket(PacketLogFormatter.Transmitted, ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerInterfaceOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength,
				packet.Body);
		}

		// Inbound (RX): the listener dispatched this parsed frame.
		public void Received(in ChatServerMessage packet)
		{
			try
			{
				Logger.LogPacket(PacketLogFormatter.Received, ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength,
					packet.Body);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// Inbound with no handler: the same line, at Warning, marked unhandled.
		public void Unhandled(in ChatServerMessage packet)
		{
			try
			{
				Logger.LogUnknownPacket(ChannelName, PacketLogFormatter.OpcodeName(typeof(ChatServerOpcodes), packet.Opcode), packet.Body.Length, packet.FrameLength, packet.Body);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}
	}
}
