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
			var level = casterLevel == 0 ? (ushort)1 : casterLevel;
			return 20 + level * 5;
		}
	}
}
