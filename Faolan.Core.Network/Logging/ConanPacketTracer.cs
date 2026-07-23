using System;
using System.Buffers.Binary;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// The Conan-tier (GameServer / login) packet tracer. Every Conan frame is an Rpc in BOTH directions - the parser
	// yields one for a received frame, the send path builds one - so both directions render through the same Trace()
	// here: same naming (ProtocolCatalog), same PassBlob/IIR sub-opcode handling, same headerData+body dump, same
	// channel label. The only asymmetries left are the ones the wire actually has (see Trace).
	public sealed class ConanPacketTracer : PacketTracer
	{
		private readonly Type? _iirOpcodes;
		private readonly Type _interfaceOpcodes;

		// `interfaceOpcodes` is this listener's s2c *Interface enum, the TX naming fall-back for the client-side classes
		// the catalog doesn't map (the login tier). `iirOpcodes` is the IIR type catalog used to name the sub-opcode
		// inside a PassBlob carrier (the GameServer tier; null for the tiers that carry no IIRs).
		public ConanPacketTracer(ILogger logger, string channelName, Type interfaceOpcodes, Type? iirOpcodes = null) : base(logger, channelName)
		{
			_interfaceOpcodes = interfaceOpcodes;
			_iirOpcodes = iirOpcodes;
		}

		// Inbound (RX): the listener dispatched this parsed frame. Both the method NAME (from the Receiver.Type + Opcode
		// pair) and the frame length ride on the Rpc itself, so the listener passes nothing but the packet.
		public void Received(in Rpc packet)
		{
			try
			{
				Trace(PacketLogFormatter.Received, in packet, packet.FrameLength, null, true);
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
				Trace(PacketLogFormatter.Received, in packet, packet.FrameLength, null, false);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// A raw Conan frame handed to the transport as bytes (no structured send site). Decoded back to an Rpc so it
		// renders through the one path - production sends are all structured, so this costs nothing on the hot path.
		protected override void RenderTransmitted(byte[] frame)
		{
			RenderTransmitted(PacketParser.Parse(frame), frame.Length);
		}

		protected override void RenderTransmitted(in Rpc rpc, int frameLength)
		{
			Trace(PacketLogFormatter.Transmitted, in rpc, frameLength, _interfaceOpcodes, true);
		}

		// The ONE render for both directions. Direction-dependent inputs are exactly two, and both are properties of the
		// wire, not of the code path: the direction tag, and the naming fall-back enum (s2c only - see ResolveName).
		private void Trace(string direction, in Rpc rpc, int frameLength, Type? fallbackOpcodes, bool handled)
		{
			var opcodeName = ProtocolCatalog.ResolveName(rpc.Receiver.Type, rpc.Opcode, fallbackOpcodes);

			// UNHANDLED wins over the sub-opcode line shape: the marker is the point of that line.
			if (!handled)
			{
				Logger.LogUnknownPacket(ChannelName, opcodeName, rpc.Body.Length, frameLength, ("headerData", rpc.HeaderData), ("body", rpc.Body));
				return;
			}

			if (TryReadIirHeader(in rpc, out var payloadLength, out var iir))
			{
				Logger.LogPacketWithSubOpcode(direction, ChannelName, opcodeName, PacketLogFormatter.OpcodeName(_iirOpcodes!, iir), payloadLength, rpc.Body.Length, frameLength,
					("headerData", rpc.HeaderData), ("body", rpc.Body));

				return;
			}

			Logger.LogPacket(direction, ChannelName, opcodeName, rpc.Body.Length, frameLength, ("headerData", rpc.HeaderData), ("body", rpc.Body));
		}

		// A PassBlob carrier's body starts [u32 payloadLength][u32 iirType] - the IIR the packet actually carries, which
		// is what a reader of the trace wants to see. Gated on the receiver CLASS as well as the opcode: 0x2000 is
		// PassBlob only on the GameAgent (c2s) / GameInterface (s2c) pair, and names a completely different method on
		// every other class (GameCharAgent.RequestAggroStatus, GameCharInterface.DispatchUpdateAggroStatus, …), whose
		// body would then be mis-read as an IIR header.
		private bool TryReadIirHeader(in Rpc rpc, out uint payloadLength, out uint iir)
		{
			payloadLength = 0;
			iir = 0;

			if (_iirOpcodes is null || rpc.Opcode != (ushort)GameAgentOpcodes.PassBlob)
				return false;

			if (rpc.Receiver.Type != Endpoints.GameAgentType && rpc.Receiver.Type != Endpoints.GameInterfaceType)
				return false;

			var body = rpc.Body.Span;
			if (body.Length < sizeof(uint) * 2)
				return false;

			payloadLength = BinaryPrimitives.ReadUInt32BigEndian(body);
			iir = BinaryPrimitives.ReadUInt32BigEndian(body[sizeof(uint)..]);
			return true;
		}
	}
}
