using System;
using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Builds a spell-cooldown OVERRIDE as a GameActionData element inside a GameActionListIIR (0xF508F4C1) - the wire
	// mechanism by which the retail server sets a spell's recast timer on the client (#271). The element grammar is
	// POSITIONAL and fully recovered: from the client deserializer FUN_00899a60 plus the live Frida type catalog
	// Captures/re/gameaction_types.json (docs/re/gameactionlist-grammar.md). An element is:
	//
	//   [u32 typeId][u32 field][u32 version=4][u32 subCount]
	//   [u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]     // four ALWAYS-present positional int values
	//   <descriptor keyList values, in this type's order; u32 unless the key is 0x4d9/0x4da = std::string>
	//
	// The keys are NOT on the wire (they live in the client's static per-type descriptor); the wire carries only the
	// VALUES, in the type's order. For ChangeRecastTime (0xcf81) that order is [0x4c3 = spell id][0x2f9 = recast],
	// recast in CENTISECONDS (client applies recast x 0.01 s, DAT_01f2800c; FUN_007e1080 "GameCode/Magic/Recast").
	//
	// The recast handler (FUN_008b0f50, "GameCode/GameAction/SetSpellRecastTime") reads ONLY the keyList values
	// (spell + 0x2f9); it never reads the four fixed ints, so those carry the GameActionData ctor's default seeds
	// (FUN_008985c0: 0x2d5=1, 0x2d6=0, 0x2f2=1; 0x2f5=0 - a real create-editor element carried 0x2f5=9, but the recast
	// handler ignores it, and any value parses). subCount=0 (no sub-records) and field=0 for a bare override.
	//
	// CONSTRUCT-ONLY, deliberately not wired to the cast path: retail does NOT emit a recast on a normal cast - base
	// cooldowns are client-side, read from the client's own RDB when a spell is granted (#271, 2026-06-22). This
	// exists for the OPTIONAL server-override case (cooldown resets / AA reductions) and to prove the element is
	// byte-constructible now that the type catalog is recovered; it awaits an in-client / capture check before it
	// ships on the wire.
	public static class SpellRecastActionBuilder
	{
		// GameAction wire type ids (Captures/re/gameaction_types.json). ChangeRecastTime is the direct per-spell recast
		// override; the SpellStatModifier family (0xcfdd/0xd009) is what FUN_008b0f50 Applies, via key 0x463 instead.
		public const uint ChangeRecastTimeType = 0xCF81; // keyList: [0x4c3 spell, 0x2f9 recast]

		// GameActionData version word (obj+0x14 area; validated == 4 by the deserializer, else "Invalid GameAction data version").
		public const uint Version = 4;

		// The four ALWAYS-present positional int values, in wire order = the ctor default seeds (the recast handler
		// ignores them). Keys shown for documentation; only the values reach the wire.
		private static readonly uint[] FixedInts = [/*0x2d5*/ 1, /*0x2d6*/ 0, /*0x2f2*/ 1, /*0x2f5*/ 0];

		// Byte length of an element prefix through the first fixed int (0x2d5): [typeId][field][version][subCount][0x2d5].
		private const int PrefixThrough0x2d5 = 5 * sizeof(uint);

		// Builds a single ChangeRecastTime GameActionData ELEMENT (the positional body FUN_00899a60 consumes): set
		// `spellId`'s recast timer to `recastCentiseconds` on the receiving client.
		public static byte[] BuildChangeRecastTimeElement(uint spellId, uint recastCentiseconds)
		{
			// ChangeRecastTime keyList value order: 0x4c3 (spell), 0x2f9 (recast centiseconds).
			return BuildElement(ChangeRecastTimeType, field: 0, subCount: 0, keyValues: [spellId, recastCentiseconds]);
		}

		// Builds any single all-u32 GameActionData element from its typeId + descriptor-ordered keyList VALUES. (String
		// keys 0x4d9/0x4da are not modelled here - the recast/stat-modifier setters are all-u32.)
		public static byte[] BuildElement(uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues)
		{
			ArgumentNullException.ThrowIfNull(keyValues);
			var s = new FaolanStream()
				.WriteUInt32(typeId)
				.WriteUInt32(field)
				.WriteUInt32(Version)
				.WriteUInt32(subCount);
			foreach (var value in FixedInts)
				s.WriteUInt32(value);
			foreach (var value in keyValues)
				s.WriteUInt32(value);
			return s.ToArray();
		}

		// Wraps a ChangeRecastTime element as a full GameActionListIIR body (a 1-element 0x3F1 list) delivered to
		// `frameHandle`. Reuses GameActionListCodec's list framing: it writes [subop][c350][frame][0][0x7E2][typeId]
		// [field][version 4][u64 1 = subCount 0 + 0x2d5 value 1], so the "action tail" it appends is the element from
		// 0x2d6 onward. Building the whole element first and slicing keeps the two construction paths in agreement
		// (asserted by the round-trip test).
		public static byte[] BuildChangeRecastTimeBody(uint frameHandle, uint spellId, uint recastCentiseconds)
		{
			var element = BuildChangeRecastTimeElement(spellId, recastCentiseconds);
			return GameActionListCodec.BuildBody(frameHandle, ChangeRecastTimeType, field3: 0, element[PrefixThrough0x2d5..]);
		}
	}
}
