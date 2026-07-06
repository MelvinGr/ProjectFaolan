using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.GameServer.Combat
{
	// Builds the two combat-EFFECT IIR carrier contents decoded from the guardian combat capture
	// (Blablabla_..._random-actions-combat): the floating damage number and the buff/debuff stat effect (#274).
	// Both are sent inside the standard 0x2000 IIR carrier (the caller wraps with WriteArrayUInt32Length, exactly
	// like NpcSpellCastBuilder), so these return the content WITHOUT the leading [u32 length]. The single env byte
	// after the target handle is the IIR carrier's [u8 env] (see iir_catalog_decoded), always 0 here. Byte-exact
	// against the captured records (CombatEffectBuilderTests).
	public static class CombatEffectBuilder
	{
		// DamageResultIIR (0xFEC4158A): the floating combat-damage number shown over the target. Decoded fields:
		//   [subop][class][target][env=0][class][attacker][u32 damage][u32 type][0][0]
		// type is the hit category (2 = normal, 3 = the bigger hits in the capture - crit/special). Sent both ways
		// (NPC->player and player->NPC). Golden: fr 6514 = an NPC hitting the player for 83 (type 2).
		public static byte[] BuildDamageResult(uint target, uint attacker, uint damage, uint type)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.DamageResultIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(target)
				.WriteByte(0)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(attacker)
				.WriteUInt32(damage)
				.WriteUInt32(type)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.ToArray();
		}

		// StatDamageIIR (0xE9F24184): a buff/debuff's effect on one of the target's stats (e.g. Rend -> stat 1 by -8).
		// Decoded fields (env-aligned):
		//   [subop][class][target][env=0][u32 statValue][0][0][0][u32 0x1b][u32 count=2][i32 delta][u32 statId]
		//   [class][u32 caster][u32 idx][0][u32 spellBaseId][u32 1][0][0][0]
		// statValue is the target stat's resulting value; delta is the signed change (negative = a debuff); spellId is
		// the BASE spell id (wire id >> 8, e.g. Rend 0x316449). idx is the effect's slot (1, 2...). The 0x1b marker
		// and count=2 are constant across the capture. Golden: fr 6689 = Rend's stat-1 -8 from the trapper.
		public static byte[] BuildStatDamage(uint target, uint statValue, int delta, uint statId, uint caster, uint idx, uint spellBaseId)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.StatDamageIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(target)
				.WriteByte(0)
				.WriteUInt32(statValue)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0x1b)
				.WriteUInt32(2)
				.WriteUInt32(delta)
				.WriteUInt32(statId)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(caster)
				.WriteUInt32(idx)
				.WriteUInt32(0)
				.WriteUInt32(spellBaseId)
				.WriteUInt32(1)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.ToArray();
		}
	}
}
