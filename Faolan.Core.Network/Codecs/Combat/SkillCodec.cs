using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     SkillIIR - the client spending skill points. It is the <c>AdjustSkill(int i_nSkill, int i_nAmount)</c> surface
	///     from the client's own method table, but it arrives as an IIR rather than an RPC and carries the whole transfer
	///     rather than one side of it:
	///     <c>[subop][u32 entityClass][u32 handle][u8 env][ListFrame(n)]( [u32 statId][i32 delta] )xn</c>.
	/// </summary>
	/// <remarks>
	///     The head is the same base record <see cref="StatCodec" /> reads - class, handle and the base byte - and the tail
	///     is the count-framed (id, value) list the client's own reader takes (FUN_007a9a40 into FUN_004d5900), which
	///     validates the frame word and then reads that many pairs into a map.
	///     [V1: docs/re/iir-readers-binary-only.md]
	///     <para>
	///         The body must be split on the base byte, not one byte earlier: read early, the leading zero run and the frame
	///         word look like a constant pair rather than the env byte followed by a two-element list count. A frame word is
	///         always a multiple of the list-frame unit and reads as a magic constant when it is misaligned.
	///     </para>
	///     <para>
	///         The two pairs are the transfer: (SkillPoints 0x0F3, -1) debiting the pool and (skill, +1) crediting the skill,
	///         so the deltas sum to zero. All four captured spends are a single point addressed by the skill's stat id -
	///         Runspeed 0x43D, Climbing 0x332, Hiding 0x33C, Perception 0x33E - which is consistent with the sibling
	///         <c>DoSkillSellback(int i_eStat)</c> also taking a stat id.
	///     </para>
	///     [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3 frames 953 / 1040 / 1121 / 1179]
	/// </remarks>
	public static class SkillCodec
	{
		public const uint Opcode = (uint)IirTypes.SkillIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The SkillIIR body. <c>Env</c> is the base record's byte, 0x00 on every captured spend, while the StatIIR replies
	///     that answer one carry 0x01 (see StatIirs.CharacterEnv). [V0: what the base byte selects]
	/// </summary>
	public sealed record SkillIIR(uint EntityClass, uint Handle, byte Env, IReadOnlyList<SkillAdjustment> Adjustments);

	/// <summary>One (stat, signed delta) pair of a spend. <c>Delta</c> is signed: the pool debit is negative.</summary>
	public sealed record SkillAdjustment(uint StatId, int Delta);
}
