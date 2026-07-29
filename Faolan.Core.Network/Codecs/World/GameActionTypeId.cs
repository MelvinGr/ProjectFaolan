using System;

namespace Faolan.Core.Network.Codecs.World
{
	// The GameAction type-id space, as the CLIENT bounds it.
	//
	// Every reader asserts `i_nGameAction >= e_Type_GC_GameAction && i_nGameAction < e_Type_GC_GameAction_End`
	// (SpellData.cpp:1577) and compiles it to `(id - 0xCF09) <= 0x11D`, so the valid ids are 0xCF09..0xD026 - 286 of
	// them, 260 registered. Failing it is not a dropped packet: it pops an "Assertion Failed" dialog and takes the
	// client down.
	//
	// ⚠️ This is not theoretical - we sent 0xD12D and it crashed the client (2026-07-29). That id came from reading the
	// descriptor table with a stride of 0x41 (what tools/ghidra/DumpGameActionTypes.java used). The real stride is
	// 0x104 (FUN_008c3910: `IMUL EAX,EAX,0x104; ADD EAX,0x25077c8`), and because 0x104 == 4 * 0x41 an out-by-4x index
	// still looked perfectly "aligned" - so every id derived that way was four times too far up the table. The
	// 68-entry docs/re/gameaction_types.json's `typeId` column has the same 4x error; trust its `enumId`, the value at
	// descriptor+8, or docs/re/gameaction-type-ids.tsv.
	//
	// The lesson worth keeping: for a table index, validate against the client's own BOUNDS, never against another
	// derivation of the same quantity - two derivations sharing an error agree perfectly and prove nothing.
	public static class GameActionTypeId
	{
		public const uint First = 0xCF09; // e_Type_GC_GameAction
		public const uint End = 0xD027;   // e_Type_GC_GameAction_End (exclusive)

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
