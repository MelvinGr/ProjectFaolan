namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Provisional combat math. Spell/ability power, mitigation and the ApplySpell magnitude sign/scale aren't
	///     decoded from captures yet (see docs/quests-and-combat.md), so damage is a simple level-scaled value and
	///     <paramref name="spellId" /> is accepted for future per-spell power but not yet used.
	/// </summary>
	public static class CombatResolver
	{
		// Placeholder NPC max health until NPC stats are decoded from the spawn/snapshot data.
		public const uint DefaultNpcMaxHealth = 100;

		// Provisional NPC max health from its level - used to seed a model NPC's vitals so its health bar scales with
		// level (the real per-template HP isn't lifted from the spawn data yet, like ResolveSpellDamage). Level 0 maps
		// to the level-1 value.
		public static uint NpcMaxHealthForLevel(uint level)
		{
			var lvl = level == 0
				? 1u
				: level;
			return DefaultNpcMaxHealth + lvl * 20u;
		}

		// Provisional XP awarded for killing an NPC of the given level. Level-scaled (a real XP curve / level
		// difference scaling isn't modelled yet, like the damage values). Level 0 maps to the level-1 award.
		public static uint ResolveKillXp(uint npcLevel)
		{
			var lvl = npcLevel == 0
				? 1u
				: npcLevel;
			return 10u + lvl * 25u;
		}

		// Provisional damage an NPC's swing deals to a player, scaled by the NPC's level (mirrors ResolveSpellDamage;
		// real NPC weapon/mitigation data isn't modelled yet). Level 0 maps to level 1.
		public static uint ResolveNpcAttackDamage(uint npcLevel)
		{
			var lvl = npcLevel == 0
				? 1u
				: npcLevel;
			return 5u + lvl * 3u;
		}

		public static int ResolveSpellDamage(uint spellId, ushort casterLevel)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			return 20 + level * 5;
		}

		// Provisional mana cost for a cast. Per-spell costs aren't decoded from captures yet (like damage), so this
		// is a simple level-scaled value just so casting visibly drains the mana bar (the spell/mana analogue of the
		// melee stamina cost).
		public static uint ResolveSpellManaCost(uint spellId, ushort casterLevel)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			return (uint)(10 + level * 2);
		}
	}
}
