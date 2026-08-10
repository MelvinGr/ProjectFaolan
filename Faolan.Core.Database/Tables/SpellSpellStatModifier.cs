using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One stat modifier a SPELL applies to ANOTHER spell, or to a whole spell LINE (#710) - "while this buff is up,
	///     your next Fireball crits harder". The spell-sourced twin of <see cref="FeatSpellStatModifier" />, and the
	///     same two GameAction kinds (53054 <c>SpellStatModifier</c>, 53062 <c>SpellLineStatModifier</c>) with the same
	///     four-slot payload.
	/// </summary>
	/// <remarks>
	///     <para>
	///         ⚠️ <b>These rows do not target their carrier, which is why they needed a table of their own.</b>
	///         <see cref="SpellStatEffect" /> is keyed by <c>(SpellId, Sequence)</c> with the spell as the SUBJECT, and
	///         it has nowhere to put "…and the thing that moves is a property of some OTHER spell". So the records were
	///         imported with their stat and their magnitude and the TARGET dropped on the floor - the one field that
	///         makes them meaningful. This is a data gap at the importer, not merely an unapplied effect.
	///     </para>
	///     <para>
	///         ONE MECHANISM, TWO SOURCES. A feat states these and so does a spell; the parser
	///         (<c>ItemTemplateStatModifiers.ParseSpellStatModifiers</c>) is shared, and so is the consumer
	///         (<c>SpellModifierService</c>) - a trained feat and a live buff differ only in where the row was read
	///         from and how it is scoped to the caster.
	///     </para>
	///     <para>
	///         The stats these carry are SPELL properties rather than character ones - <c>Spell Critical Chance</c>,
	///         <c>Spell Critical Damage</c>, <c>Splash Damage</c>/<c>Radius</c>/<c>max targets</c>,
	///         <c>Casting Time (Seconds)</c>, <c>Mana Cost</c> - each of which is already a column on
	///         <see cref="Spell" />, so applying one is a per-cast override of the base row rather than a new number.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(SpellId), nameof(Sequence))]
	public class SpellSpellStatModifier
	{
		/// <summary>The CARRIER - the spell whose record states the modifier. The buff you are under, not the one that moves.</summary>
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		/// <summary>Wire position within the carrier's element list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The single spell this is scoped to (<see cref="Spell.Id" />), on a kind-53054 record. Null on a
		///     kind-53062 record, where <see cref="TargetSpellLineId" /> carries the target instead - exactly one of the
		///     two is ever set.
		///     <para>
		///         Deliberately NOT a foreign key, matching <c>Spell.EffectSpell</c> and <c>SpellAreaEffect.EffectSpellId</c>:
		///         it names a record in the client's catalog, and a carrier whose target we have not catalogued must
		///         still keep its modifier rather than fail the import.
		///     </para>
		/// </summary>
		public uint? TargetSpellId { get; set; }

		/// <summary>
		///     The spell LINE this is scoped to (<see cref="Spell.GuiStackingLine" />, i.e.
		///     <c>ConanClass × 1000 + lineIndex</c>), on a kind-53062 record - so the carrier covers every RANK of the
		///     line at once. Null on a kind-53054 record.
		/// </summary>
		public uint? TargetSpellLineId { get; set; }

		/// <summary>Which property of the TARGET spell moves. Same id space as <see cref="FeatSpellStatModifier.StatId" />.</summary>
		public CharacterStatId StatId { get; set; }

		/// <summary>
		///     An ABSOLUTE delta in the stat's own wire units (GameAction key 39, <c>Value</c>). Null when the record
		///     carries a percentage instead - the two are alternatives, never a pair.
		/// </summary>
		public int? Value { get; set; }

		/// <summary>
		///     A RELATIVE delta in HUNDREDTHS OF A PERCENT (GameAction key 175, <c>Amount</c>): -10000 = -100%. Null
		///     when the record carries an absolute <see cref="Value" /> instead. SIGNED - most of these are reductions.
		/// </summary>
		public int? PercentHundredths { get; set; }

		/// <summary>
		///     The effect-KIND tag that opened the record: 53054 (spell-scoped) or 53062 (line-scoped). Redundant with
		///     which target column is set, and kept because it is what the record actually carried.
		/// </summary>
		public uint EffectKind { get; set; }
	}
}
