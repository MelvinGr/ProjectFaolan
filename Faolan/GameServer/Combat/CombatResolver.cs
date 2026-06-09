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
