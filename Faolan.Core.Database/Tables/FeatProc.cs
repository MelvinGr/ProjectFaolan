using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One PROC a feat attaches to a combo or a spell line (#593) - "Touch of Death: empowers their next combo
	///     with additional unholy damage", "Blood Warrior: improves the Blood Flow spell". The feat does not change a
	///     number here; it hangs an extra spell off something the character already uses, to fire on a trigger.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Two GameAction kinds, one shape with the target space swapped - the same pairing 53075/53030 and
	///         53054/53062 have: 53212 <c>AddComboProc</c> <c>[88, 236, 187 combo, 117, 237]</c> fills
	///         <see cref="ComboId" /> and 53070 <c>AddSpellLineProc</c> <c>[88, 236, 1 line, 117, 237, 73]</c> fills
	///         <see cref="SpellLineId" />. Every reference resolves: 75/76 combos, 26/26 lines, 42/43 proc spells.
	///     </para>
	///     <para>
	///         The two SPELL slots are named by the client's own tooltip builder, which tests each independently and
	///         renders <c>AddComboProcSelf</c> when only key 88 is set, <c>AddComboProcOther</c> when only key 236 is,
	///         and <c>AddComboProcBoth</c> when both are - see docs/re/gameactionlist-grammar.md.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatProc
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The combo the proc hangs off (<c>Combos.Id</c>), on a kind-53212 record. Null on a kind-53070 one.
		///     Deliberately not a foreign key, for the same reason <see cref="FeatComboModifier.ComboId" /> is not:
		///     one of the 76 references points at a combo our catalog does not carry (Gitea #581).
		/// </summary>
		public uint? ComboId { get; set; }

		/// <summary>
		///     The spell LINE the proc hangs off (<see cref="Spell.GuiStackingLine" />), on a kind-53070 record. Null on a
		///     kind-53212 one; exactly one of the two targets is ever set.
		/// </summary>
		public uint? SpellLineId { get; set; }

		/// <summary>
		///     The spell the proc casts on the CARRIER (<see cref="Spell.Id" />) - key 88. Null when the proc only
		///     affects the target.
		/// </summary>
		public uint? SelfSpellId { get; set; }

		/// <summary>
		///     The spell the proc casts on the TARGET (<see cref="Spell.Id" />) - key 236. Null when the proc only
		///     affects the carrier, which is all but one of the 710 shipped records.
		/// </summary>
		public uint? TargetSpellId { get; set; }

		/// <summary>
		///     The trigger the proc fires on - key 117, a <see cref="Faolan.Core.Enums.FeatProcTrigger" /> value: 0 Hit /
		///     1 Damage on the combo form, 2 SpellDamage / 3 Cast on the line form. Named by the client's generic proc
		///     tooltip switch (`FUN_0149e450` at 0x0149ef57: 0 → <c>AddProcOnHit</c>, 1 → <c>AddProcDamage</c>, 2 →
		///     <c>AddProcSpellDamage</c>, key 66 = 1 flipping each to its <c>Getting</c> twin) and, for the value that
		///     switch has no sentence for, by the feats that carry it - every trigger-3 row hangs off a HEALING line and
		///     reads "after casting the Wave of Life…" (#697). Stored as the raw word; the enum is the reading.
		/// </summary>
		public uint Trigger { get; set; }

		/// <summary>
		///     The proc's RATE - key 237, PROCS PER MINUTE: the client's own label in the composed
		///     <c>AddSpellLineProc</c> sentence ("Disrupted Charge on self, 200 Procs per Minute", docs/rdb/spell-tooltip.md)
		///     and the unused combo sentence's "Triggers #4%d times per minute". 2..200 across the catalog; 120 and 200
		///     are what "always" looks like once the rate is taken over a one-to-two-second event, which is why the
		///     feats carrying them state their effect flatly while a 10 says "a chance of". ⚠️ It was first read as a
		///     percent chance; the client's label settles the unit. <c>ProcRules.Chance</c> is the conversion.
		/// </summary>
		public int Amount { get; set; }

		/// <summary>The effect-KIND tag that opens the record: 53212 or 53070, and what says which target is set.</summary>
		public uint EffectKind { get; set; }
	}
}
