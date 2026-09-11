using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The GameActionData element grammar - the positional body the client's deserializer FUN_00899a60 consumes inside
	///     a GameActionListIIR. A sub-record codec, composed by <see cref="GameActionListCodec" />, so it declares no
	///     opcode.
	///     <code>
	///     [u32 typeId][u32 field][u32 version=4][u32 subCount]      field is a GameActionInstanceId
	///     [u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]  four always-present positional int values
	///     descriptor keyList values, in this type's order; u32 unless the key is 0x4d9/0x4da, a std::string
	///     </code>
	/// </summary>
	/// <remarks>
	///     The keys are not on the wire: they live in the client's static per-type descriptor, so the wire carries only the
	///     values in that type's order. A type with an empty key list therefore emits nothing after the four fixed ints,
	///     which is the shape of a parameterless toggle.
	///     <para>
	///         The descriptor table is BSS, filled at runtime, so a static read of a key list returns zeros. Key lists come
	///         from a live dump (docs/re/gameaction_types.json); a type absent from that dump has an unverified key list, and
	///         an empty one there is an assumption to be proven in-client rather than a fact.
	///     </para>
	/// </remarks>
	public static class GameActionDataCodec
	{
		/// <summary>
		///     The GameActionData version word; the deserializer rejects anything else with "Invalid GameAction data
		///     version".
		/// </summary>
		public const uint Version = 4;

		/// <summary>
		///     The byte length of the element prefix through the first fixed int (0x2d5):
		///     <c>[typeId][field][version][subCount][0x2d5]</c>. <see cref="GameActionListCodec" />'s own header already
		///     writes exactly those five words, so a list body slices the element from here on.
		/// </summary>
		public const int PrefixThrough0x2d5 = 5 * sizeof(uint);

		/// <summary>
		///     The GameActionData constructor's seed for the fourth fixed int (0x2f5).
		/// </summary>
		/// <remarks>
		///     Which of the two values an element carries is decided by whether the server constructed it, and it lines up
		///     with the <c>field</c> counter exactly: across all 1,745 captured elements, every one of the 1,558 with
		///     <c>field == 0</c> carries 9 and every one of the 187 with a live <see cref="GameActionInstanceId" /> carries
		///     0. So a server-constructed action - the travel action, for one - wants 0, and a template-loaded one wants
		///     <see cref="CapturedSeed0x2f5" />.
		/// </remarks>
		public const uint CtorSeed0x2f5 = 0;

		/// <summary>
		///     What a template-loaded element carries at 0x2f5: the emote, numeric, template-choice and world-entry bodies
		///     GameActionListCodecTests pins, and all 1,558 corpus elements with no instance counter.
		/// </summary>
		public const uint CapturedSeed0x2f5 = 9;

		/// <summary>The two fixed ints between 0x2d5 and 0x2f5, constant in every element we emit.</summary>
		private const uint Fixed0x2d6 = 0;
		private const uint Fixed0x2f2 = 1;

		/// <summary>The first fixed int (0x2d5), constant in every element we emit.</summary>
		private const uint Fixed0x2d5 = 1;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(GameActionData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     One all-u32 element from its type id and descriptor-ordered keyList values. The string keys 0x4d9/0x4da are
		///     not modelled, every setter we emit being all-u32.
		/// </summary>
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
			// An out-of-range type is an assert dialog in the client rather than a dropped packet (SpellData.cpp:1577),
			// so fail here instead.
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

	/// <summary>
	///     One GameActionData element. <c>KeyValues</c> are the descriptor-ordered values for this type; the keys
	///     themselves live in the client, not on the wire.
	/// </summary>
	public sealed record GameActionData(uint TypeId, uint Field, uint SubCount, IReadOnlyList<uint> KeyValues, uint Seed0x2f5 = GameActionDataCodec.CtorSeed0x2f5);
}
