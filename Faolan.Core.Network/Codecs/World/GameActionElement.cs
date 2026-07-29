using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.World
{
	// The GameActionData ELEMENT grammar - the positional body the client's deserializer FUN_00899a60 consumes inside
	// a GameActionListIIR (0xF508F4C1). One definition, because two subsystems now build one (spell-recast overrides
	// and the GM flymode toggle) and a second hand-rolled copy of a positional grammar is how the two drift apart.
	//
	//   [u32 typeId][u32 field][u32 version=4][u32 subCount]
	//   [u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]     // four ALWAYS-present positional int values
	//   <descriptor keyList values, in this type's order; u32 unless the key is 0x4d9/0x4da = std::string>
	//
	// The KEYS are not on the wire - they live in the client's static per-type descriptor, so the wire carries only
	// the VALUES in that type's order. A type with an empty key list therefore emits nothing after the four fixed
	// ints, which is the shape of a parameterless toggle.
	//
	// ⚠️ The descriptor table (0x025077C8, stride 0x41) is BSS - it is filled at RUNTIME, so a static Ghidra read of a
	// key list returns zeros. Key lists come from a live Frida dump (docs/re/gameaction_types.json); a type absent
	// from that dump has an UNVERIFIED key list, and an empty one is an assumption to be proven in-client, not a fact.
	public static class GameActionElement
	{
		// GameActionData version word - the deserializer validates it == 4 ("Invalid GameAction data version").
		public const uint Version = 4;

		// Byte length of the element prefix through the first fixed int (0x2d5):
		// [typeId][field][version][subCount][0x2d5]. GameActionListCodec.BuildBody already writes exactly these five
		// words (as dynelHandle/field3/ConstFour/ConstOne), so a body slices the element from here on.
		public const int PrefixThrough0x2d5 = 5 * sizeof(uint);

		// The GameActionData ctor's seed for the fourth fixed int (0x2f5). ⚠️ EVERY captured element carries 9 here, not
		// this 0 - see CtorSeed0x2f5 vs CapturedSeed0x2f5. The recast builder documents that its handler ignores the
		// value; a handler we have NOT read might not, so a type being sent for the first time should prefer the
		// captured seed.
		public const uint CtorSeed0x2f5 = 0;

		// What all four captured GameActionList elements carry at 0x2f5 (GameActionListCodecTests' emote, numeric,
		// template-choice and world-entry bodies).
		public const uint CapturedSeed0x2f5 = 9;

		// The ReadSubClass trailer: GameActionListIIR_t::ReadSubClass (FUN_008679f0) reads the element list and THEN a
		// [u8 flag] (+0x3c) and a [u32] (+0x40). Every captured body ends in those five zero bytes, so a CONSTRUCTED
		// body has to carry them or the client reads past the record.
		public static readonly byte[] ReadSubClassTrailer = [0x00, 0x00, 0x00, 0x00, 0x00];

		// Builds a single all-u32 GameActionData element from its typeId + descriptor-ordered keyList VALUES.
		// (String keys 0x4d9/0x4da are not modelled - every setter we emit is all-u32.)
		public static byte[] Build(uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues, uint seed0x2f5 = CtorSeed0x2f5)
		{
			ArgumentNullException.ThrowIfNull(keyValues);
			// An out-of-range type is an ASSERT DIALOG in the client, not a dropped packet - fail here instead
			// (SpellData.cpp:1577; we shipped 0xD12D once and it took the client down).
			GameActionTypeId.Validate(typeId, nameof(GameActionElement));
			var s = new FaolanStream().WriteUInt32(typeId).WriteUInt32(field).WriteUInt32(Version).WriteUInt32(subCount)
				.WriteUInt32(1)         // 0x2d5
				.WriteUInt32(0)         // 0x2d6
				.WriteUInt32(1)         // 0x2f2
				.WriteUInt32(seed0x2f5); // 0x2f5
			foreach (var value in keyValues)
				s.WriteUInt32(value);
			return s.ToArray();
		}

		// Wraps one element as a complete GameActionListIIR body delivered to `frameHandle`. BuildBody writes the
		// element's first five words itself, so only the remainder rides as the action tail - building the whole
		// element first and slicing keeps the two construction paths in agreement.
		// `trailer` appends the ReadSubClass [u8][u32] every captured body ends with. It defaults to FALSE only because
		// the pre-existing recast builder was written without it and is construct-only (never sent); anything actually
		// going on the wire wants it true.
		public static byte[] BuildBody(uint frameHandle, uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues,
			uint seed0x2f5 = CtorSeed0x2f5, bool trailer = false)
		{
			var element = Build(typeId, field, subCount, keyValues, seed0x2f5);
			var tail = element[PrefixThrough0x2d5..];
			if (trailer)
				tail = [.. tail, .. ReadSubClassTrailer];
			return GameActionListCodec.BuildBody(frameHandle, typeId, field, tail);
		}
	}
}
