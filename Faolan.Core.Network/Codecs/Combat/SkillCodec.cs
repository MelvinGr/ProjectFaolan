using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SkillIIR (0xD558FF16) - the client SPENDING skillpoints. It is the `AdjustSkill(int i_nSkill, int i_nAmount)`
	// surface from the client's own method table, but it arrives as an IIR rather than an RPC, and it carries the
	// whole transfer rather than one side of it:
	//
	//   [subop][u32 entityClass][u32 handle][u8 env][ListFrame(n)]( [u32 statId][i32 delta] )xn
	//
	// The head is the SAME base record `StatCodec` reads (class + handle + the base byte), and the tail is the
	// 0x3F1-framed (id, value) list the client's own reader takes - `FUN_007a9a40` -> `FUN_004d5900`, which asserts
	// `word % 0x3F1 == 0` and then reads `count` pairs into a map [V1: docs/re/iir-readers-binary-only.md].
	//
	// ⚠️ The four captured bodies were first read as `[u32 0x0000000B][u8 0xD3][pairs]` - two "constants" that are
	// really ONE misaligned word. `00 00 0B D3` is 0xBD3 = 3 * 0x3F1 = a two-element list, and the 0x00 that looks
	// like the third byte of the leading zero-run is the base record's `env`. That is the 0x3F1 trap in its purest
	// form: split the body one byte early and you get two plausible magic numbers instead of a list count.
	//
	// The two pairs are the transfer: `(SkillPoints 0x0F3, -1)` debiting the pool and `(<skill> , +1)` crediting the
	// skill, so the deltas sum to zero. All four captured spends are a single point, addressed by the skill's STAT id
	// - Runspeed 0x43D, Climbing 0x332, Hiding 0x33C, Perception 0x33E - which is consistent with the sibling
	// `DoSkillSellback(int i_eStat)` also taking a stat id.
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3 frames 953 / 1040 / 1121 / 1179]
	public static class SkillCodec
	{
		public const uint Opcode = (uint)IirTypes.SkillIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SkillIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SkillIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SkillCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SkillIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SkillIIR subop");
			var entityClass = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();

			var count = r.ReadListCount();
			var adjustments = new SkillAdjustment[count];
			for (var i = 0; i < count; i++)
				adjustments[i] = new SkillAdjustment(r.ReadU32Be(), r.ReadI32Be());

			return new SkillIIR(entityClass, handle, env, adjustments);
		}

		internal static void Write(FaolanStream s, SkillIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Opcode).WriteUInt32(v.EntityClass).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(ListFrame.ForCount(v.Adjustments.Count));
			foreach (var a in v.Adjustments)
				s.WriteUInt32(a.StatId).WriteUInt32(unchecked((uint)a.Delta));
		}
	}

	// SkillIIR (0xD558FF16). `Env` is the base record's byte, 0x00 on every captured spend (the StatIIR replies that
	// answer one carry 0x01 - see `StatIirs.CharacterEnv`). [V0: what the base byte selects.]
	public sealed record SkillIIR(uint EntityClass, uint Handle, byte Env, IReadOnlyList<SkillAdjustment> Adjustments);

	// One (stat, signed delta) pair of a spend. `Delta` is signed: the pool debit is negative.
	public sealed record SkillAdjustment(uint StatId, int Delta);
}
