using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;


namespace Faolan.Core.Network.Logging
{
	// The GameServer channel's tracer: the Conan trace plus the one thing only this tier has - the PassBlob carrier,
	// which multiplexes the IIR sub-protocol inside its body. A PassBlob line names the IIR it carries, which is what a
	// reader of the trace actually wants to see; every other tier's body is opaque, so they use the base tracer.
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

		// A PassBlob carrier's body starts [u32 payloadLength][u32 iirType]. Gated on the receiver CLASS as well as the
		// method: method 0 is PassBlob only on the GameAgent (c2s) / GameInterface (s2c) pair, and names a completely
		// different method on every other class (GameCharAgent.RequestAggroStatus, GameCharInterface.
		// DispatchUpdateAggroStatus, …), whose body would then be mis-read as an IIR header.
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

			// #575: the opcode and receiver class are NOT enough to know this body is an IIR carrier. The same
			// (PassBlob, GameInterface) pair also carries the remoteformat FEEDBACK payloads, whose body starts with an
			// ID32 - and reading one of those as a header printed `subOpcode=0x0000C350, payloadLength=2371521387`
			// against a 528-byte body. Five of them land between a level-up ding and its AttributeChanged lines, so the
			// trace looked like a malformed burst at exactly the moment a client disconnected. `0xC350` is an ID32, not
			// a length or an opcode - the same trap as the 0x3F1 list-count word.
			//
			// ⚠️ The ENDPOINT cannot tell them apart, so do not try to narrow the receiver check instead. The two
			// senders are otherwise identical - same sender endpoint, same `RpcInterfaces.GameInterfaceType` receiver, same
			// DispatchPassBlob opcode; compare CombatService.SendWorldPacket with SendWorldFeedback. The ONLY structural
			// difference is that a carrier's body goes through `WriteArrayUInt32Length` and a feedback body does not.
			//
			// So the length IS the discriminator, not a heuristic standing in for one: the declared payload is
			// everything after that length word, making `bodyLength == payloadLength + 4` true of every genuine carrier
			// and of nothing else. Deliberately NOT a check that the type is a known IirTypes - an UNKNOWN IIR is
			// exactly what a trace should still show as raw hex; it is the length that says whether this is a carrier.
			if (declared + (long)sizeof(uint) != rpc.Body.Length)
				return false;

			payloadLength = declared;
			iir = body.ReadU32Be(sizeof(uint));
			return true;
		}
	}
}
