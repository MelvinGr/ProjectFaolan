using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// StatIIR (0xF98E10B3) - one fire-burst stage of an accumulated spellweave, emitted per stage on DEACTIVATE.
	//
	//   [subop][class][caster][u8 env=0x01][u32 fireId][u32 trailer=0x00007BD4]
	//
	// Necro fire ids: 0x398 / 0x39B / 0x39A / 0x39D. The trailer is constant across the captured burst. Decoded in
	// tools/capture/decode_spellweave.py from NecroL80_..._consuming-potions-food-spellweaving.
	public static class StatCodec
	{
		public const uint Opcode = (uint)IirTypes.StatIIR;

		// The env byte every captured fire stage carries.
		public const byte FireStageEnv = 0x01;

		// The constant trailing word of a fire stage.
		public const uint FireStageTrailer = 0x00007BD4;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(StatIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint caster, uint fireId)
		{
			return Build(new StatIIR(caster, FireStageEnv, fireId, FireStageTrailer));
		}

		public static StatIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StatIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "StatIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var caster = r.ReadU32Be();
			var env = r.ReadU8();
			var fireId = r.ReadU32Be();
			return new StatIIR(caster, env, fireId, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, StatIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Caster).WriteByte(v.Env).WriteUInt32(v.FireId).WriteUInt32(v.Trailer);
		}
	}

	// StatIIR (0xF98E10B3).
	public sealed record StatIIR(uint Caster, byte Env, uint FireId, uint Trailer);
}
