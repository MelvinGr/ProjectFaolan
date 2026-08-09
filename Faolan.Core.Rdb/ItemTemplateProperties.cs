using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	// The item template's PROPERTY TABLE, read whole - every `[fieldId u32][value u32]` pair the record opens with,
	// including the ones the strictly-decreasing reader cannot reach.
	//
	// `RdbItemImporter.TryReadPropertyField` takes the LONGEST strictly-decreasing run and rejects `fieldId == 0`,
	// which is right for the fields it was written for (74 value / 499 quality / 285-286 damage) and blind to
	// everything ahead of that run. The table is not one run: it is SEVERAL descending runs back to back - a real
	// template opens `76, 30, 0 | 298, 88 | 645, 644, 499, 496, 360, 148, 74, 54, 41, 21` - so the longest-run
	// reader silently sees only the last group. Field **0** (the client's `Flags` word) is the one that matters and
	// is exactly the one `fieldId == 0` throws away: it sits at the END of the first run, where "strictly
	// decreasing" is satisfied and the guard is not.
	//
	// Grammar (validated by `--report item-binding`, which prints how many templates walk cleanly):
	//   word 0            the item's SEARCH CATEGORY (30000 armor, 30023 cloak, ...) - not a field id
	//   then, repeating   [fieldId u32][value u32]
	//   a STRING value    `[fieldId][0x21][len u16][flags u16][len bytes, padded to a word]` - the display name is
	//                     one (field 21); `flags` is nonzero when the text carries backslash escapes (#202)
	//   the walk stops    at the first word that cannot be a field id, or at the STRING, whichever comes first
	//
	// ⚠️ **The string TERMINATES the table.** Field 21 (the display name) is the last pair in every template
	// examined, and what follows it is the record's fixed regions - asset refs and the `[2][9]` stat-modifier
	// records. Those are not `[fieldId][value]` pairs, but they hold plenty of words that PASS the "<= 2000"
	// field-id test - the list-framing word `0x3F1` and its multiples above all - so a walk that carries on past
	// the name does not stop, it DRIFTS, and re-reads garbage under real field ids. Not theoretical: walking past
	// the name gave 200-odd templates a `Flags` of `0xE2000000` / `0x0007E200` / `0x0BD30000`, which are the list
	// words `0x7E2` and `0xBD3` at three different byte alignments (CLAUDE.md: there is no such thing as a
	// `0x7E2` constant). Stopping at the string removes all of them.
	//
	// ⚠️ **This is a PREFIX read, not the whole record.** Stopping at the name is what keeps the walk honest, but it
	// also means a field that sits BEYOND the name is invisible here: 25 templates carry field 499 (the quality tier)
	// past it, which `RdbItemImporter.TryReadQualityAnywhere` does find. So this is the right reader for the fields
	// that live in the head runs (0 `Flags`, 30 `Can`, 177 `More Flags`) and the WRONG one to reach for when a lenient
	// whole-record scan already exists. `--report item-binding` prints the disagreement rather than hiding it.
	//
	// Field ids ARE wire stat ids (see docs/rdb/item-template-stats.md), so the client's own name table names them:
	// 0 `Flags`, 30 `Can`, 177 `More Flags`, 499 `Power Level`, 54 the item level, 21 the display name.
	public static class ItemTemplateProperties
	{
		// A property value of 0x21 introduces a length-prefixed Latin-1 string instead of an integer. Same tag
		// RdbItemImporter.ExtractName scans for; here it is read in place, and it ENDS the table (see above).
		private const uint StringTag = 0x21;

		// Field ids live in (0, 2000] plus 0 itself; the first word outside that is the end of the table. Same bound
		// TryReadPropertyField uses, so the two readers agree about what a field id can be.
		private const uint MaxFieldId = 2000;

		private const int MaxStringLength = 1024;

		// Walks the property table of a decrypted item template BODY (the span AFTER the 12-byte RDB-Id header).
		//
		// A repeated field is last-wins, matching every other reader over these records. `Strings` holds the
		// string-valued fields (field 21 is the display name); `Fields` holds the integer ones. `EndWord` is where
		// the walk stopped - a caller that wants to know the table was read to a sensible place can compare it
		// against the record length.
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

		// The `[len u16][flags u16][len bytes]` string body at word `at`, padded up to a word boundary. Returns false
		// unless the length is plausible AND the bytes are printable - an integer value that merely happens to be
		// 0x21 (an item level of 33, say) must not derail the walk into reading the rest of the table as text.
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
			// The field's value, or 0 - which is what the client's own `getProp` returns for a field a template does
			// not carry, so a rule expressed over these reads the same way the client's does.
			public uint Value(uint field)
			{
				return Fields.GetValueOrDefault(field);
			}
		}
	}
}
