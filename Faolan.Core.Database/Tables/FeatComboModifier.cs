using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One COMBO a feat upgrades (#593) - "Dread Lord: reduces the reuse time of all combos", "Hexer: improves
	///     the damage inflicted by the Blood for Aid combos". The fourth thing a feat can do, beside
	///     <see cref="FeatEffect" /> (a character stat), <see cref="FeatSpellModifier" /> (a hit-percent on a spell
	///     line) and <see cref="FeatSpellStatModifier" /> (a stat scoped to a spell): it makes a combo hit harder or
	///     come back sooner.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The GameAction kind is 53209 <c>UpgradeCombo</c>, <c>[187 combo, 66, 67]</c> - 3,235 records, the
	///         largest single block in the feat catalog. Both magnitudes are read straight off the client's own
	///         tooltip branch in <c>FUN_0149e450</c>, which picks between the localised
	///         <c>UpgradeCombo_IncreaseDamage</c> / <c>_DecreaseDamage</c> strings on the sign of key 66 and between
	///         <c>UpgradeCombo_IncreaseCooldown</c> / <c>_DecreaseCooldown</c> on the sign of key 67 - see
	///         docs/re/gameactionlist-grammar.md.
	///     </para>
	///     <para>
	///         A record carries exactly one of the two axes in the shipped catalog (5 of 3,235 carry both), so the
	///         columns are two nullable-in-spirit deltas kept as plain signed ints: 0 means "this axis is untouched",
	///         which is what the client's own <c>TEST/JZ</c> means before each branch.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatComboModifier
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The combo this upgrades - <see cref="Combo.Id" />. Deliberately NOT a foreign key: 338 of the 342
		///     distinct references resolve against the shipped combo catalog and 4 do not, and an unresolved reference
		///     is a gap in <c>Combos</c> (Gitea #581) rather than a reason to drop the feat's row.
		/// </summary>
		public uint ComboId { get; set; }

		/// <summary>
		///     The combo's DAMAGE delta in HUNDREDTHS OF A PERCENT: 2000 = +20.00%. Signed - the tooltip picks
		///     `Increase` vs `Decrease` on the sign - and 0 when the record does not touch damage. The ×0.01 render
		///     with a hardcoded `%` suffix is the client's (`MULSS` by the 0.01 at `0x01f2800c`), i.e. the same scale
		///     <see cref="FeatSpellModifier.PercentHundredths" /> uses.
		/// </summary>
		public int DamagePercentHundredths { get; set; }

		/// <summary>
		///     The combo's REUSE-TIME delta, same units and same signedness: -6000 = -60.00% cooldown. 0 when the
		///     record does not touch the cooldown. Nothing consumes it yet - the recast path that would is Gitea #271.
		/// </summary>
		public int CooldownPercentHundredths { get; set; }

		/// <summary>
		///     The effect-KIND tag that opens the record. Only 53209 is imported here; carried for the same reason
		///     every other feat table carries it - so a second kind landing in this shape is visible rather than
		///     silently merged into the first.
		/// </summary>
		public uint EffectKind { get; set; }
	}
}
