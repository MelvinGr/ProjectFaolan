using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One SPELL modifier a feat applies (#590) - "+35% damage or healing on Flesh to Worms". The other half of
	///     what a feat does, beside <see cref="FeatEffect" />: a stat feat raises a character stat, a spell feat
	///     raises one spell LINE. For a caster the spell half is most of the tree - 23 of DemoL80's 25 trained feats
	///     are in this shape.
	/// </summary>
	/// <remarks>
	///     Same record and same place in the template as a <see cref="FeatEffect" /> - the ordinary
	///     <c>4,0,1,0,2,9</c> signature - and that is exactly why these were invisible: the shared parser reads the
	///     payload's first word as a stat id and drops anything <c>&gt;= 0x1000</c>. Here it is not a stat id, it is
	///     a <see cref="SpellLineId" />.
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatSpellModifier
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The spell LINE this boosts - <see cref="Spell.GuiStackingLine" />, i.e. <c>ConanClass × 1000 + lineIndex</c>
		///     (41000 = Necromancer line 0 = Flesh to Worms). A line, not a spell, so the feat covers every RANK of it
		///     at once, which is what a feat has to do. Set on a kind-53075 record; null on a kind-53030 one, where
		///     <see cref="SpellId" /> carries the target instead.
		/// </summary>
		public uint? SpellLineId { get; set; }

		/// <summary>
		///     The single spell this boosts (<see cref="Spell.Id" />), on a kind-53030 <c>ChangeHitPercent</c> record -
		///     the per-SPELL twin of 53075, the same three slots with the line slot (key 1) replaced by the spell slot
		///     (key 187). Null on a kind-53075 record; exactly one of the two is ever set.
		///     <para>
		///         238 of the 240 distinct references resolve against <c>Spells.Id</c>, which is what says the space is
		///         a spell and not the COMBO the same key index carries on <c>UpgradeCombo</c> - an unnamed key means a
		///         different thing on every type that uses it (see <c>GameActionTypes</c>).
		///     </para>
		/// </summary>
		public uint? SpellId { get; set; }

		/// <summary>
		///     The boost in HUNDREDTHS OF A PERCENT: 3500 = +35.00%. Decoded from the five ranks of
		///     <c>Decomposition</c>, which carry 1000/1500/2000/2500/3500 where the in-client tooltip reads
		///     10/15/20/25/35%.
		///     <para>
		///         SIGNED, and the shipped catalog proves it rather than merely allowing it: <c>Set's Chosen</c>
		///         steps -2000/-4000/-6000/-8000/-10000 across its five ranks on Demonologist lines 44100/44101,
		///         which is a rank ladder, not a misread. It was a <c>uint</c> until #591 and every consumer cast
		///         it back to <c>int</c>, so the runtime was right and only the column and the DB text were not.
		///     </para>
		/// </summary>
		public int PercentHundredths { get; set; }

		/// <summary>
		///     The effect-KIND tag that opens the record, and what says which target column is set: 53075
		///     <c>ChangeLineHitPercent</c> (#590) fills <see cref="SpellLineId" /> and 53030 <c>ChangeHitPercent</c>
		///     (#593) fills <see cref="SpellId" />. The two are one payload with the target space swapped, which is why
		///     they share a table - the same reason 53054 and 53062 share <see cref="FeatSpellStatModifier" />.
		/// </summary>
		public uint EffectKind { get; set; }
	}
}
