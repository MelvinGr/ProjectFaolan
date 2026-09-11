using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     The item template's property table, read whole - every <c>[fieldId u32][value u32]</c> pair the record
	///     opens with, including the ones a strictly-decreasing reader cannot reach.
	/// </summary>
	/// <remarks>
	///     <c>RdbItemImporter.TryReadPropertyField</c> takes the longest strictly-decreasing run and rejects
	///     <c>fieldId == 0</c>, which is right for the fields it was written for (74 value / 499 quality / 285-286
	///     damage) and blind to everything ahead of that run. The table is not one run: it is several descending runs
	///     back to back - a real template opens <c>76, 30, 0 | 298, 88 | 645, 644, 499, 496, 360, 148, 74, 54, 41,
	///     21</c> - so the longest-run reader sees only the last group. Field 0 (the client's <c>Flags</c> word) is
	///     the one that matters and is exactly the one <c>fieldId == 0</c> throws away: it sits at the end of the
	///     first run, where "strictly decreasing" is satisfied and the guard is not.
	///     <para />
	///     Grammar (validated by <c>--report item-binding</c>, which prints how many templates walk cleanly):
	///     <list type="bullet">
	///         <item>word 0 - the item's search category (30000 armor, 30023 cloak, ...), not a field id</item>
	///         <item>then, repeating - <c>[fieldId u32][value u32]</c></item>
	///         <item>
	///             a string value - <c>[fieldId][0x21][len u16][flags u16][len bytes, padded to a word]</c>; the
	///             display name is one (field 21), and <c>flags</c> is nonzero when the text carries backslash
	///             escapes
	///         </item>
	///         <item>the walk stops at the first word that cannot be a field id, or at the string, whichever comes first</item>
	///     </list>
	///     The string terminates the table. Field 21 (the display name) is the last pair in every template examined,
	///     and what follows it is the record's fixed regions - asset refs and the <c>[2][9]</c> stat-modifier records.
	///     Those are not <c>[fieldId][value]</c> pairs, but they hold plenty of words that pass the "at most 2000"
	///     field-id test - the list-framing word and its multiples above all - so a walk that carries on past the name
	///     does not stop, it drifts, and re-reads garbage under real field ids. Walking past the name gives 200-odd
	///     templates a <c>Flags</c> of <c>0xE2000000</c> / <c>0x0007E200</c> / <c>0x0BD30000</c>, which are list words
	///     at three different byte alignments. Stopping at the string removes all of them.
	///     <para />
	///     This is a prefix read, not the whole record. Stopping at the name is what keeps the walk honest, but it
	///     also means a field that sits beyond the name is invisible here: 25 templates carry field 499 (the quality
	///     tier) past it, which <c>RdbItemImporter.TryReadQualityAnywhere</c> does find. So this is the right reader
	///     for the fields that live in the head runs (0 <c>Flags</c>, 30 <c>Can</c>, 177 <c>More Flags</c>) and the
	///     wrong one to reach for when a lenient whole-record scan already exists. <c>--report item-binding</c> prints
	///     the disagreement rather than hiding it.
	///     <para />
	///     Field ids are wire stat ids (see <c>docs/rdb/item-template-stats.md</c>), so the client's own name table
	///     names them: 0 <c>Flags</c>, 30 <c>Can</c>, 177 <c>More Flags</c>, 499 <c>Power Level</c>, 54 the item
	///     level, 21 the display name.
	/// </remarks>
	public static class ItemTemplateProperties
	{
		/// <summary>
		///     A property value of 0x21 introduces a length-prefixed Latin-1 string instead of an integer. The same
		///     tag <c>RdbItemImporter.ExtractName</c> scans for; here it is read in place, and it ends the table.
		/// </summary>
		private const uint StringTag = 0x21;

		/// <summary>
		///     Field ids live in (0, 2000] plus 0 itself; the first word outside that is the end of the table. The
		///     same bound <c>TryReadPropertyField</c> uses, so the two readers agree about what a field id can be.
		/// </summary>
		private const uint MaxFieldId = 2000;

		private const int MaxStringLength = 1024;

		/// <summary>
		///     Walks the property table of a decrypted item template body (the span after the 12-byte RDB-Id header).
		/// </summary>
		/// <remarks>
		///     A repeated field is last-wins, matching every other reader over these records. <c>Strings</c> holds the
		///     string-valued fields (field 21 is the display name); <c>Fields</c> holds the integer ones.
		///     <c>EndWord</c> is where the walk stopped - a caller that wants to know the table was read to a sensible
		///     place can compare it against the record length.
		/// </remarks>
		public static Table Parse(ReadOnlySpan<byte> body)
		{
			var fields = new Dictionary<uint, uint>();
			var strings = new Dictionary<uint, string>();
			var words = body.Length / 4;
			var i = 1; // word 0 is the search category, not a field id

			while (i + 1 < words)
			{
				var field = body.ReadU32Le(i * 4);
				if (field > MaxFieldId)
					break;

				var value = body.ReadU32Le((i + 1) * 4);
				if (value == StringTag && TryReadString(body, i + 2, out var text, out var wordsConsumed))
				{
					strings[field] = text;
					i += 2 + wordsConsumed;
					break;
				}

				fields[field] = value;
				i += 2;
			}

			return new Table(fields, strings, i);
		}

		/// <summary>
		///     The <c>[len u16][flags u16][len bytes]</c> string body at word <paramref name="at" />, padded up to a
		///     word boundary. Returns false unless the length is plausible and the bytes are printable - an integer
		///     value that merely happens to be 0x21 (an item level of 33, say) must not derail the walk into reading
		///     the rest of the table as text.
		/// </summary>
		private static bool TryReadString(ReadOnlySpan<byte> body, int at, out string text, out int wordsConsumed)
		{
			text = "";
			wordsConsumed = 0;
			if (at >= body.Length / 4)
				return false;

			var length = body.ReadU16Le(at * 4);
			var start = (at + 1) * 4;
			if (length is 0 or > MaxStringLength || start + length > body.Length)
				return false;

			var bytes = body.Slice(start, length);
			foreach (var b in bytes)
			{
				if (b is < 32 or > 126)
					return false;
			}

			text = Encoding.Latin1.GetString(bytes);
			wordsConsumed = 1 + (length + 3) / 4; // the len/flags word, then the padded text
			return true;
		}

		public sealed record Table(IReadOnlyDictionary<uint, uint> Fields, IReadOnlyDictionary<uint, string> Strings, int EndWord)
		{
			/// <summary>
			///     The field's value, or 0 - which is what the client's own <c>getProp</c> returns for a field a
			///     template does not carry, so a rule expressed over these reads the same way the client's does.
			/// </summary>
			public uint Value(uint field)
			{
				return Fields.GetValueOrDefault(field);
			}
		}
	}
}
