using System;

namespace Faolan.Core.Enums
{
	// The `flag1` byte of a CharDCMoveIIR (0x4517AA81) - a layout bitfield, not a movement mode.
	//
	// The u16 it sits in is `[u8 env][u8 flag1]` (docs/re/chardcmove-grammar.md); the envelope byte is not part of
	// the flag. flag1 is a bitfield the client's own reader (FUN_007ad6b0) uses to decide which fields follow, so
	// every combination is legal and an enumeration of observed values can only be incomplete (the captures carry 19
	// distinct flag1 values over both env bytes, 37 pairs). A handler must read the bits, never switch on the byte.
	//
	// So there is nothing to name at this level, and the movement "mode" (walking, mounted, falling) is not encoded
	// here at all. The bits below are the reader's own.
	[Flags]
	public enum MoveFlag : byte
	{
		None = 0,

		// bit0 - toggles freely with no effect on the layout (the doc's 0x1A/0x1B twins are the same length). Never
		// observed set: all 19 captured values are even.
		Unused0 = 0x01,

		// Clear => the param7..9 heading vector follows.
		NoHeadingVector = 0x02,

		// Clear => the param10..12 velocity vector follows.
		NoVelocityVector = 0x04,

		// Clear => the param13..15 acceleration vector follows.
		NoAccelerationVector = 0x08,

		// Set => the post-position rotation quaternion is sent as two floats instead of four: (qy, qw), the only
		// non-zero components of an upright character's yaw-only facing. The client's reader treats the leading part
		// as a "move vector" and the last float as a scalar, but the four floats are unit-length in 100% of the
		// corpus's records in both forms, and the yaw implied by the collapsed pair (2*atan2(qy, qw)) tracks the
		// direction of travel. See CharDCMove.Rotation.
		CollapsedRotation = 0x10,

		// Bit 5 is stored as a bool by the reader and its meaning is not pinned. Named for what it is rather than
		// guessed at: 0x52 and 0x56 both occur while a player is seated, so a "jump"/"mounted" reading does not hold.
		Bool5 = 0x20,

		// Airborne - a jump. Pinned by xxx_2clients_jumping, a session recorded for nothing else: the bit appears in c2s
		// moves only there (20 records, none in a comparable non-jumping capture), and retail relays it - both clients
		// received 0x40 moves naming the other subject, plus five more characters including a passer-by who happened to
		// jump nearby. Walking is 0x1E; jumping is 0x56, the same minus NoAccelerationVector plus this bit, which fits an
		// acceleration vector arriving exactly as the character leaves the ground. The value pair 0x52/0x56 is not
		// diagnostic on its own (both occur while seated); the isolated jumping session is what makes this bit so.
		Airborne = 0x40
	}
}
