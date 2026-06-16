using System.Numerics;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// The MoveTask transform packet (MoveTaskIIR 0x96C46740) - how the server tells clients where an entity is and which
	// way it faces. Decoded from the strickenbla capture (the 61-byte "at-position-facing" form, the simplest + by far
	// the most common shape; the larger 96..370-byte forms carry multi-waypoint paths and aren't built yet). Even the
	// player uses this form. The two positions are a FROM/TO segment - in the capture's stationary updates from==to; for
	// a moving NPC we send from = the previous position, to = the stepped position so the client interpolates the step.
	//
	// Wire body (the inner 0x2000 IIR record, after the u32 totalLen WriteArrayUInt32Length wraps), all big-endian:
	//   [subop 0x96C46740][0x0000C350][handle][u16 0][from: 3 floats][u8 0x02][to: 3 floats][quat: 4 floats][u16 0]
	public static class MoveTaskCodec
	{
		public const uint MoveTaskSubOpcode = (uint)GameServer0X2000RespondsOpcodes.MoveTaskIIR;
		private const uint WireType = EntityClass.Character; // the shared 0xC350 type word
		public const int BodyLength = 57;

		// The MoveTask "mode" byte (at body offset 26, after the from-position) selects the form (decoded from the
		// captures, #53): 0x00 = a bare position, 0x02 = the stationary from/to/facing below, 0x04 = a multi-waypoint
		// path (the PatrolPath source, still variable-length), 0x08 = approach/face a target entity.
		public const byte ModeBarePosition = 0x00;
		public const byte ModeStationary = 0x02;
		public const byte ModeApproachTarget = 0x08;

		// mode-00: a bare position update - [u16 0][from 3 floats][0x00][0x01] (28-byte body / 32-byte frame). The
		// simplest move; the dominant shape for the player's own position ticks. Validated 74/74.
		public static byte[] BuildBarePositionBody(uint handle, Vector3 from)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(ModeBarePosition)
				.WriteByte(0x01)
				.ToArray();
		}

		// mode-08: approach/face a target entity - [u16 0][from 3 floats][0x08][u32 0xC350 type][u32 targetHandle]
		// [0x01][9 zero bytes] (45-byte body / 49-byte frame). The NPC "turn to / move toward" a dynel. Validated 825/825.
		public static byte[] BuildApproachTargetBody(uint handle, Vector3 from, uint targetHandle)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(ModeApproachTarget)
				.WriteUInt32(WireType)
				.WriteUInt32(targetHandle)
				.WriteByte(0x01)
				.WriteUInt64(0)
				.WriteByte(0x00)
				.ToArray();
		}

		// Builds the 61-byte-frame MoveTask body for `handle` moving from `from` to `to`, facing `facing`. from==to is a
		// stationary orientation/position update (the capture's shape); from!=to is a step the client walks along.
		public static byte[] BuildBody(uint handle, Vector3 from, Vector3 to, Quaternion facing)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(0x02)
				.WriteFloatBigEndian(to.X).WriteFloatBigEndian(to.Y).WriteFloatBigEndian(to.Z)
				.WriteFloatBigEndian(facing.X).WriteFloatBigEndian(facing.Y).WriteFloatBigEndian(facing.Z).WriteFloatBigEndian(facing.W)
				.WriteUInt16(0)
				.ToArray();
		}
	}
}
