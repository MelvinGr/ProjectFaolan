using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     DamageResultIIR - the floating combat-damage number shown over the target, 37 bytes:
	///     <c>[subop][class][target][env=0][class][attacker][u32 damage][u32 school][u32 0][u32 0]</c>. Sent in both
	///     directions.
	/// </summary>
	/// <remarks>
	///     The carrier handle is the target and the second endpoint is the attacker, in that order.
	///     <para>
	///         <c>School</c> is the attacker's damage school, not a hit result: across all 1485 captured DamageResults it is
	///         constant per attacker (the player's physical melee is Crushing/3, while NPCs use 1/2/8 =
	///         Slashing/Piercing/Unholy) and the two trailing u32 are always 0. There is no crit or miss flag - a crit is
	///         simply a bigger number, and misses are not sent as DamageResult at all. Golden: frame 6514, NPC 0x2c56 hitting
	///         the player for 83 with school Piercing.
	///     </para>
	///     <para>
	///         This record is also the measurement instrument for combat balance: every hit retail ever dealt is one of
	///         these, so joining them to the attacker's spawn level gives the damage curve directly. That is what
	///         <c>--report npc-damage</c> does, and where CombatResolver's measured constants come from. Changing this
	///         layout silently stops that report measuring.
	///     </para>
	/// </remarks>
	public static class DamageResultCodec
	{
		public const uint Opcode = (uint)IirTypes.DamageResultIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(DamageResultIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint target, uint attacker, uint damage, DamageSchool school)
		{
			return Build(new DamageResultIIR(target, 0x00, attacker, damage, school));
		}

		public static DamageResultIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(DamageResultCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static DamageResultIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "DamageResultIIR subop");
			r.ExpectU32Be(EntityClass.Character, "target class");
			var target = r.ReadU32Be();
			var env = r.ReadU8();
			r.ExpectU32Be(EntityClass.Character, "attacker class");
			var attacker = r.ReadU32Be();
			var damage = r.ReadU32Be();
			var school = (DamageSchool)r.ReadU32Be();
			return new DamageResultIIR(target, env, attacker, damage, school, r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, DamageResultIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Target)
				.WriteByte(v.Env)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Attacker)
				.WriteUInt32(v.Damage)
				.WriteUInt32(v.School)
				.WriteUInt32(v.TrailerA)
				.WriteUInt32(v.TrailerB);
		}
	}

	/// <summary>
	///     The DamageResultIIR body. <c>TrailerA</c> and <c>TrailerB</c> are 0 in all 1485 captured records and are carried
	///     as fields rather than asserted, so a record with something there round-trips instead of being rejected.
	/// </summary>
	public sealed record DamageResultIIR(uint Target, byte Env, uint Attacker, uint Damage, DamageSchool School, uint TrailerA = 0, uint TrailerB = 0);
}
