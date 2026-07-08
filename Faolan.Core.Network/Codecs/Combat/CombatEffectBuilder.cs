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
		public static byte[] BuildDamageResult(uint target, uint attacker, uint damage, uint type) => new DamageResult(target, attacker, damage, type).ToArray();

		public static byte[] BuildStatDamage(uint target, uint statValue, int delta, uint statId, uint caster, uint idx, uint spellBaseId) =>
			new StatDamage(target, statValue, delta, statId, caster, idx, spellBaseId).ToArray();

		// DamageResultIIR (0xFEC4158A): the floating combat-damage number shown over the target. Decoded fields:
		//   [subop][class][target][env=0][class][attacker][u32 damage][u32 type][0][0]
		// type is the hit category (2 = normal, 3 = the bigger hits in the capture - crit/special). Sent both ways
		// (NPC->player and player->NPC). Golden: fr 6514 = an NPC hitting the player for 83 (type 2).
		public sealed record DamageResult(uint Target, uint Attacker, uint Damage, uint Type) : ICodec<DamageResult>
		{
			public void Write(FaolanStream s)
			{
				s.WriteUInt32(GameServer0X2000Opcodes.DamageResultIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(Target)
					.WriteByte(0)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(Attacker)
					.WriteUInt32(Damage)
					.WriteUInt32(Type)
					.WriteUInt32(0)
					.WriteUInt32(0);
			}

			public static DamageResult Read(FaolanStream s)
			{
				s.ReadUInt32(); // subop (DamageResultIIR)
				s.ReadUInt32(); // class
				var target = s.ReadUInt32();
				s.ReadByte(); // env 0
				s.ReadUInt32(); // class
				var attacker = s.ReadUInt32();
				var damage = s.ReadUInt32();
				var type = s.ReadUInt32();
				s.ReadUInt32(); // 0
				s.ReadUInt32(); // 0
				return new DamageResult(target, attacker, damage, type);
			}
		}

		// StatDamageIIR (0xE9F24184): a buff/debuff's effect on one of the target's stats (e.g. Rend -> stat 1 by -8).
		// Decoded fields (env-aligned):
		//   [subop][class][target][env=0][u32 statValue][0][0][0][u32 0x1b][u32 count=2][i32 delta][u32 statId]
		//   [class][u32 caster][u32 idx][0][u32 spellBaseId][u32 1][0][0][0]
		// statValue is the target stat's resulting value; delta is the signed change (negative = a debuff); spellId is
		// the BASE spell id (wire id >> 8, e.g. Rend 0x316449). idx is the effect's slot (1, 2...). The 0x1b marker
		// and count=2 are constant across the capture. Golden: fr 6689 = Rend's stat-1 -8 from the trapper.
		public sealed record StatDamage(uint Target, uint StatValue, int Delta, uint StatId, uint Caster, uint Idx, uint SpellBaseId) : ICodec<StatDamage>
		{

			public void Write(FaolanStream s)
			{
				s.WriteUInt32(GameServer0X2000Opcodes.StatDamageIIR)
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
					.WriteUInt32(StatId)
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
				var statId = s.ReadUInt32();
				s.ReadUInt32(); // class
				var caster = s.ReadUInt32();
				var idx = s.ReadUInt32();
				s.ReadUInt32(); // 0
				var spellBaseId = s.ReadUInt32();
				s.ReadUInt32(); // 1
				s.ReadUInt32(); // 0
				s.ReadUInt32(); // 0
				s.ReadUInt32(); // 0
				return new StatDamage(target, statValue, delta, statId, caster, idx, spellBaseId);
			}
		}
	}
}
