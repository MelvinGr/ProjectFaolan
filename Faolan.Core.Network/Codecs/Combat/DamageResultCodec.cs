using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// DamageResultIIR (0xFEC4158A): the floating combat-damage number shown over the target.
	//
	//   [subop][class][target][env=0][class][attacker][u32 damage][u32 school][u32 0][u32 0]     (37 B)
	//
	// This packet is also the MEASUREMENT INSTRUMENT for combat balance: every hit retail ever dealt is one of these,
	// so joining them to the attacker's spawn level gives the damage curve directly. That is what
	// `dotnet run --project Faolan.Importer -- --report npc-damage` does, and where CombatResolver's measured constants
	// come from - see the provenance block at the top of CombatResolver. If you change this record's layout, that
	// report is the thing that silently stops measuring.
	//
	// `School` is the ATTACKER's damage school (DamageSchool), NOT a hit result - RE of all 1485 captured DamageResults
	// (2026-07-22) showed it is CONSTANT per attacker (the player's physical melee is Crushing/3; different NPCs use
	// 1/2/8 = Slashing/Piercing/Unholy) and the two trailing u32 are always 0, so there is no crit/miss flag: a crit is
	// just a bigger number, and misses aren't sent as DamageResult at all. Sent both ways. Golden: fr 6514 = NPC
	// 0x2c56 hitting the player for 83 (that NPC's school = Piercing/2).
	//
	// ⚠️ The carrier handle is the TARGET and the second endpoint is the ATTACKER, in that order. The second emitter
	// this codec absorbed (#530 wave 4) had the two names the other way round in its signature while writing the same
	// bytes, which is exactly the confident-nonsense a duplicated layout produces. Nothing shipped wrong - it was only
	// ever called from tests - but it is why one codec per IIR is a rule.
	public static class DamageResultCodec
	{
		public const uint Opcode = (uint)IirTypes.DamageResultIIR;

		// CANONICAL - the only method here that writes bytes.
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

	// DamageResultIIR (0xFEC4158A). `TrailerA`/`TrailerB` are 0 in all 1485 captured records and are carried as fields
	// rather than asserted so a record with something there round-trips instead of being rejected.
	public sealed record DamageResultIIR(uint Target, byte Env, uint Attacker, uint Damage, DamageSchool School, uint TrailerA = 0, uint TrailerB = 0);
}
