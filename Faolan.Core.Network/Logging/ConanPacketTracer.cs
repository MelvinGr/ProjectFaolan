using System;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;


namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     The Conan-tier packet tracer. Every Conan frame is an <see cref="Rpc" /> in both directions - the parser
	///     yields one for a received frame, the send path builds one - so both render through the same <c>Trace</c>:
	///     same naming (<see cref="ProtocolCatalog" />), same body dump, same channel label.
	/// </summary>
	/// <remarks>
	///     This is the plain tier (the login/CS listeners), to which an Rpc body is opaque. The GameServer multiplexes a
	///     sub-protocol inside its PassBlob carrier and renders a richer line for it; see <c>GameServerPacketTracer</c>.
	/// </remarks>
	public class ConanPacketTracer : PacketTracer
	{
		private readonly Type _interfaceOpcodes;

		/// <param name="interfaceOpcodes">
		///     This listener's s2c <c>*Interface</c> enum: the transmit-side naming fall-back for the client-side
		///     classes the catalog does not map (the login tier).
		/// </param>
		public ConanPacketTracer(ILogger logger, string channelName, Type interfaceOpcodes) : base(logger, channelName)
		{
			_interfaceOpcodes = interfaceOpcodes;
		}

		// A raw Conan frame handed to the transport as bytes (no structured send site). Decoded back to an Rpc, which
		// stamps its FrameLength, so it renders through the one path. Production sends are all structured, so this
		// costs nothing on the hot path.
		protected override void RenderTransmitted(byte[] frame)
		{
			RenderTransmitted(Rpc.Parse(frame));
		}

		protected override void RenderTransmitted(in Rpc rpc)
		{
			Trace(PacketLogFormatter.Transmitted, in rpc, _interfaceOpcodes, true);
		}

		// Inbound: the listener dispatched this parsed frame. Both the method name (from the Receiver.Type + Opcode
		// pair) and the frame length ride on the Rpc itself, so the listener passes nothing but the packet.
		public void Received(in Rpc packet)
		{
			try
			{
				Trace(PacketLogFormatter.Received, in packet, null, true);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// Inbound with no handler: the same line, at Warning, marked unhandled.
		public void Unhandled(in Rpc packet)
		{
			try
			{
				Trace(PacketLogFormatter.Received, in packet, null, false);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// The line for a packet the listener handled. Virtual so a tier that multiplexes a sub-protocol can render a
		// richer line for its carrier (GameServerPacketTracer's PassBlob/IIR); everything else about the trace - naming,
		// direction, the unhandled shape, the byte dump - stays shared.
		protected virtual void RenderPacket(string direction, string opcodeName, in Rpc rpc)
		{
			Logger.LogPacket(direction, ChannelName, opcodeName, rpc.Body.Length, rpc.FrameLength, rpc.Body);
		}

		// The single render for both directions. Direction-dependent inputs are exactly two, and both are properties of
		// the wire rather than of the code path: the direction tag, and the naming fall-back enum (s2c only - see
		// ResolveName).
		private void Trace(string direction, in Rpc rpc, Type? fallbackOpcodes, bool handled)
		{
			// Named from the whole message, not from (class, opcode): on the login tier the opcode is a lossy view of the
			// method and would name the wrong one of a colliding pair.
			var opcodeName = ProtocolCatalog.ResolveName(in rpc, fallbackOpcodes);

			// The unhandled marker wins over any richer line shape: the marker is the point of that line.
			if (!handled)
			{
				Logger.LogUnknownPacket(ChannelName, opcodeName, rpc.Body.Length, rpc.FrameLength, rpc.Body);
				return;
			}

			RenderPacket(direction, opcodeName, in rpc);
		}
	}
}
