using System.Collections.Frozen;
using System.Collections.Generic;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     How the client picks the SENTENCE it draws a GameAction element with - the parts that are not simply
	///     "look LDB 170 up by the type id" (#782).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The tooltip builder is <c>FUN_0149e450</c>: a per-type-id switch (an MSVC index-byte + jump-target
	///         table pair at <c>0x14a2e90</c>/<c>0x14a2de8</c> over <c>0xCF0A..0xD002</c>, plus a second pair at
	///         <c>0x14a324c</c>/<c>0x14a323c</c> over <c>0xD013..0xD025</c>). 49 of the 260 registered types reach a
	///         branch of their own; the other 211 fall to the default and the client draws nothing for them.
	///     </para>
	///     <para>
	///         Each branch fetches its sentence one of three ways, and only the first is the obvious one:
	///         <list type="number">
	///             <item><b>by its own type id</b> - <c>MOV EDI,0xaa; CALL FUN_0060f400</c> with the element's type in
	///             <c>ECX</c>;</item>
	///             <item><b>by ANOTHER type's id</b> - the same call with a LITERAL <c>MOV ECX,&lt;id&gt;</c> first.
	///             That is <see cref="SentenceKinds" />, and it is why several very common kinds looked like they had
	///             no sentence at all;</item>
	///             <item><b>by a TOKEN NAME</b> - <c>PUSH 0xaa; MOV ESI,&lt;char*&gt;; CALL FUN_0060ec00</c>, keyed
	///             into the same category by <c>elf28(token)</c> (docs/re/gameaction-tooltip-tokens.tsv).</item>
	///         </list>
	///     </para>
	/// </remarks>
	public static class GameActionTooltip
	{
		/// <summary>The LDB category holding one tooltip sentence per GameAction - see the type remarks.</summary>
		public const uint LdbCategory = 170;

		// Every branch whose first act is `MOV ECX,<literal>; MOV EDI,0xaa; CALL 0x0060f400` - i.e. it draws itself
		// with ANOTHER type's sentence. Recovered mechanically from the disassembly, not inferred from the names,
		// which matters because two of them are not name-alike at all (53098/53099 are not `...StatModifier`s).
		private static readonly FrozenDictionary<uint, uint> Aliases = new Dictionary<uint, uint>
		{
			// ⭐ The interpolated family draws itself as its FLAT twin, with the ramp already resolved at the
			// caster's level: `InterpolatedModify` reads 174/175/0, lerps them against stat 0x3C5 (Apprentice
			// Level), and renders the result through `Modify`'s own `#2%s #1%s` (branch 0x014a12c6).
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
		///     ⭐ The DAMAGE/HEAL renderer `FUN_0149aa30(buf, stat, min, max, flag)`, which is how <c>Hit</c> (53002),
		///     its twin 53132 and <c>InterpolatedHit</c> (53219) are drawn - NOT through the `%s %s %s` sitting at
		///     their own type id. It selects one of three token sentences, and the rule is the whole function's head:
		///     <code>
		///     if (stat == 0x1B Health)  return min &lt; 0 ? DamageHealth : HealHealth;
		///     else                      return DamageOtherStat;
		///     </code>
		///     The value slot is the RANGE `min-max` (built by <c>FUN_0149a700</c>), which is why these carry two
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
	}
}
