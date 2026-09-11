using System.Collections.Frozen;
using System.Collections.Generic;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     How the client picks the sentence it draws a GameAction element with - the parts that are not simply "look
	///     LDB 170 up by the type id".
	/// </summary>
	/// <remarks>
	///     <para>
	///         The tooltip builder is <c>FUN_0149e450</c>: a per-type-id switch (an MSVC index-byte plus jump-target
	///         table pair at <c>0x14a2e90</c>/<c>0x14a2de8</c> over <c>0xCF0A..0xD002</c>, plus a second pair at
	///         <c>0x14a324c</c>/<c>0x14a323c</c> over <c>0xD013..0xD025</c>). 54 of the 260 registered types reach a
	///         branch of their own; the other 206 fall to the default and the client draws nothing for them.
	///     </para>
	///     <para>
	///         Each branch fetches its sentence one of three ways, and only the first is the obvious one:
	///         <list type="number">
	///             <item>
	///                 by its own type id - <c>MOV EDI,0xaa; CALL FUN_0060f400</c> with the element's type in
	///                 <c>ECX</c>;
	///             </item>
	///             <item>
	///                 by another type's id - the same call with a literal <c>MOV ECX,&lt;id&gt;</c> first. That is
	///                 <see cref="SentenceKinds" />, and it is why several very common kinds look like they have no
	///                 sentence at all;
	///             </item>
	///             <item>
	///                 by a token name - <c>PUSH 0xaa; MOV ESI,&lt;char*&gt;; CALL FUN_0060ec00</c>, keyed into the
	///                 same category by <c>elf28(token)</c> (<c>docs/re/gameaction-tooltip-tokens.tsv</c>).
	///             </item>
	///         </list>
	///     </para>
	/// </remarks>
	public static class GameActionTooltip
	{
		/// <summary>The LDB category holding one tooltip sentence per GameAction - see the type remarks.</summary>
		public const uint LdbCategory = 170;

		// Every branch whose first act is `MOV ECX,<literal>; MOV EDI,0xaa; CALL 0x0060f400` - it draws itself with
		// another type's sentence. Recovered mechanically from the disassembly, not inferred from the names, which
		// matters because two of them are not name-alike at all (53098/53099 are not `...StatModifier`s).
		private static readonly FrozenDictionary<uint, uint> Aliases = new Dictionary<uint, uint>
		{
			// The interpolated family draws itself as its flat twin, with the ramp already resolved at the caster's
			// level: `InterpolatedModify` reads 174/175/0, lerps them against stat 0x3C5 (Apprentice Level), and
			// renders the result through `Modify`'s own `#2%s #1%s` (branch 0x014a12c6).
			[53220] = 53045, // InterpolatedModify        -> Modify
			[53221] = 53110, // InterpolatedTempChangeSkill -> TempChangeSkill

			// The whole spell/line stat-modifier family shares `SpellStatModifier`'s sentence (branch 0x014a2584,
			// which spells the remap out as two compares and a default).
			[53062] = 53054, // SpellLineStatModifier
			[53098] = 53054,
			[53099] = 53054,
			[53283] = 53054,
			[53285] = 53054,

			// The per-LINE recast setter draws as the per-SPELL one (branch 0x014a02b4).
			[53069] = 53031 // ChangeLineRecastTime -> ChangeRecastTime
		}.ToFrozenDictionary();

		/// <summary>
		///     The kind whose LDB <see cref="LdbCategory" /> entry renders <paramref name="kind" /> - itself, unless
		///     the client's branch swaps in another type's sentence.
		/// </summary>
		public static uint SentenceKind(uint kind)
		{
			return Aliases.TryGetValue(kind, out var alias) ? alias : kind;
		}

		/// <summary>Every kind that borrows another's sentence, and whose.</summary>
		public static IReadOnlyDictionary<uint, uint> SentenceKinds => Aliases;

		/// <summary>
		///     The damage/heal renderer <c>FUN_0149aa30(buf, stat, min, max, flag)</c>, which is how <c>Hit</c>
		///     (53002), its twin 53132 and <c>InterpolatedHit</c> (53219) are drawn - not through the
		///     <c>%s %s %s</c> sitting at their own type id. It selects one of three token sentences, and the rule is
		///     the whole function's head:
		///     <code>
		///     if (stat == 0x1B Health)  return min &lt; 0 ? DamageHealth : HealHealth;
		///     else                      return DamageOtherStat;
		///     </code>
		///     The value slot is the range <c>min-max</c> (built by <c>FUN_0149a700</c>), which is why these carry two
		///     magnitudes where every other modifier carries one.
		/// </summary>
		public static string HitSentenceToken(uint statId, int minValue)
		{
			if (statId != HealthStatId)
				return "DamageOtherStat";

			return minValue < 0 ? "DamageHealth" : "HealHealth";
		}

		/// <summary>The stat id the hit renderer special-cases - <c>Health</c>, the only one with a heal phrasing.</summary>
		public const uint HealthStatId = 0x1B;

		// Every type id that reaches a branch of `FUN_0149e450` - 54 of the 260 the client registers. Read out of the
		// two dispatch tables plus the two compares that bracket them (`0xD00B` ConeFormula takes a `JZ` rather than a
		// table slot). Mirrors docs/re/gameaction-tooltip-dispatch.tsv.
		private static readonly FrozenSet<uint> Branched = new uint[]
		{
			53002, 53007, 53012, 53019, 53026, 53027, 53030, 53031, 53032, 53037, 53045, 53046, 53051, 53054, 53061,
			53062, 53066, 53067, 53069, 53070, 53075, 53084, 53087, 53088, 53098, 53099, 53121, 53122, 53129, 53162,
			53170, 53176, 53182, 53189, 53196, 53209, 53211, 53212, 53213, 53217, 53219, 53220, 53221, 53223, 53224,
			53225, 53227, 53249, 53250, 53259, 53267, 53273, 53283, 53285
		}.ToFrozenSet();

		/// <summary>
		///     Whether the client's tooltip builder has a branch for this kind at all.
		/// </summary>
		/// <remarks>
		///     False is an answer, not a gap: 206 of the 260 registered types fall to the switch's default, and the
		///     client draws nothing for them. <c>DPSHit</c> (53013) is the big one - 2,660 elements in the spell
		///     catalog and no tooltip line anywhere in retail. Counting those as "sentence missing" reads as decode
		///     debt when it is the client's own behaviour.
		/// </remarks>
		public static bool HasTooltipBranch(uint kind)
		{
			return Branched.Contains(kind);
		}

		/// <summary>
		///     Why a kind that does have a branch still has no category-170 sentence to find - or null when its
		///     absence is genuinely unexplained and so still decode debt.
		/// </summary>
		/// <remarks>
		///     A third bucket, and it exists for the same reason <see cref="HasTooltipBranch" /> does: "we could not
		///     find a sentence" and "there is provably no sentence to find" read identically in a census and are
		///     opposite facts. Both entries below are proven, and each is proven by something outside the LDB lookup
		///     that failed:
		///     <list type="bullet">
		///         <item>
		///             53019 <c>Spell</c> / 53061 <c>BCCCastSpell</c> - their branches push category <c>0x64</c> =
		///             100, not the <c>0xaa</c> = 170 every sentence-fetching branch pushes, and the tokens resolve in
		///             100 as plain label nouns ("Spell", "Combo", "Ability", ...). They draw a label, so there is no
		///             170 sentence by construction (<c>docs/re/gameaction-tooltip-tokens.tsv</c>'s header carries the
		///             same proof).
		///         </item>
		///         <item>
		///             53170 <c>InheritedComboAttack</c> - its single token is a 170 entry (<c>PerSecond</c>, "%s per
		///             second"), but the <c>%s</c> is a number the record does not contain: branch <c>0x014a103e</c>
		///             computes it as <c>key224 x 0.01 x [ESI] / [EBX]</c>, two floats that come from the invocation
		///             context rather than the element. So the sentence is found and cannot be filled from a template
		///             - rendering it would be inventing a number.
		///         </item>
		///     </list>
		/// </remarks>
		public static string? NoSentenceReason(uint kind)
		{
			return kind switch
			{
				53019 or 53061 => "draws a category-100 label NOUN, not a 170 sentence",
				53170 => "sentence found (`PerSecond`); its number needs the invocation context, not the record",
				_ => null
			};
		}
	}
}
