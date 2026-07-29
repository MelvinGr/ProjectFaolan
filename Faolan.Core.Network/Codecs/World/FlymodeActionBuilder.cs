using System;

namespace Faolan.Core.Network.Codecs.World
{
	// The GM FLY toggle, as a GameActionData element inside a GameActionListIIR (0xF508F4C1).
	//
	// Flying is CLIENT-SIDE state, not a wire field: the client models it as "alignment mode" 5 in its movement state
	// machine (`vehiclealignment.cpp`; the modes are 0=Normal 1=Climbing 2=Dead 3=Diving 4=Falling 5=Flying
	// 6=Knockback 7=Swimming, from the mode factory FUN_00a3cbc0). The setter FUN_00a29f20 writes the flag at
	// object+0x1BC and switches the mode; nothing about it rides CharDCMove, whose "MovingType" is a layout bitfield
	// with no movement-mode enum at all (docs/re/chardcmove-grammar.md).
	//
	// ⚠️ The client can already do this WITHOUT us, and that is the ROUTE THAT WORKS: `SkywalkingCommand` is a plain
	// client-local chat command - verb `/luke` (Luke Skywalker; the verb is an LDB string, which is why nothing in the
	// exe greps as "fly") - whose execute FUN_013f6130 sends no packet, it just toggles the same object+0x1BC flag.
	// Confirmed in-client 2026-07-29. This GameAction is the SERVER-driven alternative, kept because a server that
	// wants to GRANT flight (rather than the player toggling it) needs a lever of its own.
	//
	// The type id is `e_Type_GC_GameAction_StartFlymode` = 0xCF92, read from the descriptor table FUN_008bdf40
	// initialises: the id lives at descriptor+8, and equivalently descriptor = 0x025077C8 + (id - 0xCF09) * 0x104
	// (StartFlymode registers at 0x025102EC => index 137 => 0xCF92). Both readings agree for all 171 inline
	// registrations, and the result agrees with docs/re/gameaction_types.json's `enumId` on all 68 shared names.
	// ⚠️ That file's `typeId` COLUMN is not a wire id - it is 4x-inflated (see GameActionTypeId.End).
	public static class FlymodeActionBuilder
	{
		// e_Type_GC_GameAction_StartFlymode. See the class comment for the derivation.
		public const uint StartFlymodeType = 0xCF92;

		// Builds the StartFlymode body delivered to `frameHandle` (the receiving player's instance). Unlike the
		// construct-only recast builder this is actually SENT, so it mirrors what real captured elements carry rather
		// than the ctor defaults: 0x2f5 = 9 (all four captured bodies) and the ReadSubClass [u8][u32] trailer (every
		// captured body ends in those five zero bytes).
		public static byte[] BuildStartFlymodeBody(uint frameHandle)
		{
			return GameActionElement.BuildBody(frameHandle, StartFlymodeType, 0, 0, [],
				GameActionElement.CapturedSeed0x2f5, trailer: true);
		}
	}

	// The GameAction type-id space, as the CLIENT bounds it.
	//
	// Every reader asserts `i_nGameAction >= e_Type_GC_GameAction && i_nGameAction < e_Type_GC_GameAction_End`
	// (SpellData.cpp:1577) and compiles it to `(id - 0xCF09) <= 0x11D`, so the valid ids are 0xCF09..0xD026 - 286 of
	// them. Failing it pops an "Assertion Failed" dialog and takes the client down.
	//
	// ⚠️ This is not theoretical: we sent 0xD12D and it crashed the client (2026-07-29). That id came from reading the
	// descriptor table with a stride of 0x41, which is what tools/ghidra/DumpGameActionTypes.java used. The real stride
	// is 0x104, and because 0x104 == 4 * 0x41 an out-by-4x index still looked perfectly "aligned" - every id derived
	// that way was four times too far up the table, which is also why the 68-entry gameaction_types.json's `typeId`
	// column (= 53005 + 4*(enumId-53002)) disagrees with its own `enumId`. Trust `enumId` / this range.
	public static class GameActionTypeId
	{
		public const uint First = 0xCF09;  // e_Type_GC_GameAction
		public const uint End = 0xD027;    // e_Type_GC_GameAction_End (exclusive)

		public static bool IsValid(uint typeId)
		{
			return typeId >= First && typeId < End;
		}

		// Throws rather than letting an out-of-range id reach the client, where it is an assert dialog and a
		// disconnect rather than a dropped packet.
		public static void Validate(uint typeId, string what)
		{
			if (!IsValid(typeId))
				throw new ArgumentOutOfRangeException(nameof(typeId), typeId,
					$"{what}: GameAction type 0x{typeId:X4} is outside the client's asserted range " +
					$"0x{First:X4}..0x{End - 1:X4} (SpellData.cpp:1577) - sending it crashes the client.");
		}
	}
}
