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
