using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Rdb
{
	// Parses one LDB page (RDB type 1030002, "TDC2" localized-string table). Grammar recovered from the client
	// loader FUN_0060e5c0 (#291, docs/rdb/ldb-string-index.md): a page carries an EXPLICIT per-string id index AFTER
	// the string blob - it is NOT a flat positional array (the earlier "no per-string id, unsolvable offline" read
	// mis-parsed the blob as NUL-separated strings and never found the trailing id table). Body layout, all u32 LE:
	//
	//   [0..12)   RDB-Id header (type=1030002 | id | type2)
	//   [12..16)  magic "TDC2" (0x32434454)
	//   [16..20)  category      (50200 item dev-names, 50210 spell names, 50211 descriptions, 50025 area/dialogue, …)
	//   [20..24)  flags
	//   [24..28)  size          (string-blob length in bytes)
	//   [28..32)  count          (number of index entries)
	//   [32..48)  16-byte MD5 of the blob
	//   [48..48+size)          the string blob
	//   [48+size .. +count*16) count × { u32 id, u32 langOrFlags, u32 offset, u32 length }  - offset/length into blob
	//
	// The string id is EXPLICIT (index[i].id), so string[id] is recovered directly. LANGUAGE is per-PAGE, not
	// per-entry: the same (category, id) appears once per shipped language in adjacent page blocks (a client install
	// bundles several, e.g. FR/EN/DE/ES/RU/PL/KO), so pick the right page (crib against known names) before trusting
	// a value - see RdbLdbNameImporter.
	public static class LdbStringIndex
	{
		public const uint ItemDescriptionCategory = 50201; // tooltip descriptions, keyed by item template id
		public const uint ItemNameCategory = 50200;
		public const uint Magic = 0x32434454; // "TDC2" little-endian
		public const uint SpellDescriptionCategory = 50211; // spell tooltip descriptions, keyed by SPELL id
		public const uint SpellNameCategory = 50210;

		// The client's STAT-NAME table: id -> name keyed by the WIRE stat id (1 "Health", 310 "Hit Rating", 975
		// "Absorb", …) - the same id space as the 0x96B8DC59 vitals block, the FullChar snapshot stat sheet, the item
		// [2][9] modifier region and the spell stat effects. Localized like every other category, so crib the page
		// (ReportCli's ldb-stat-names does it against CharacterStatId's own names).
		public const uint StatNameCategory = 615;

		// The page's category, or 0 if the body is not a TDC2 page (too short / wrong magic).
		public static uint CategoryOf(ReadOnlySpan<byte> body)
		{
			if (body.Length < 48)
				return 0;

			var span = body[12..];
			return BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic
				? 0u
				: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
		}

		// Yields every (category, id, value) entry in the page. Empty if the body is not a well-formed TDC2 page.
		public static IEnumerable<Entry> Parse(byte[] body)
		{
			if (body.Length < 48)
				yield break;

			// AsSpan can't cross a yield, so read the header fields up front into locals.
			int blobStart, indexStart, count;
			uint category, size;
			{
				var span = body.AsSpan(12);
				if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
					yield break;

				category = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
				size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
				count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
				blobStart = 12 + 36; // RDB-Id(12) + TDC2(4) + cat/flags/size/count(16) + MD5(16)
				indexStart = blobStart + (int)size;
			}

			if (count < 0 || indexStart + (long)count * 16 > body.Length)
				yield break; // grammar mismatch - don't emit garbage

			for (var i = 0; i < count; i++)
			{
				var rec = body.AsSpan(indexStart + i * 16, 16);
				var id = BinaryPrimitives.ReadUInt32LittleEndian(rec);
				var off = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8, 4));
				var len = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(12, 4));
				if ((long)off + len > size)
					continue;

				var strBytes = body.AsSpan(blobStart + (int)off, (int)len);
				var nul = strBytes.IndexOf((byte)0);
				if (nul >= 0)
					strBytes = strBytes[..nul];
				yield return new Entry(category, id, Encoding.UTF8.GetString(strBytes));
			}
		}

		public readonly record struct Entry(uint Category, uint Id, string Value);
	}
}
