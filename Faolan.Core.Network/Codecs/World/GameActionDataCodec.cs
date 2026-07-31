using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// The GameActionData ELEMENT grammar - the positional body the client's deserializer FUN_00899a60 consumes inside
	// a GameActionListIIR (0xF508F4C1). One definition, because two subsystems now build one (spell-recast overrides
	// and the GM flymode toggle) and a second hand-rolled copy of a positional grammar is how the two drift apart.
	// A class-(c) sub-record codec (#530 rule 5c): composed by GameActionListCodec, so it declares no opcode.
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
	public static class GameActionDataCodec
	{
		// GameActionData version word - the deserializer validates it == 4 ("Invalid GameAction data version").
		public const uint Version = 4;

		// Byte length of the element prefix through the first fixed int (0x2d5):
		// [typeId][field][version][subCount][0x2d5]. GameActionListCodec's own header already writes exactly these
		// five words, so a list body slices the element from here on.
		public const int PrefixThrough0x2d5 = 5 * sizeof(uint);

		// The GameActionData ctor's seed for the fourth fixed int (0x2f5). ⚠️ EVERY captured element carries 9 here, not
		// this 0 - see CtorSeed0x2f5 vs CapturedSeed0x2f5. The recast builder documents that its handler ignores the
		// value; a handler we have NOT read might not, so a type being sent for the first time should prefer the
		// captured seed.
		public const uint CtorSeed0x2f5 = 0;

		// What all four captured GameActionList elements carry at 0x2f5 (GameActionListCodecTests' emote, numeric,
		// template-choice and world-entry bodies).
		public const uint CapturedSeed0x2f5 = 9;

		// The two fixed ints between 0x2d5 and 0x2f5, constant in every element we emit.
		private const uint Fixed0x2d6 = 0;
		private const uint Fixed0x2f2 = 1;

		// The first fixed int (0x2d5), constant in every element we emit.
		private const uint Fixed0x2d5 = 1;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GameActionData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// One all-u32 element from its typeId + descriptor-ordered keyList VALUES. (String keys 0x4d9/0x4da are not
		// modelled - every setter we emit is all-u32.)
		public static byte[] Build(uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues, uint seed0x2f5 = CtorSeed0x2f5)
		{
			return Build(new GameActionData(typeId, field, subCount, keyValues, seed0x2f5));
		}

		public static GameActionData Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GameActionDataCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static GameActionData Read(ref SpanReader r)
		{
			var typeId = r.ReadU32Be();
			GameActionTypeId.Validate(typeId, nameof(GameActionDataCodec));
			var field = r.ReadU32Be();
			r.ExpectU32Be(Version, "GameActionData version");
			var subCount = r.ReadU32Be();
			r.ExpectU32Be(Fixed0x2d5, "fixed int 0x2d5");
			r.ExpectU32Be(Fixed0x2d6, "fixed int 0x2d6");
			r.ExpectU32Be(Fixed0x2f2, "fixed int 0x2f2");
			var seed = r.ReadU32Be();

			// The descriptor's key list is not on the wire, so the element runs to the end of whatever it was handed.
			var keyValues = new List<uint>(r.Remaining / sizeof(uint));
			while (r.Remaining >= sizeof(uint))
				keyValues.Add(r.ReadU32Be());
			return new GameActionData(typeId, field, subCount, keyValues, seed);
		}

		internal static void Write(FaolanStream s, GameActionData v)
		{
			// An out-of-range type is an ASSERT DIALOG in the client, not a dropped packet - fail here instead
			// (SpellData.cpp:1577; we shipped 0xD12D once and it took the client down).
			GameActionTypeId.Validate(v.TypeId, nameof(GameActionDataCodec));
			s.WriteUInt32(v.TypeId)
				.WriteUInt32(v.Field)
				.WriteUInt32(Version)
				.WriteUInt32(v.SubCount)
				.WriteUInt32(Fixed0x2d5)
				.WriteUInt32(Fixed0x2d6)
				.WriteUInt32(Fixed0x2f2)
				.WriteUInt32(v.Seed0x2f5);
			foreach (var value in v.KeyValues)
				s.WriteUInt32(value);
		}
	}

	// One GameActionData element. `KeyValues` are the descriptor-ordered values for this type; the keys themselves
	// live in the client, not on the wire.
	public sealed record GameActionData(uint TypeId, uint Field, uint SubCount, IReadOnlyList<uint> KeyValues, uint Seed0x2f5 = GameActionDataCodec.CtorSeed0x2f5);
}
