using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The continuous short-form BehaviorControlCenterIIR (0x642CD3D6) "AI / idle behavior pulse" retail streams ~10/s
	// for every rendered entity (#225). It is what keeps a standing creature playing its IDLE animation instead of
	// freezing - without it our NPCs/pets stand static. NOT required for rendering (#23), pure fidelity + the seed of
	// the simulation AI layer. Decoded byte-exact from the mount capture (frame 105):
	//   [subop 0x642CD3D6][u32 type=0xC350][u32 actor][u16 0x0004][u32 behaviorId=7][u32 payloadLen] then the payload:
	//     [08 c5 c3 02]  (field 1 = the idle-behavior constant)
	//     [18 <varint tick>]  (field 3 = a MONOTONIC per-stream tick - the payloadLen grows as the varint widens)
	//     [28 18]  (field 5 = 0x18)
	//     [30 00]  (field 6 = 0)
	public static class BehaviorHeartbeatCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;

		// The idle-behavior constant in args field 1 (the capture's `08 c5 c3 02` varint).
		private const uint IdleBehaviorAction = 41413;
		private const uint IdleBehaviorId = 7;

		public static byte[] Build(uint actor, uint tick)
		{
			var payload = new CommandMessage
			{
				BccActionId = IdleBehaviorAction,
				CommandqueueServerId = tick, // the monotonic per-stream tick
				Flags = 0x18,
				ExecutionStatus = 0
			}.ToProtobuf();

			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(actor)
				.WriteUInt16(0x0004)
				.WriteUInt32(IdleBehaviorId)
				.WriteArrayUInt32Length(payload)
				.ToArray();
		}
	}
}
