using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;


namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     The GameServer channel's tracer: the Conan trace plus the one thing only this tier has, the PassBlob carrier
	///     that multiplexes the IIR sub-protocol inside its body.
	/// </summary>
	/// <remarks>
	///     A PassBlob line names the IIR it carries. Every other tier's body is opaque, so they use the base tracer.
	/// </remarks>
	public sealed class GameServerPacketTracer : ConanPacketTracer
	{
		public GameServerPacketTracer(ILogger logger) : base(logger, "GameServer", typeof(GameCharInterfaceMethods))
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
				rpc.Body);
		}

		/// <summary>
		///     Reads a PassBlob carrier's <c>[u32 payloadLength][u32 iirType]</c> header.
		/// </summary>
		/// <remarks>
		///     Gated on the receiver class as well as the method: method 0 is PassBlob only on the GameAgent (c2s) /
		///     GameInterface (s2c) pair, and names a completely different method on every other class
		///     (<c>GameCharAgent.RequestAggroStatus</c>, <c>GameCharInterface.DispatchUpdateAggroStatus</c>, …), whose
		///     body would then be mis-read as an IIR header.
		/// </remarks>
		private static bool TryReadIirHeader(in Rpc rpc, out uint payloadLength, out uint iir)
		{
			payloadLength = 0;
			iir = 0;

			if (rpc.Method != (uint)GameAgentMethods.PassBlob)
				return false;

			if (rpc.Receiver.Type != RpcInterfaces.GameAgentType && rpc.Receiver.Type != RpcInterfaces.GameInterfaceType)
				return false;

			var body = rpc.Body.Span;
			if (body.Length < sizeof(uint) * 2)
				return false;

			var declared = body.ReadU32Be(0);

			// The opcode and receiver class are not enough to know this body is an IIR carrier: the same
			// (PassBlob, GameInterface) pair also carries the remoteformat feedback payloads, whose body starts with an
			// ID32. Reading one of those as a header prints a nonsense sub-opcode and payload length.
			//
			// The endpoint cannot tell the two apart, so do not narrow the receiver check instead. The senders are
			// otherwise identical - same sender endpoint, same `RpcInterfaces.GameInterfaceType` receiver, same
			// DispatchPassBlob opcode; compare CombatService.SendWorldPacket with SendWorldFeedback. The only
			// structural difference is that a carrier's body goes through `WriteArrayUInt32Length` and a feedback body
			// does not.
			//
			// The length is therefore the discriminator rather than a heuristic standing in for one: the declared
			// payload is everything after that length word, making `bodyLength == payloadLength + 4` true of every
			// genuine carrier and of nothing else. Deliberately not a check that the type is a known IirTypes value -
			// an unknown IIR is exactly what a trace should still show as raw hex.
			if (declared + (long)sizeof(uint) != rpc.Body.Length)
				return false;

			payloadLength = declared;
			iir = body.ReadU32Be(sizeof(uint));
			return true;
		}
	}
}
