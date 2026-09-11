using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One stat modifier a feat applies to a spell, or to a whole spell line - "Bombardment: +1.5m attack
	///     range on Shock and Shockstrike". The third thing a feat can do, beside <see cref="FeatEffect" /> (a
	///     character stat) and <see cref="FeatSpellModifier" /> (a flat hit-percent on a line): a stat scoped to one
	///     spell.
	/// </summary>
	/// <remarks>
	///     The two GameAction kinds behind it - 53054 <c>SpellStatModifier</c> and 53062 <c>SpellLineStatModifier</c> -
	///     are the same four-slot payload with the target space swapped, so they share one table and
	///     <see cref="SpellId" /> / <see cref="SpellLineId" /> say which was used. Both resolve completely against the
	///     shipped catalogs: 593/593 of the per-spell targets are a <see cref="Spell.Id" /> and 475/475 of the per-line
	///     targets a <see cref="Spell.GuiStackingLine" />.
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatSpellStatModifier
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The single spell this is scoped to (<see cref="Spell.Id" />), on a kind-53054 record. Null on a
		///     kind-53062 record, where <see cref="SpellLineId" /> carries the target instead - exactly one of the two
		///     is ever set.
		/// </summary>
		public uint? SpellId { get; set; }

		/// <summary>
		///     The spell line this is scoped to (<see cref="Spell.GuiStackingLine" />, i.e. <c>ConanClass × 1000 + lineIndex</c>),
		///     on a kind-53062 record - so the feat covers every rank of the line at once. Null on a kind-53054 record.
		/// </summary>
		public uint? SpellLineId { get; set; }

		/// <summary>
		///     Which property of the target spell moves. The same id space as <see cref="FeatEffect.StatId" /> - the
		///     client resolves this word through LDB category 615, the one stat-name table - but the ids the spell half
		///     uses name spell properties: <see cref="CharacterStatId.ManaCost" />,
		///     <see cref="CharacterStatId.CastingTimeSeconds" />, <see cref="CharacterStatId.SplashRadius" />, …
		/// </summary>
		public CharacterStatId StatId { get; set; }

		/// <summary>
		///     An absolute delta, in the stat's own wire units (GameAction key 39, <c>Value</c>). Null when the record
		///     carries a percentage instead.
		///     <para>
		///         The two are alternatives, not a pair, and the client's tooltip builder is what says so: on the
		///         absolute branch it appends the stat's own unit suffix, while the percentage branch appends a
		///         hardcoded <c>"%"</c>. Across the 1068 shipped records exactly one of the two is set - never both,
		///         never neither.
		///     </para>
		/// </summary>
		public int? Value { get; set; }

		/// <summary>
		///     A relative delta in hundredths of A percent (GameAction key 175, <c>Amount</c>): -10000 = -100%. Null
		///     when the record carries an absolute <see cref="Value" /> instead.
		///     <para>
		///         The scale is the client's: for <see cref="CharacterStatId.CastingTimeSeconds" /> its tooltip prints
		///         this word as <c>abs(amount) * 0.01</c> under a <c>"%.1f%%"</c> format, and the shipped values are a
		///         clean ladder off it (-1000 / -2000 / -3300 / -5000 / -6600 / -10000 = -10% … -100%).
		///     </para>
		///     <para>signed, like every feat magnitude - most of these are reductions.</para>
		/// </summary>
		public int? PercentHundredths { get; set; }

		/// <summary>
		///     The effect-kind tag that opens the record: 53054 (spell-scoped) or 53062 (line-scoped). Redundant with
		///     which of <see cref="SpellId" /> / <see cref="SpellLineId" /> is set, and kept because it is what the
		///     record actually carried - the importer takes these two kinds and this column is what says so.
		/// </summary>
		public uint EffectKind { get; set; }
	}
}
