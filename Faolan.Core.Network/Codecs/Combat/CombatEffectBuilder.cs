using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Builds the two combat-EFFECT IIR carrier contents decoded from the guardian combat capture
	// (Blablabla_..._random-actions-combat): the floating damage number and the buff/debuff stat effect (#274).
	// Both are sent inside the standard 0x2000 IIR carrier (the caller wraps with WriteArrayUInt32Length, exactly
	// like NpcSpellCastBuilder), so these return the content WITHOUT the leading [u32 length]. The single env byte
	// after the target handle is the IIR carrier's [u8 env] (see iir_catalog_decoded), always 0 here. Byte-exact
	// against the captured records (CombatEffectBuilderTests).
	public static class CombatEffectBuilder
	{
		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.DamageResultIIR, GameServer0X2000Opcodes.StatDamageIIR];

		public static byte[] BuildDamageResult(uint target, uint attacker, uint damage, DamageSchool school)
		{
			return new DamageResult(target, attacker, damage, school).ToArray();
		}

		public static byte[] BuildStatDamage(uint target, uint statValue, int delta, uint school, uint caster, uint idx, uint spellBaseId)
		{
			return new StatDamage(target, statValue, delta, (DamageSchool)school, caster, idx, spellBaseId).ToArray();
		}
	}

	// DamageResultIIR (0xFEC4158A): the floating combat-damage number shown over the target. Decoded fields:
	//   [subop][class][target][env=0][class][attacker][u32 damage][u32 type][0][0]
	// `Type` is the ATTACKER's damage school (DamageSchool), NOT a hit result - RE of all 1485 captured DamageResults
	// (2026-07-22) showed it is CONSTANT per attacker (the player's physical melee is Crushing/3; different NPCs use
	// 1/2/8=Slashing/Piercing/Unholy) and the two trailing u32 are always 0, so there is no crit/miss flag: a crit is
	// just a bigger number, and misses aren't sent as DamageResult at all. Sent both ways. Golden: fr 6514 = NPC
	// 0x2c56 hitting the player for 83 (that NPC's school = Piercing/2).
	public sealed record DamageResult(uint Target, uint Attacker, uint Damage, DamageSchool School) : ICodec<DamageResult>
	{
		public static DamageResult Read(FaolanStream s)
		{
			s.ReadUInt32(); // subop (DamageResultIIR)
			s.ReadUInt32(); // class
			var target = s.ReadUInt32();
			s.ReadByte(); // env 0
			s.ReadUInt32(); // class
			var attacker = s.ReadUInt32();
			var damage = s.ReadUInt32();
			var school = (DamageSchool)s.ReadUInt32();
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0
			return new DamageResult(target, attacker, damage, school);
		}

		public void Write(FaolanStream s)
		{
			s
				.WriteUInt32(GameServer0X2000Opcodes.DamageResultIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Target)
				.WriteByte(0)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Attacker)
				.WriteUInt32(Damage)
				.WriteUInt32(School)
				.WriteUInt32(0)
				.WriteUInt32(0);
		}
	}

	// StatDamageIIR (0xE9F24184): a damage/DoT tick on the target - the packet that renders SPELL and
	// damage-over-time numbers, where DamageResult renders the melee hit (#349).
	// Decoded fields (env-aligned):
	//   [subop][class][target][env=0][u32 statValue][0][0][0][u32 0x1b][u32 count=2][i32 delta][u32 school]
	//   [class][u32 caster][u32 idx][0][u32 spellBaseId][u32 1][0][0][0]
	// statValue is the target's resulting health; delta is the signed change (negative = damage); spellId is the
	// BASE spell id (wire id >> 8, e.g. Rend 0x316449). idx is the effect's slot (1, 2...). The 0x1b marker and
	// count=2 are constant across the capture. Golden: fr 6689 = Rend ticking the trapper's victim for 8 Slashing.
	//
	// ⚠️ The word after `delta` is the damage SCHOOL, not a stat id - it used to be called StatId, which made the
	// golden read as "Rend reduces stat 1 by 8" instead of "Rend deals 8 Slashing". Every value the corpus carries
	// is 0..10, exactly the DamageSchool range (`--report npc-damage`), and our own send path already passed a
	// `DotType` into it. That mislabel is also why a census of magical NPC damage came up empty: it looked for
	// health-stat records and there are none - the caster NPCs are Fire/Cold/Electrical/Siege records here.
	public sealed record StatDamage(uint Target, uint StatValue, int Delta, DamageSchool School, uint Caster, uint Idx, uint SpellBaseId) : ICodec<StatDamage>
	{
		public static StatDamage Read(FaolanStream s)
		{
			s.ReadUInt32(); // subop (StatDamageIIR)
			s.ReadUInt32(); // class
			var target = s.ReadUInt32();
			s.ReadByte(); // env 0
			var statValue = s.ReadUInt32();
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0x1b marker
			s.ReadUInt32(); // count 2
			var delta = (int)s.ReadUInt32();
			var school = (DamageSchool)s.ReadUInt32();
			s.ReadUInt32(); // class
			var caster = s.ReadUInt32();
			var idx = s.ReadUInt32();
			s.ReadUInt32(); // 0
			var spellBaseId = s.ReadUInt32();
			s.ReadUInt32(); // 1
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0
			s.ReadUInt32(); // 0
			return new StatDamage(target, statValue, delta, school, caster, idx, spellBaseId);
		}

		public void Write(FaolanStream s)
		{
			s
				.WriteUInt32(GameServer0X2000Opcodes.StatDamageIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Target)
				.WriteByte(0)
				.WriteUInt32(StatValue)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0x1b)
				.WriteUInt32(2)
				.WriteUInt32(Delta)
				.WriteUInt32(School)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Caster)
				.WriteUInt32(Idx)
				.WriteUInt32(0)
				.WriteUInt32(SpellBaseId)
				.WriteUInt32(1)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0);
		}
	}
}
