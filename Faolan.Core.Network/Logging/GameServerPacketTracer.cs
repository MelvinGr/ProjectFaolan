using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

// CS8620 here is a Roslyn false positive: passing (string, ReadOnlyMemory<byte>) tuples to the params tuple-array on the
// LogPacketWithSubOpcode extension member trips a nullability-inference bug even when the argument and parameter tuple
// types are identical (verified). The values are non-null - suppress it (see PacketLogFormatter).
#pragma warning disable CS8620

namespace Faolan.Core.Network.Logging
{
	// The GameServer channel's tracer: the Conan trace plus the one thing only this tier has - the PassBlob carrier,
	// which multiplexes the IIR sub-protocol inside its body. A PassBlob line names the IIR it carries, which is what a
	// reader of the trace actually wants to see; every other tier's body is opaque, so they use the base tracer.
	public sealed class GameServerPacketTracer : ConanPacketTracer
	{
		public GameServerPacketTracer(ILogger logger) : base(logger, "GameServer", typeof(GameCharInterfaceOpcodes))
		{
		}

		protected override void RenderPacket(string direction, string opcodeName, in Rpc rpc)
		{
			if (!TryReadIirHeader(in rpc, out var payloadLength, out var iir))
			{
				base.RenderPacket(direction, opcodeName, in rpc);
				return;
			}

			Logger.LogPacketWithSubOpcode(direction, ChannelName, opcodeName, PacketLogFormatter.OpcodeName(typeof(IirTypes), iir), payloadLength, rpc.Body.Length, rpc.FrameLength,
				("headerData", rpc.HeaderData), ("body", rpc.Body));
		}

		// A PassBlob carrier's body starts [u32 payloadLength][u32 iirType]. Gated on the receiver CLASS as well as the
		// opcode: 0x2000 is PassBlob only on the GameAgent (c2s) / GameInterface (s2c) pair, and names a completely
		// different method on every other class (GameCharAgent.RequestAggroStatus, GameCharInterface.
		// DispatchUpdateAggroStatus, …), whose body would then be mis-read as an IIR header.
		private static bool TryReadIirHeader(in Rpc rpc, out uint payloadLength, out uint iir)
		{
			payloadLength = 0;
			iir = 0;

			if (rpc.Opcode != (ushort)GameAgentOpcodes.PassBlob)
				return false;

			if (rpc.Receiver.Type != Endpoints.GameAgentType && rpc.Receiver.Type != Endpoints.GameInterfaceType)
				return false;

			var body = rpc.Body.Span;
			if (body.Length < sizeof(uint) * 2)
				return false;

			payloadLength = body.ReadU32Be(0);
			iir = body.ReadU32Be(sizeof(uint));
			return true;
		}
	}
}
