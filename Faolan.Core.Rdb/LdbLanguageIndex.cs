using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Which language an LDB page (RDB 1030002) is in - stated by the client, not inferred from the text.
	/// </summary>
	/// <remarks>
	///     RDB type 1030001 is the language record ("TDL1"): the install ships exactly one per bundled language
	///     (English, French, German, Spanish, Polish, Russian, Korean), each naming itself in plain ASCII and listing
	///     the 152 LDB pages that belong to it. Page to language is therefore a lookup, and the English page of a
	///     category is picked by name.
	///     <para />
	///     Never guess a language from the text. Whole pages ship untranslated, so the Polish and Korean pages of the
	///     spell-description category (50211) contain English sentences and score as English to a text sniffer; such a
	///     sniffer unions several languages' pages and the last write wins per id.
	///     <para />
	///     Record layout (only what is needed to answer "whose pages are these"):
	///     <code>
	///     [0..12)   RDB-Id header (type | id | type2)
	///     [12..16)  magic "TDL1"
	///     ...       the language name as ASCII (twice: the English name then the localized one), a plural-form rule
	///               ("(N == 1) /? 1 /: -1"), then a table of the language's page ids.
	///     </code>
	///     The page table is a run of <c>{ u32 pageId, u32 }</c> pairs, and it is not word-aligned in every record -
	///     the leading strings differ in length per language, so the table starts on a byte offset that is only
	///     sometimes a multiple of 4 (it is in the French and Spanish records, it is not in the English one). Hence
	///     the byte-granular scan below, anchored on "this value is the id of a real 1030002 page".
	/// </remarks>
	public static class LdbLanguageIndex
	{
		public const string English = "English";
		public const uint LanguageRecordType = 1030001;
		public const uint Magic = 0x314C4454; // "TDL1" little-endian
		public const uint PageType = 1030002;

		/// <summary>
		///     Reads every language record and returns page id to language name. Pages no record claims are absent
		///     (39 of 1103 in the shipped install - stale or duplicate pages), so a caller filtering on a language
		///     never picks them up.
		/// </summary>
		public static Dictionary<uint, string> LanguageByPage(RdbReader rdb)
		{
			var pages = rdb.EntriesOfType(PageType).Where(rdb.CanRead).Select(x => x.Id).ToHashSet();
			var byPage = new Dictionary<uint, string>();

			foreach (var entry in rdb.EntriesOfType(LanguageRecordType).Where(rdb.CanRead))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue; // the container is not an IOz1/zlib record
				}

				if (body.Length == 0)
					continue; // a CDN record the mirror could not serve (RdbReader returns empty rather than throwing)

				var (name, claimed) = ParseRecord(body, pages);
				if (name == null)
					continue;

				foreach (var page in claimed)
					byPage[page] = name;
			}

			return byPage;
		}

		/// <summary>
		///     One language record: the language it declares, and the pages it claims out of
		///     <paramref name="knownPages" />. Returns a null name for a body that is not a TDL1 record (or names
		///     nothing), and then claims nothing.
		/// </summary>
		public static (string? Language, IReadOnlyList<uint> Pages) ParseRecord(ReadOnlySpan<byte> body, IReadOnlyCollection<uint> knownPages)
		{
			if (body.Length < 16 || body.ReadU32Le(12) != Magic)
				return (null, []);

			var name = NameOf(body);
			if (name == null)
				return (null, []);

			// Byte-granular (see the type remarks): every 4-byte window that is a page id belongs to this language.
			// The id space is sparse enough that a coincidental hit would have to equal a real page id exactly.
			var claimed = new List<uint>();
			for (var off = 16; off + 4 <= body.Length; off++)
			{
				var value = body.ReadU32Le(off);
				if (knownPages.Contains(value) && !claimed.Contains(value))
					claimed.Add(value);
			}

			return (name, claimed);
		}

		/// <summary>
		///     The language this record declares: the first ASCII run after the magic that is not the magic itself.
		///     The name appears twice (the English name, then the localized one - "French" then "Francais"); the first
		///     is the one that is stable across languages, so it is the one used.
		/// </summary>
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
