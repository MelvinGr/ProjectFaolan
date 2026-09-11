using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The snapshot's post-access-window block - everything after the <c>PFAccessWindow_</c> catalog, 1.3-2.7 KB per
	///     subclass and the largest region of the login snapshot.
	/// </summary>
	/// <remarks>
	///     Recovered from the client's own reader (<c>CoreFullPlayerCharacterIIR::ReadSubClass</c> = FUN_00797440, then
	///     FullPlayerCharacterIIR's FUN_0148c210):
	///     <code>
	///     List D      FUN_0077d220 / FUN_0077db70   count-framed, variable-width records  empty in all 151 captures
	///     List E      FUN_0077d5b0 / FUN_0077df00   count-framed, variable-width records  empty in all 151 captures
	///     StatList#1  FUN_004d5900                  count-framed, [u32 statId][u32 value] the 17 displayed stats
	///     StatList#2  FUN_0139d450 / FUN_0139d210   count-framed, [u32 statId][u32 value] 74 sheet pairs, twice
	///     SpellBook_t FUN_0077af90                  count-framed, 3 x u32, conditional    the known-spell list
	///     wide tail   FUN_00797440's tail           [u32 n][ n x ([u8][u64][u64]) ]
	///     pair tail   FUN_0148c210                  [u32 m][ m x ([u32][u32]) ]
	///     </code>
	///     <para>
	///         <c>SpellBook_t</c>'s 0x14 stride is the in-memory array stride; the reader makes three 4-byte reads per
	///         record, so the wire record is 12 bytes, which makes it the same list <see cref="KnownSpellListCodec" />
	///         models. There are two trailing lists, and the first one's element is 17 bytes: <c>FUN_0048f720</c> reads 8
	///         bytes and the loop reads one raw byte ahead of the two.
	///     </para>
	///     <para>
	///         Lists D and E are lists, not constants - an empty list still carries its frame word - so they are read as
	///         such, and they are what says a post-access block starts here. Their record shape is unobservable from the
	///         corpus, every capture having them empty, so nothing beyond the frame is modelled: an empty list writes back
	///         as its frame word.
	///     </para>
	///     <para>
	///         The spellbook list is genuinely optional rather than merely empty: 130 login snapshots carry one (0 to 127
	///         records) and the 21 create-editor snapshots end at StatList#2, going straight to the two zero-count tails.
	///         An absent list and an empty one differ on the wire, so the model distinguishes them.
	///     </para>
	///     <para>
	///         All 151 captured blocks parse to their exact last byte and re-encode byte-exact
	///         (<c>SubClassPostAccessBlockTests</c>).
	///     </para>
	/// </remarks>
	public static class SubClassPostAccessBlockCodec
	{
		// Refuses a wildly out-of-range trailing count before it is used as a length: these two lists are per-character
		// counter tables, so a four-digit count means this is not the trailing pair after all.
		private const uint MaxTrailingCount = 0x1000;

		// Canonical: the only method here that writes bytes.
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
			// The two leading empty framed lists are what says a post-access block starts here.
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

		// Whether a spellbook list follows. The discriminator is the frame word itself: a framed count cannot be mistaken
		// for the wide tail's count, because the smallest one would claim 1009 x 17 bytes - far past the largest captured
		// block - while the tails' own counts are single digits. The `fits` test says exactly that rather than trusting the
		// arithmetic to stay true.
		private static bool HasSpellBook(SpanReader r)
		{
			if (r.Remaining < 4 || !ListFrame.TryCountOf(r.PeekU32Be(), out var records))
				return false;

			return 4 + (long)records * KnownSpellListCodec.RecordSize + 8 <= r.Remaining;
		}

		// The two plainly-counted lists that close the region. Their counts are read as counts rather than peeked at: by
		// here the framed lists are all consumed, so anything that does not land exactly on the region's end is a
		// mis-modelled block, and Parse's EnsureAtEnd says so.
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
