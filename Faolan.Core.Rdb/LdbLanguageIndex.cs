using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Core.Rdb
{
	// Which LANGUAGE an LDB page (RDB 1030002) is in - stated by the client, not inferred from the text.
	//
	// RDB type 1030001 is the language record ("TDL1"): the install ships exactly one per bundled language (English,
	// French, German, Spanish, Polish, Russian, Korean), each naming itself in plain ASCII and listing the 152 LDB
	// pages that belong to it. So page -> language is a lookup, and the English page of a category is picked by name.
	//
	// ⚠️ This replaced a text-sniffing heuristic ("which page reads as English prose"), which is not merely
	// approximate - it is WRONG on this data: whole pages ship UNTRANSLATED, so the Polish and Korean pages of the
	// spell-description category (50211) contain English sentences and score as English. A sniffer therefore unions
	// several languages' pages and the last write wins per id, which is how 1991 of 3380 spell descriptions ended up
	// Spanish. Never guess a language from the text when the client declares it.
	//
	// Record layout (only what is needed to answer "whose pages are these"):
	//   [0..12)   RDB-Id header (type | id | type2)
	//   [12..16)  magic "TDL1"
	//   ...       the language NAME as ASCII (twice: the English name then the localized one), a plural-form rule
	//             ("(N == 1) /? 1 /: -1"), then a table of the language's page ids.
	// The page table is a run of { u32 pageId, u32 } pairs, and it is NOT word-aligned in every record - the leading
	// strings differ in length per language, so the table starts on a byte offset that is only sometimes a multiple
	// of 4 (it is in the French/Spanish records, it is not in the English one). Hence the byte-granular scan below,
	// anchored on "this value is the id of a real 1030002 page".
	public static class LdbLanguageIndex
	{
		public const string English = "English";
		public const uint LanguageRecordType = 1030001;
		public const uint Magic = 0x314C4454; // "TDL1" little-endian
		public const uint PageType = 1030002;

		// Reads every language record and returns page id -> language name. Pages no record claims are absent (39 of
		// 1103 in the shipped install - stale/duplicate pages), so a caller filtering on a language never picks them up.
		public static Dictionary<uint, string> LanguageByPage(RdbReader rdb)
		{
			var pages = rdb.EntriesOfType(PageType).Where(x => x.IsLocal).Select(x => x.Id).ToHashSet();
			var byPage = new Dictionary<uint, string>();

			foreach (var entry in rdb.EntriesOfType(LanguageRecordType).Where(x => x.IsLocal))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue; // not a readable record
				}

				var (name, claimed) = ParseRecord(body, pages);
				if (name == null)
					continue;

				foreach (var page in claimed)
					byPage[page] = name;
			}

			return byPage;
		}

		// One language record: the language it declares, and the pages it claims out of `knownPages`. Returns a null
		// name for a body that is not a TDL1 record (or names nothing), and then claims nothing.
		public static (string? Language, IReadOnlyList<uint> Pages) ParseRecord(ReadOnlySpan<byte> body, IReadOnlyCollection<uint> knownPages)
		{
			if (body.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12, 4)) != Magic)
				return (null, []);

			var name = NameOf(body);
			if (name == null)
				return (null, []);

			// Byte-granular (see the type comment): every 4-byte window that IS a page id belongs to this language.
			// The id space is sparse enough that a coincidental hit would have to equal a real page id exactly.
			var claimed = new List<uint>();
			for (var off = 16; off + 4 <= body.Length; off++)
			{
				var value = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(off, 4));
				if (knownPages.Contains(value) && !claimed.Contains(value))
					claimed.Add(value);
			}

			return (name, claimed);
		}

		// The language this record declares: the first ASCII run after the magic that is not the magic itself. The
		// name appears twice (English name, then the localized one - "French" then "Français"); the first is the one
		// that is stable across languages, so it is the one used.
		private static string? NameOf(ReadOnlySpan<byte> body)
		{
			var run = new StringBuilder();
			for (var i = 16; i < body.Length; i++)
			{
				var c = body[i];
				if (c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
				{
					run.Append((char)c);
					continue;
				}

				if (run.Length >= 4)
					return run.ToString();

				run.Clear();
			}

			return run.Length >= 4
				? run.ToString()
				: null;
		}
	}
}
