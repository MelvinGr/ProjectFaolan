using System;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

// CS8620 here is a Roslyn false positive: passing (string, ReadOnlyMemory<byte>) tuples to the params tuple-array on the
// LogPacket/LogPacketWithSubOpcode/LogUnknownPacket extension members trips a nullability-inference bug even when the
// argument and parameter tuple types are identical (verified). The values are non-null - suppress it (see PacketLogFormatter).
#pragma warning disable CS8620

namespace Faolan.Core.Network.Logging
{
	// The Conan-tier packet tracer. Every Conan frame is an Rpc in BOTH directions - the parser yields one for a
	// received frame, the send path builds one - so both directions render through the same Trace() here: same naming
	// (ProtocolCatalog), same headerData+body dump, same channel label. The only asymmetries left are the ones the wire
	// actually has (see Trace).
	//
	// This is the plain tier (the login/CS listeners): an Rpc body is opaque to it. The GameServer multiplexes a
	// sub-protocol inside its PassBlob carrier and renders a richer line for it - see GameServerPacketTracer.
	public class ConanPacketTracer : PacketTracer
	{
		private readonly Type _interfaceOpcodes;

		// `interfaceOpcodes` is this listener's s2c *Interface enum, the TX naming fall-back for the client-side classes
		// the catalog doesn't map (the login tier).
		public ConanPacketTracer(ILogger logger, string channelName, Type interfaceOpcodes) : base(logger, channelName)
		{
			_interfaceOpcodes = interfaceOpcodes;
		}

		// A raw Conan frame handed to the transport as bytes (no structured send site). Decoded back to an Rpc - which
		// stamps its FrameLength - so it renders through the one path; production sends are all structured, so this
		// costs nothing on the hot path.
		protected override void RenderTransmitted(byte[] frame)
		{
			RenderTransmitted(Rpc.Parse(frame));
		}

		protected override void RenderTransmitted(in Rpc rpc)
		{
			Trace(PacketLogFormatter.Transmitted, in rpc, _interfaceOpcodes, true);
		}

		// Inbound (RX): the listener dispatched this parsed frame. Both the method NAME (from the Receiver.Type + Opcode
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

		// Inbound (RX) with no handler: the same line, at Warning, marked UNHANDLED.
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
		// direction, the UNHANDLED shape, the byte dump - stays shared.
		protected virtual void RenderPacket(string direction, string opcodeName, in Rpc rpc)
		{
			Logger.LogPacket(direction, ChannelName, opcodeName, rpc.Body.Length, rpc.FrameLength, ("headerData", rpc.HeaderData), ("body", rpc.Body));
		}

		// The ONE render for both directions. Direction-dependent inputs are exactly two, and both are properties of the
		// wire, not of the code path: the direction tag, and the naming fall-back enum (s2c only - see ResolveName).
		private void Trace(string direction, in Rpc rpc, Type? fallbackOpcodes, bool handled)
		{
			var opcodeName = ProtocolCatalog.ResolveName(rpc.Receiver.Type, rpc.Opcode, fallbackOpcodes);

			// UNHANDLED wins over any richer line shape: the marker is the point of that line.
			if (!handled)
			{
				Logger.LogUnknownPacket(ChannelName, opcodeName, rpc.Body.Length, rpc.FrameLength, ("headerData", rpc.HeaderData), ("body", rpc.Body));
				return;
			}

			RenderPacket(direction, opcodeName, in rpc);
		}
	}
}
