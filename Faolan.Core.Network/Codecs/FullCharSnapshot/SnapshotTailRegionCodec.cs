using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot tails BEHIND the access-window table - what FullCharSnapshotSubClass carries as
	// UnknownAfterAccessWindowTable (Gitea #393). Recovered from the client's own reader
	// (`CoreFullPlayerCharacterIIR::ReadSubClass` = FUN_00797440, then FullPlayerCharacterIIR's FUN_0148c210):
	//
	//   List D      FUN_0077d220 / FUN_0077db70   0x3F1-framed, VARIABLE-width records
	//   List E      FUN_0077d5b0 / FUN_0077df00   0x3F1-framed, VARIABLE-width records
	//   List F      FUN_004d5900                  0x3F1-framed, 2 x u32
	//   StatList_t  FUN_0139d450 / FUN_0139d210   0x3F1-framed, [u32 statId][u32 value]   (read TWICE)
	//   SpellBook_t FUN_0077af90                  0x3F1-framed, 3 x u32, CONDITIONAL
	//   trailing    FUN_00797440's tail           [u32 n][ n x ([u8][u64][u64]) ]
	//   trailing    FUN_0148c210                  [u32 m][ m x ([u32][u32]) ]
	//
	// ⚠️ Two long-standing errors in the write-up this corrects, both found by decompiling rather than by staring
	// at bytes:
	//   - `SpellBook_t` was documented as "records of 20 bytes (stride 0x14)". The 0x14 is the IN-MEMORY array
	//     stride; the reader makes THREE 4-byte reads per record, so the WIRE record is 12 bytes - which makes it
	//     the same list KnownSpellList models (#74), not a separate one.
	//   - the final list was documented as `count x [u32,u32]`. There are TWO trailing lists, and the first one's
	//     element is 17 bytes: `FUN_0048f720` reads 8 bytes, and the loop reads one raw byte ahead of the two.
	//
	// Both empty-everywhere lists (D and E) are still LISTS - `ForCount(0)` is 0x3F1, the trap this codebase keeps
	// re-learning - so they are parsed as such rather than asserted to be a constant.
	//
	// All 151 captured subclasses walk to their exact last byte and re-encode byte-exact (SnapshotTailRegionTests).
	//
	// ⚠️ The walk BACKTRACKS, which is why it is not a plain sequential read: a frame word alone does not say whether
	// the list behind it is the 8-byte stat shape or the 12-byte spell shape, and only landing the two trailing
	// lists exactly on the region's end decides it. Each attempt takes a COPY of the reader (SpanReader is a ref
	// struct, so assignment is the save/restore) and the successful branch is the one that leaves it at the end.
	public static class SnapshotTailRegionCodec
	{
		// The reader has six lists, not dozens - the bound is what stops a mis-framed region exploring forever.
		private const int MaxLists = 8;

		// Refuses a wildly out-of-range trailing count before it is used as a length: these two lists are per-
		// character counter tables, so a four-digit count means we are not at the trailing pair after all.
		private const uint MaxTrailingCount = 0x1000;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SnapshotTailRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SnapshotTailRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SnapshotTailRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SnapshotTailRegion Read(ref SpanReader r)
		{
			var start = r.Position;
			var result = new SnapshotTailRegion();
			if (!Walk(ref r, result, start, 0))
				throw CodecParseException.Unexpected(nameof(SnapshotTailRegionCodec), start, "a list walk ending on the two trailing lists", "no framing that reaches the region end");

			return result;
		}

		internal static void Write(FaolanStream s, SnapshotTailRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);

			foreach (var list in v.Lists)
			{
				s.WriteUInt32(ListFrame.ForCount(list.Words.Count / (list.Stride / 4)));
				foreach (var word in list.Words)
					s.WriteUInt32(word);
			}

			s.WriteUInt32((uint)v.WideTail.Count);
			foreach (var entry in v.WideTail)
				s.WriteByte(entry.Flag).WriteUInt64(entry.A).WriteUInt64(entry.B);

			s.WriteUInt32((uint)v.PairTail.Count);
			foreach (var pair in v.PairTail)
				s.WriteUInt32(pair.Id).WriteUInt32(pair.Value);
		}

		// Consumes framed lists until the two trailing lists land EXACTLY on the region's end.
		private static bool Walk(ref SpanReader r, SnapshotTailRegion result, int regionStart, int depth)
		{
			var save = r;
			if (TryTrailingLists(ref r, result))
				return true;

			r = save;
			if (depth >= MaxLists)
				return false;

			foreach (var stride in new[] { 8, 12 })
			{
				r = save;
				if (r.Remaining < 4 || !ListFrame.TryCountOf(r.PeekU32Be(), out var count) || 4 + (long)count * stride > r.Remaining)
					continue;

				r.Skip(4);
				var list = new SnapshotTailList
				{
					Stride = stride,
					Offset = r.Position - regionStart
				};
				for (var w = 0; w < count * stride / 4; w++)
					list.Words.Add(r.ReadU32Be());

				result.Lists.Add(list);
				if (Walk(ref r, result, regionStart, depth + 1))
					return true;

				result.Lists.RemoveAt(result.Lists.Count - 1);
			}

			r = save;
			return false;
		}

		// The two trailing lists, which must together consume the rest of the region exactly - that is the anchor
		// the whole backtracking walk is decided by.
		private static bool TryTrailingLists(ref SpanReader r, SnapshotTailRegion result)
		{
			if (r.Remaining < 4)
				return false;

			var wideWord = r.PeekU32Be();
			if (wideWord > MaxTrailingCount || 4 + (long)wideWord * SnapshotTailWideEntry.ByteCount + 4 > r.Remaining)
				return false;

			var wide = (int)wideWord;
			var probe = r;
			probe.Skip(4);
			var wideEntries = new SnapshotTailWideEntry[wide];
			for (var n = 0; n < wide; n++)
				wideEntries[n] = new SnapshotTailWideEntry(probe.ReadU8(), probe.ReadU64Be(), probe.ReadU64Be());

			var pairWord = probe.PeekU32Be();
			if (pairWord > MaxTrailingCount || 4 + (long)pairWord * 8 != probe.Remaining)
				return false;

			var pairs = (int)pairWord;
			probe.Skip(4);
			var pairEntries = new SnapshotTailPair[pairs];
			for (var n = 0; n < pairs; n++)
				pairEntries[n] = new SnapshotTailPair(probe.ReadU32Be(), probe.ReadU32Be());

			result.WideTail.AddRange(wideEntries);
			result.PairTail.AddRange(pairEntries);
			r = probe;
			return true;
		}
	}
}
