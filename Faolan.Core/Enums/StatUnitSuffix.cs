using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Enums
{
	/// <summary>
	///     The UNIT a stat's value is drawn with - <c>%</c>, <c>m</c>, or nothing - transcribed from
	///     <c>AgeOfConan.exe</c>'s own suffix helper (#775).
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The twin of <see cref="StatDisplayScale" />, and NOT derivable from it.</b> That one answers "what
	///         does the client divide the wire value by"; this one answers "what does it write after the result", and
	///         the two sets genuinely differ: <c>0x001 HealthMax</c> is a ÷100 with no suffix (a vital, not a
	///         percentage) while <c>0x348 DamageMultiplier</c> is a ÷1 that draws <c>%</c>. Rendering a modifier needs
	///         both, so guessing one from the other reports <c>-5</c> as <c>-5</c> and <c>-1000</c> as <c>-1000%</c>.
	///     </para>
	///     <para>
	///         <b>Where it comes from.</b> <c>FUN_01119f20(text, statId)</c> appends one of three literals: the
	///         <c>"%"</c> at <c>0x01fbcf18</c>, the <c>"m"</c> at <c>0x01fb3ac8</c>, or the empty string at
	///         <c>0x02c23bf8</c>. It is five ranges, four of them MSVC dense switches (an index byte table plus a
	///         dword target table - <c>tools/ghidra/DumpSwitchTable.java</c> composes the pair) and the fifth a run of
	///         plain compares. The id sets below are those tables read out, literally.
	///     </para>
	///     <para>
	///         ⚠️ <b>Only two ids take the metre suffix</b> - <c>0x0DE SpellColumnRadius</c> and <c>0x11F
	///         AttackRange</c> - and both are on the spell record's property run, which is what makes the third unit
	///         worth carrying at all: without it a spell's range renders as a bare number.
	///     </para>
	///     <para>
	///         ⚠️ This says how a value is DRAWN, not what it means. A stat can be a ratio the client happens to show
	///         raw (<c>0x177 FatalityChance</c> takes no suffix), and an id absent from every set below is drawn bare
	///         because the client's formatter appends the empty string - an answer, not a gap.
	///     </para>
	/// </remarks>
	public enum StatUnit
	{
		/// <summary>Drawn bare - the client appends the empty string at <c>0x02c23bf8</c>.</summary>
		None,

		/// <summary>Drawn with <c>%</c> (the literal at <c>0x01fbcf18</c>).</summary>
		Percent,

		/// <summary>Drawn with <c>m</c> (the literal at <c>0x01fb3ac8</c>) - metres.</summary>
		Metres
	}

	/// <summary>
	///     <see cref="Of" /> is the client's <c>FUN_01119f20</c>: the unit suffix it draws after a stat's value.
	///     See <see cref="StatUnit" /> for where the tables come from.
	/// </summary>
	public static class StatUnitSuffix
	{
		// The four dense switches, each transcribed as the ids that reach the "%" branch (0x0111a02f). The ranges are
		// the switch bounds themselves - `ADD EAX,-<base>` then `CMP EAX,<count>` - and every id inside a range that
		// is not listed here reaches the empty-suffix branch.
		private static readonly FrozenSet<uint> PercentIds = new uint[]
			{
				// 0x031..0x120 (table @0x0111a060 / targets @0x0111a054)
				0x031, 0x04B, 0x081, 0x093, 0x0A7, 0x0AA, 0x0E4, 0x0E5, 0x0E6, 0x0E7, 0x0E8, 0x0E9, 0x0EA, 0x0EC,
				0x0F0, 0x0F1, 0x0FC, 0x0FD, 0x0FE, 0x0FF, 0x119, 0x11A, 0x120,

				// 0x13F..0x221 (table @0x0111a158 / targets @0x0111a150)
				0x13F, 0x140, 0x144, 0x145, 0x146, 0x147, 0x148, 0x149, 0x14A, 0x14E, 0x15C, 0x168,
				0x177, 0x178, 0x179, 0x17A, 0x17B, 0x17C, 0x181, 0x182, 0x1A6, 0x1AB, 0x1C2, 0x1DA,
				0x215, 0x217, 0x218, 0x219, 0x220, 0x221,

				// 0x2B2..0x397 (table @0x0111a244 / targets @0x0111a23c)
				0x2B2, 0x2B5, 0x2D0, 0x2D1, 0x323, 0x348, 0x34D, 0x34E, 0x351, 0x352, 0x353, 0x354, 0x357, 0x358,
				0x37E, 0x37F, 0x380, 0x382, 0x386, 0x387, 0x388, 0x389, 0x38A, 0x38B, 0x38C, 0x38D, 0x38E, 0x38F,
				0x396, 0x397,

				// 0x3CD..0x42F (table @0x0111a334 / targets @0x0111a32c)
				0x3CD, 0x409, 0x411, 0x42B, 0x42C, 0x42F,

				// The plain compares that bracket the switches, in the client's own order: the two ids either side of
				// the first table's ceiling, then one each between the next three, then the 0xF42xx block's members
				// and the 0x507..0x509 run.
				0x13C, 0x13D, 0x268, 0x3CB, 0x447, 0x448, 0x562,
				0xF4255, 0xF4256, 0xF425F
			}
			.Concat(Range(0x507, 0x509))
			.ToFrozenSet();

		// The only two ids that take the metre suffix - both spell-record fields.
		private static readonly FrozenSet<uint> MetreIds = new uint[] { 0x0DE, 0x11F }.ToFrozenSet();

		/// <summary>The unit the client draws after <paramref name="statId" />'s value.</summary>
		public static StatUnit Of(uint statId)
		{
			if (MetreIds.Contains(statId))
				return StatUnit.Metres;

			return PercentIds.Contains(statId) ? StatUnit.Percent : StatUnit.None;
		}

		/// <summary>The literal the client appends - <c>"%"</c>, <c>"m"</c> or <c>""</c>.</summary>
		public static string SuffixOf(uint statId)
		{
			return Of(statId) switch
			{
				StatUnit.Percent => "%",
				StatUnit.Metres => "m",
				_ => ""
			};
		}

		private static IEnumerable<uint> Range(uint from, uint to)
		{
			for (var i = from; i <= to; i++)
				yield return i;
		}
	}
}
