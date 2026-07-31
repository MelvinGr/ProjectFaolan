using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot's POST-ACCESS-WINDOW block - everything after the PFAccessWindow_ catalog, 1.3-2.7 KB per
	// subclass and the largest region of the login snapshot that was ever carried opaque (#393). Recovered from the
	// client's own reader (`CoreFullPlayerCharacterIIR::ReadSubClass` = FUN_00797440, then FullPlayerCharacterIIR's
	// FUN_0148c210) rather than guessed:
	//
	//   List D      FUN_0077d220 / FUN_0077db70   0x3F1-framed, VARIABLE-width records   EMPTY in all 151 captures
	//   List E      FUN_0077d5b0 / FUN_0077df00   0x3F1-framed, VARIABLE-width records   EMPTY in all 151 captures
	//   StatList#1  FUN_004d5900                  0x3F1-framed, [u32 statId][u32 value]  the 17 DISPLAYED stats
	//   StatList#2  FUN_0139d450 / FUN_0139d210   0x3F1-framed, [u32 statId][u32 value]  74 sheet pairs, TWICE
	//   SpellBook_t FUN_0077af90                  0x3F1-framed, 3 x u32, CONDITIONAL     the known-spell list (#74)
	//   wide tail   FUN_00797440's tail           [u32 n][ n x ([u8][u64][u64]) ]
	//   pair tail   FUN_0148c210                  [u32 m][ m x ([u32][u32]) ]
	//
	// ⚠️ Two long-standing errors in the write-up this corrects, both found by decompiling rather than by staring at
	// bytes:
	//   - `SpellBook_t` was documented as "records of 20 bytes (stride 0x14)". The 0x14 is the IN-MEMORY array
	//     stride; the reader makes THREE 4-byte reads per record, so the WIRE record is 12 bytes - which makes it the
	//     same list KnownSpellListCodec models (#74), not a separate one.
	//   - the final list was documented as `count x [u32,u32]`. There are TWO trailing lists, and the first one's
	//     element is 17 bytes: `FUN_0048f720` reads 8 bytes, and the loop reads one raw byte ahead of the two.
	//
	// Lists D and E are still LISTS - `ForCount(0)` is 0x3F1, the trap this codebase keeps re-learning - so they are
	// read as such rather than asserted to be a constant; they are what says a post-access block starts here. Their
	// record shape is unobservable from the corpus (every capture has them empty), so nothing beyond the frame is
	// modelled and nothing needs to be: an empty list writes back as its frame word.
	//
	// The SPELLBOOK list is genuinely optional, not merely empty: 130 login snapshots carry one (0 to 127 records)
	// and the 21 create-editor snapshots end at StatList#2, going straight to the two zero-count tails. An absent
	// list and an empty one differ on the wire (nothing vs 0x3F1), so the model distinguishes them.
	//
	// All 151 captured blocks parse to their exact last byte and re-encode byte-exact
	// (SubClassPostAccessBlockTests). This replaced a flat `uint[]` view plus a separate backtracking walker over the
	// same bytes (`SnapshotTailRegionCodec`) - two spellings of one layout, which is what #543 was.
	public static class SubClassPostAccessBlockCodec
	{
		// Refuses a wildly out-of-range trailing count before it is used as a length: these two lists are
		// per-character counter tables, so a four-digit count means we are not at the trailing pair after all.
		private const uint MaxTrailingCount = 0x1000;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SubClassPostAccessBlock value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SubClassPostAccessBlock Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SubClassPostAccessBlockCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static void Write(FaolanStream s, SubClassPostAccessBlock v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.Empty); // List D
			s.WriteUInt32(ListFrame.Empty); // List E
			StatListCodec.Write(s, v.Displayed);
			StatListCodec.Write(s, v.Sheet);

			if (v.KnownSpells != null)
				KnownSpellListCodec.Write(s, v.KnownSpells);

			s.WriteUInt32((uint)v.WideTail.Count);
			foreach (var entry in v.WideTail)
				s.WriteByte(entry.Flag).WriteUInt64(entry.A).WriteUInt64(entry.B);

			s.WriteUInt32((uint)v.PairTail.Count);
			foreach (var pair in v.PairTail)
				s.WriteUInt32(pair.Id).WriteUInt32(pair.Value);
		}

		internal static SubClassPostAccessBlock Read(ref SpanReader r)
		{
			// The two leading EMPTY framed lists are what says a post-access block starts here.
			r.ExpectU32Be(ListFrame.Empty, "post-access List D frame");
			r.ExpectU32Be(ListFrame.Empty, "post-access List E frame");

			var displayed = StatListCodec.Read(ref r);
			var sheet = StatListCodec.Read(ref r);

			var block = new SubClassPostAccessBlock
			{
				Displayed = displayed,
				Sheet = sheet,
				KnownSpells = HasSpellBook(r) ? KnownSpellListCodec.Read(ref r) : null
			};

			ReadTails(ref r, block);
			return block;
		}

		// Whether a spellbook list follows. The discriminator is the frame word itself: a 0x3F1-framed count cannot
		// be mistaken for the wide tail's count, because the smallest one (0x3F1 = 1009) would claim 1009 x 17 bytes
		// - far past the largest captured block - while the tails' own counts are single digits. The `fits` test
		// says exactly that rather than trusting the arithmetic to stay true.
		private static bool HasSpellBook(SpanReader r)
		{
			if (r.Remaining < 4 || !ListFrame.TryCountOf(r.PeekU32Be(), out var records))
				return false;

			return 4 + (long)records * KnownSpellListCodec.RecordSize + 8 <= r.Remaining;
		}

		// The two plainly-counted lists that close the region. Their counts are read as counts, not peeked at: by
		// here the framed lists are all consumed, so anything that does not land exactly on the region's end is a
		// block we have mis-modelled and Parse's EnsureAtEnd says so.
		private static void ReadTails(ref SpanReader r, SubClassPostAccessBlock block)
		{
			var wide = r.ReadU32Be();
			if (wide > MaxTrailingCount)
				throw CodecParseException.Unexpected(nameof(SubClassPostAccessBlockCodec), r.Position - 4, $"a wide-tail count <= {MaxTrailingCount}", wide.ToString());

			for (var i = 0; i < wide; i++)
				block.WideTail.Add(new SnapshotTailWideEntry(r.ReadU8(), r.ReadU64Be(), r.ReadU64Be()));

			var pairs = r.ReadU32Be();
			if (pairs > MaxTrailingCount)
				throw CodecParseException.Unexpected(nameof(SubClassPostAccessBlockCodec), r.Position - 4, $"a pair-tail count <= {MaxTrailingCount}", pairs.ToString());

			for (var i = 0; i < pairs; i++)
				block.PairTail.Add(new SnapshotTailPair(r.ReadU32Be(), r.ReadU32Be()));
		}
	}
}
