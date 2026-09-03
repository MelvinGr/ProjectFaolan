namespace Faolan.Core.Enums
{
	/// <summary>
	///     The event a feat PROC fires on - GameAction key 117 on <c>AddComboProc</c> (53212) and <c>AddSpellLineProc</c>
	///     (53070), stored raw in <c>FeatProc.Trigger</c> (#697).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Named by the client's generic proc tooltip (`FUN_0149e450`, branch at 0x0149ef57): it reads key 117 into a
	///         three-way switch and picks <c>AddProcOnHit</c> / <c>AddProcDamage</c> / <c>AddProcSpellDamage</c> for 0 / 1 / 2
	///         (or their <c>AddProcGetting…</c> twins when key 66 is 1 - the defensive side, which neither of our two kinds
	///         carries). Value 3 falls into the switch's default, so it has no sentence in that family; the feats that
	///         carry it name it themselves - every one hangs off a HEALING line (Blood Flow, Wave of Life, Life of Set)
	///         and reads "after casting the Wave of Life…", which is the one event a heal has.
	///     </para>
	///     <para>
	///         The shipped rows keep the two families apart: 0 and 1 occur only on the combo form, 2 and 3 only on the
	///         spell-line form (`--report feat-procs`).
	///     </para>
	/// </remarks>
	public enum FeatProcTrigger : uint
	{
		/// <summary>The combo's weapon attack lands - <c>AddProcOnHit</c>.</summary>
		Hit = 0,

		/// <summary>The combo deals damage - <c>AddProcDamage</c>.</summary>
		Damage = 1,

		/// <summary>A spell in the line deals damage - <c>AddProcSpellDamage</c>.</summary>
		SpellDamage = 2,

		/// <summary>A spell in the line is CAST, damaging or not - the value the damage family has no sentence for.</summary>
		Cast = 3
	}
}
