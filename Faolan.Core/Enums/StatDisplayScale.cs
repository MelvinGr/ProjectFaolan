using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Enums
{
	/// <summary>
	///     The client's own WIRE -> DISPLAY divisor for a stat id, transcribed from <c>AgeOfConan.exe</c> (#251).
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Where this comes from.</b> The modifier line of an item/feat tooltip is rendered by
	///         <c>FUN_0149a300(text, value, invertGoodBad)</c>, and that function IS the scale rule - it picks a float
	///         divisor and then either prints <c>value / divisor</c> (<c>FUN_0149a5c0</c>) or, when no divisor applies,
	///         prints the raw integer (<c>FUN_0149a540</c>). Its selection, in the client's order:
	///     </para>
	///     <code>
	///     divisor = 1.0                                        // 0x01f27500
	///     if (predicate1(stat)) divisor  = 100.0               // 0x01f24468
	///     if (predicate2(stat)) divisor *= 100.0               // the two compose -> 10000
	///     if (stat in {0x324,0x326,0x328,0x32A,0x32C,0x32E,0x57B}) divisor = 10.0   // 0x01f68aa4
	///     </code>
	///     <para>
	///         The two predicates are not inline code: they are signal slots the client registers at startup
	///         (<c>FUN_011196e0</c> stores them at <c>DAT_029c8200</c> / <c>DAT_029c8204</c>, with the RTTI type
	///         <c>SlotFull1&lt;SignalTarget, SignalSignatureStat1&lt;bool, stats::Int32Stat_e, SignalTarget&gt;, …&gt;</c> -
	///         i.e. <c>bool(Int32Stat_e)</c>), and <c>FUN_00651ca0</c> is the generic invoker that dispatches through
	///         them. Their bodies are <c>FUN_011197f0</c> (predicate 1) and <c>FUN_01119ca0</c> (predicate 2), each one
	///         big jump-table of stat ids. <see cref="Percent1" /> and <see cref="Percent2" /> below are those two id
	///         sets, and the range branches are transcribed literally rather than flattened, so a diff against the
	///         decompiler output stays readable.
	///     </para>
	///     <para>
	///         <b>Why this supersedes hand-measuring.</b> #251 had reached "a scale can only come from a number a human
	///         reads - a tooltip or the character sheet, against the wire value we hold", which made every stat its own
	///         in-client experiment and left ten of them unreachable because a level-1 character's sheet does not render
	///         them. This settles all of them at once, offline. It is not a competing method: it REPRODUCES all 27
	///         values that were measured that way (see <c>StatDisplayScaleTests</c>), including the two the old table
	///         called out as counter-intuitive - <c>0x0177 FatalityChance</c> renders raw while <c>0x017C
	///         SpellFatalityChance</c> is a ×100 - and it answers the ten that could not be probed:
	///         Attack/Defense/Magic Rating and Magical Resist, Miss Chance, Healing Efficiency and Magic Damage
	///         Modifier are ×100; Damage Deflection, Knockback Resistance and Chance of Being Staggered are ×1.
	///     </para>
	///     <para>
	///         ⚠️ <b>×10000 is real, not a transcription slip.</b> Five ids are in BOTH predicate sets and the client
	///         multiplies the two divisors: <c>0x00FF</c> Evade Level Growth, <c>0x013C</c> Variance, <c>0x013D</c>,
	///         <c>0x0140</c> Magic Variance and <c>0x0397</c> Critical Blast Chance. The composition is a
	///         <c>MULSS</c> against the same 100.0 constant, so it is explicit in the instruction stream.
	///     </para>
	///     <para>
	///         ⚠️ A divisor of 1 means the client prints the raw integer - it takes a different formatter, so "×1" is a
	///         genuine answer here and not the absent-from-the-table default it used to be.
	///     </para>
	///     <para>
	///         ⚠️ <b>This is only half of rendering a value.</b> The other half is the UNIT the client writes after it,
	///         which is a DIFFERENT function over a DIFFERENT id set - <see cref="StatUnitSuffix" />, transcribed from
	///         <c>FUN_01119f20</c> (#775). Neither is a function of the other: <c>0x001 HealthMax</c> is a ÷100 that
	///         draws nothing and <c>0x348 DamageMultiplier</c> a ÷1 that draws <c>%</c>. It also answers the note on
	///         <c>0x0177 FatalityChance</c> below - its trailing percent sign is that suffix, not a scale.
	///     </para>
	/// </remarks>
	public static class StatDisplayScale
	{
		// FUN_011197f0 - predicate 1. Grouped exactly as the decompiled switch groups them.
		private static readonly FrozenSet<uint> Percent1 = new uint[]
			{
				0x001, 0x008, 0x01B, 0x05A, 0x084, 0x099, 0x09E, 0x0A0, 0x0A1, 0x0A7, 0x0D2, 0x0D3, 0x0DE,
				0x0E2, 0x0E3, 0x0E4, 0x0E5, 0x0E6, 0x0E7, 0x0E8, 0x0E9, 0x0EA, 0x0EC, 0x0FC, 0x0FF,
				0x11D, 0x11E, 0x11F, 0x123, 0x124, 0x126, 0x13C, 0x13D, 0x140, 0x14A, 0x14E, 0x15B, 0x17C,
				0x1A5, 0x1C2, 0x1CE, 0x1DA, 0x1F1, 0x1F2, 0x1F8, 0x1F9, 0x1FA, 0x1FB, 0x218,
				0x2B5, 0x2C6, 0x35D, 0x36C, 0x36D, 0x36E, 0x36F, 0x370, 0x371, 0x372, 0x373, 0x374, 0x37E, 0x37F, 0x380,
				0x397, 0x398, 0x39A, 0x39B, 0x39D,
				0x40F, 0x411, 0x42F, 0x432, 0x449, 0x507, 0x508, 0x509, 0x547,
				0xF4255, 0xF4256, 0xF425C, 0xF425D
			}
			// The contiguous runs the switch spells out case-by-case, plus the four bare ranges it lets fall through
			// with no id test at all (0x114-0x11A, 0x230-0x232, 0x3CF-0x3D3, 0x434) and the lone 0x383.
			.Concat(Range(0x114, 0x11A)).Concat(Range(0x230, 0x232)).Concat(Range(0x383, 0x383))
			.Concat(Range(0x385, 0x38F)).Concat(Range(0x3A4, 0x3B6)).Concat(Range(0x3CF, 0x3D3))
			.Concat(Range(0x3DA, 0x3EB)).Concat(Range(0x434, 0x434))
			.ToFrozenSet();

		// FUN_01119ca0 - predicate 2.
		private static readonly FrozenSet<uint> Percent2 = new uint[]
			{
				0x081, 0x093, 0x0FD, 0x0FE, 0x0FF,
				0x120, 0x13C, 0x13D, 0x140, 0x144, 0x145, 0x146, 0x147, 0x148, 0x149, 0x15C,
				0x178, 0x179, 0x17A, 0x17B, 0x181, 0x182, 0x1A6, 0x1AB, 0x215,
				0x217, 0x219, 0x220, 0x221, 0x268,
				0x2D0, 0x2D1, 0x34D, 0x34E, 0x351, 0x352, 0x353, 0x354, 0x357, 0x358,
				0x382, 0x396, 0x397, 0x3CB, 0x3CD,
				0x42B, 0x42C, 0x447, 0x448,
				0xF425F
			}
			.ToFrozenSet();

		// The ×10 ids: the five character attributes (every other id across 0x324-0x32E) plus 0x57B Ferocity, which
		// the sheet lists as the sixth attribute. The client hardcodes exactly these seven and this branch OVERRIDES
		// the percent divisors above rather than composing with them.
		private static readonly FrozenSet<uint> Tenths = new uint[] { 0x324, 0x326, 0x328, 0x32A, 0x32C, 0x32E, 0x57B }.ToFrozenSet();

		/// <summary>The divisor the client applies to <paramref name="statId" />'s wire value before drawing it.</summary>
		public static uint Of(uint statId)
		{
			if (Tenths.Contains(statId))
				return 10;

			var scale = 1u;
			if (Percent1.Contains(statId))
				scale = 100;
			if (Percent2.Contains(statId))
				scale *= 100;

			return scale;
		}

		private static IEnumerable<uint> Range(uint from, uint to)
		{
			for (var i = from; i <= to; i++)
				yield return i;
		}
	}
}
