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
		///     The spell LINE the proc hangs off (<see cref="Spell.LineId" />), on a kind-53070 record. Null on a
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
		///     The trigger the proc fires on - key 117, values 0/1 on the combo form and 2/3 on the line form. The
		///     client's `AddProc` tooltip branch dispatches its Hit / Damage / SpellDamage string family on this slot,
		///     so it is the trigger CONDITION, but the mapping from value to condition is not pinned: that branch
		///     belongs to the generic proc types, not to these two, and neither of these has a tooltip branch of its
		///     own. Carried raw rather than mapped to an enum we cannot justify.
		/// </summary>
		public uint Trigger { get; set; }

		/// <summary>
		///     The proc's magnitude - key 237, the slot the tooltip renders last. 2..200 across the catalog. NOT
		///     asserted to be a percentage: unlike <c>UpgradeCombo</c>'s two slots, no branch was found that scales or
		///     suffixes it, so its unit is undecoded and it is stored verbatim.
		/// </summary>
		public int Amount { get; set; }

		/// <summary>The effect-KIND tag that opens the record: 53212 or 53070, and what says which target is set.</summary>
		public uint EffectKind { get; set; }
	}
}
