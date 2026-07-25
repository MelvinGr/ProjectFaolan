using System;

namespace Faolan.Core.Enums
{
	// The `flag1` byte of a CharDCMoveIIR (0x4517AA81) - a LAYOUT BITFIELD, not a movement mode.
	//
	// This was a `MovingType` enum with invented names (Walk / Jump / Sprint / MountedRun / Falling / ...) assembled
	// from whichever byte pairs happened to appear in a capture, which the move handler switched on with a
	// fall-through list of the known ones. Two things were wrong with that:
	//
	//   * The u16 it named is really `[u8 env][u8 flag1]` (docs/re/chardcmove-grammar.md). Folding the envelope byte
	//     into the "type" forces a separate constant per (env, flag1) pair - which is why that enum carried both
	//     0x011A and 0x001A for one flag value, while its other nine entries only ever covered env=0x01.
	//   * flag1 is a bitfield the client's own reader (FUN_007ad6b0) uses to decide WHICH FIELDS FOLLOW, so every
	//     combination is legal and enumerating the observed ones can only be incomplete. Measured across the captures:
	//     **19 distinct flag1 values over both env bytes (37 pairs)**, of which the 11-entry enum matched 14,414 of
	//     19,034 records - the other **4,620 (24%) hit `default:` and were logged and DROPPED**, i.e. that movement
	//     was silently ignored.
	//
	// So there is nothing to name at this level, and the movement "mode" (walking, mounted, falling) is not encoded
	// here at all. The bits below are the reader's own.
	[Flags]
	public enum MoveFlag : byte
	{
		None = 0,

		// bit0 - toggles freely with no effect on the layout (the doc's 0x1A/0x1B twins are the same length). Never
		// observed SET: all 19 captured values are even.
		Unused0 = 0x01,

		// CLEAR => the param7..9 heading vector follows.
		NoHeadingVector = 0x02,

		// CLEAR => the param10..12 velocity vector follows.
		NoVelocityVector = 0x04,

		// CLEAR => the param13..15 acceleration vector follows.
		NoAccelerationVector = 0x08,

		// SET => the post-position move vector collapses to ONE float, which with param6 forms a unit-length 2D facing
		// (facingX^2 + param6^2 ~= 1.0 in 11,785 of 11,884 collapsed records).
		CollapsedFacing = 0x10,

		// Bits 5 and 6 are stored as bools by the reader and their meaning is NOT pinned. Named for what they are
		// rather than guessed at: the old enum implied "jump"/"mounted" by pairing them with those labels, but 0x52 and
		// 0x56 both occur while a player is SEATED, where a jump reading makes no sense.
		Bool5 = 0x20,

		// AIRBORNE - a jump. Pinned by xxx_2clients_jumping, a session recorded for nothing else: the bit appears in c2s
		// moves ONLY there (20 records, none in a comparable non-jumping capture), and retail relays it - both clients
		// received 0x40 moves naming the OTHER subject, plus five more characters including a passer-by who happened to
		// jump nearby. Walking is 0x1E; jumping is 0x56, the same minus NoAccelerationVector plus this bit, which fits an
		// acceleration vector arriving exactly as the character leaves the ground.
		//
		// It stayed unnamed until that capture for a good reason: the old invented enum paired 0x52/0x56 with a "jump"
		// label while those same values occur while SEATED, so the pair was not diagnostic. Isolating a jumping session
		// is what makes THIS BIT diagnostic on its own.
		Airborne = 0x40
	}
}
