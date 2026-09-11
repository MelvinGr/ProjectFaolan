using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Parses one LDB page (RDB type 1030002, "TDC2" localized-string table).
	/// </summary>
	/// <remarks>
	///     Grammar recovered from the client loader FUN_0060e5c0 (<c>docs/rdb/ldb-string-index.md</c>): a page carries
	///     an explicit per-string id index after the string blob; it is not a flat positional array. Body layout, all
	///     u32 little-endian:
	///     <code>
	///     [0..12)   RDB-Id header (type=1030002 | id | type2)
	///     [12..16)  magic "TDC2" (0x32434454)
	///     [16..20)  category      (50200 item dev-names, 50210 spell names, 50211 descriptions, 50025 area/dialogue, ...)
	///     [20..24)  flags
	///     [24..28)  size          (string-blob length in bytes)
	///     [28..32)  count         (number of index entries)
	///     [32..48)  16-byte MD5 of the blob
	///     [48..48+size)          the string blob
	///     [48+size .. +count*16) count x { u32 id, u32 langOrFlags, u32 offset, u32 length } - offset/length into blob
	///     </code>
	///     The string id is explicit (<c>index[i].id</c>), so <c>string[id]</c> is recovered directly. Language is per
	///     page, not per entry: the same (category, id) appears once per shipped language in adjacent page blocks (a
	///     client install bundles several, FR/EN/DE/ES/RU/PL/KO), so pick the right page (crib against known names)
	///     before trusting a value - see <c>RdbLdbNameImporter</c>.
	/// </remarks>
	public static class LdbStringIndex
	{
		public const uint ItemDescriptionCategory = 50201; // tooltip descriptions, keyed by item template id
		public const uint ItemNameCategory = 50200;

		/// <summary>
		///     The achievement catalog, keyed by the id the wire calls a tag (<c>DispatchAddTag</c>): 55701 the title,
		///     55700 what earns it. Verified against every tag the corpus awards - 7917 is "Seeing Red" / "Achieve a
		///     kill streak.", 716 "Old Tarantia Docks" (a vista). A third category, 55702, exists and is sparse (716
		///     has "???"); nothing reads it.
		/// </summary>
		public const uint AchievementDescriptionCategory = 55700;
		public const uint AchievementNameCategory = 55701;
		public const uint Magic = 0x32434454; // "TDC2" little-endian
		public const uint SpellDescriptionCategory = 50211; // spell tooltip descriptions, keyed by SPELL id
		public const uint SpellNameCategory = 50210;

		/// <summary>
		///     The client's stat-name table: id to name keyed by the wire stat id (1 "Health", 310 "Hit Rating", 975
		///     "Absorb", ...) - the same id space as the 0x96B8DC59 vitals block, the FullChar snapshot stat sheet, the
		///     item [2][9] modifier region and the spell stat effects. Localized like every other category, so crib
		///     the page (<c>--report ldb-stat-names</c> does it against <c>CharacterStatId</c>'s own names).
		/// </summary>
		public const uint StatNameCategory = 615;

		/// <summary>
		///     The id the client would look a token name up by: a string id is the 28-bit ELF/PJW hash of the literal
		///     token the exe passes to its LDB getter (<c>FUN_0061f640(&lt;category&gt;, "&lt;Token&gt;")</c>), so any
		///     token name found statically resolves to its shipped string with no live client.
		/// </summary>
		/// <remarks>
		///     Hashing is one-way and the id space is not uniformly hashed - a handful of ids are small and sequential
		///     (<c>Guard</c> resolves from both 246974048 and 28). A hit is evidence and a miss is not: it can mean
		///     the token is spelled differently, or that this particular entry is one of the sequential ones. See
		///     <c>docs/rdb/ldb-string-index.md</c>.
		/// </remarks>
		public static uint TokenId(string token)
		{
			return Elf28.Of(token);
		}

		/// <summary>The page's category, or 0 if the body is not a TDC2 page (too short, or the wrong magic).</summary>
		public static uint CategoryOf(ReadOnlySpan<byte> body)
		{
			if (body.Length < 48)
				return 0;

			var span = body[12..];
			return span.ReadU32Le(0) != Magic
				? 0u
				: span.ReadU32Le(4);
		}

		/// <summary>
		///     Yields every (category, id, value) entry in the page, decoded as a UTF-8 string. Empty if the body is
		///     not a well-formed TDC2 page.
		/// </summary>
		/// <remarks>
		///     Only meaningful for a plaintext page. On one of the eleven encrypted categories the blob is ciphertext,
		///     and both steps this takes - truncating at the first NUL and UTF-8 decoding - destroy it. Use
		///     <see cref="ParseRaw" /> there and decrypt the bytes first (<c>LdbKey</c> plus <c>Tea.DecryptEcb</c>);
		///     the NUL padding is part of the plaintext, so it is stripped after decryption, not before.
		/// </remarks>
		public static IEnumerable<Entry> Parse(byte[] body)
		{
			foreach (var (category, id, bytes) in ParseRaw(body))
			{
				var strBytes = bytes.AsSpan();
				var nul = strBytes.IndexOf((byte)0);
				if (nul >= 0)
					strBytes = strBytes[..nul];
				yield return new Entry(category, id, Encoding.UTF8.GetString(strBytes));
			}
		}

		/// <summary>
		///     The same walk, but handing back each entry's blob bytes verbatim - no NUL truncation, no text decoding.
		///     This is the form an encrypted category needs: its blob is a TEA-ECB ciphertext whose length is a
		///     multiple of 8 and which contains NUL bytes freely, so anything that treats it as text loses data before
		///     it can be decrypted.
		/// </summary>
		public static IEnumerable<RawEntry> ParseRaw(byte[] body)
		{
			if (body.Length < 48)
				yield break;

			// AsSpan cannot cross a yield, so read the header fields up front into locals.
			int blobStart, indexStart, count;
			uint category, size;
			{
				var span = body.AsSpan(12);
				if (span.ReadU32Le(0) != Magic)
					yield break;

				category = span.ReadU32Le(4);
				size = span.ReadU32Le(12);
				count = (int)span.ReadU32Le(16);
				blobStart = 12 + 36; // RDB-Id(12) + TDC2(4) + cat/flags/size/count(16) + MD5(16)
				indexStart = blobStart + (int)size;
			}

			if (count < 0 || indexStart + (long)count * 16 > body.Length)
				yield break; // grammar mismatch - don't emit garbage

			for (var i = 0; i < count; i++)
			{
				var rec = body.AsSpan(indexStart + i * 16, 16);
				var id = rec.ReadU32Le(0);
				var off = rec.ReadU32Le(8);
				var len = rec.ReadU32Le(12);
				if ((long)off + len > size)
					continue;

				yield return new RawEntry(category, id, body.AsSpan(blobStart + (int)off, (int)len).ToArray());
			}
		}

		/// <summary>
		///     The page header's <c>flags</c> word, which is an encryption flag: 1 on the eleven encrypted categories
		///     (the NPC dialogue 50000-50003 and the quest text 50300-50306) and 0 on the other 147. It separates the
		///     two sets perfectly, so it is the page's own statement of whether <see cref="ParseRaw" /> plus decrypt
		///     is required rather than something to infer from the bytes. See <c>docs/rdb/ldb-string-index.md</c>.
		/// </summary>
		public static uint FlagsOf(ReadOnlySpan<byte> body)
		{
			return body.Length < 48 || body[12..].ReadU32Le(0) != Magic
				? 0u
				: body[12..].ReadU32Le(8);
		}

		/// <summary>
		///     (category, id) to the finished string, over the English pages of the plaintext categories - roughly
		///     half the LDB (52000 regions, 51200 bounded_areas, 51000 npc_template, 50200/50201 item, 55000
		///     simpledynels, ...). A <c>&lt;remoteformat&gt;</c> or typed reference into one of these carries no key
		///     and needs none, so resolving it is a dictionary lookup once this walk has run.
		/// </summary>
		/// <remarks>
		///     Both filters are the page's own statement rather than something sniffed from the text. The language
		///     comes from the client's language records (whole pages ship untranslated, so a text sniffer unions
		///     several languages and silently yields French), and <c>flags</c> is the header's encryption bit, whose
		///     set is exactly the eleven encrypted categories <c>EncryptedLdbBlobs</c> owns.
		/// </remarks>
		public static Dictionary<(uint Category, uint Id), string> EnglishPlaintext(RdbReader rdb)
		{
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var strings = new Dictionary<(uint, uint), string>();

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // the container is not an IOz1/zlib page
				}

				if (body.Length == 0)
					continue; // a CDN page the mirror could not serve (RdbReader returns empty rather than throwing)

				if (CategoryOf(body) == 0 || FlagsOf(body) == 1)
					continue; // not a TDC2 page, or an encrypted one

				foreach (var entry in Parse(body))
					strings[(entry.Category, entry.Id)] = entry.Value;
			}

			return strings;
		}

		/// <summary>
		///     The encrypted twin of <see cref="EnglishPlaintext" />: the ciphertext of every string on an English
		///     encrypted page, ready to be handed to <c>LdbKey.Decrypt</c> with the per-string key our own tables
		///     hold. The eleven encrypted categories (quest objectives, NPC dialogue, ...) are not readable from the
		///     RDB alone - the key is per string and the server sends it - so the readable set is exactly what we have
		///     captured a <c>key=</c> for.
		/// </summary>
		/// <remarks>
		///     Here rather than in a report because the importer wants it too
		///     (<c>RdbDialogueTeleportImporter</c>), which is the same reason <see cref="EnglishPlaintext" /> lives
		///     here: this is the RDB grammar's concern, not a report's.
		/// </remarks>
		public static Dictionary<(uint Category, uint Id), byte[]> EnglishCiphertext(RdbReader rdb)
		{
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var blobs = new Dictionary<(uint, uint), byte[]>();

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // the container is not an IOz1/zlib page
				}

				if (body.Length == 0)
					continue; // a CDN page the mirror could not serve (RdbReader returns empty rather than throwing)

				if (CategoryOf(body) == 0 || FlagsOf(body) != 1)
					continue; // not a TDC2 page, or a plaintext one

				foreach (var entry in ParseRaw(body))
					blobs[(entry.Category, entry.Id)] = entry.Bytes;
			}

			return blobs;
		}

		public readonly record struct Entry(uint Category, uint Id, string Value);

		public readonly record struct RawEntry(uint Category, uint Id, byte[] Bytes);
	}
}
