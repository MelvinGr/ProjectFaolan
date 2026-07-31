using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot's POST-ACCESS-WINDOW block - everything after the PFAccessWindow_ catalog:
	//
	//   [u32 List D frame][u32 List E frame][u32 StatList#1 frame][ u32 values... ][ 0-3 trailer bytes ]
	//
	// The three leading words are the region's own framing (two EMPTY lists then StatList#1's count, 0x46F2 for the
	// 17 displayed stats - not a type tag, see SubClassStatBlock.BlockType), and `Values` is the flat u32 view over
	// the rest. The region's real grammar - which of those words are stride-8 [statId][value] tables - comes from
	// SnapshotTailRegionCodec, which walks the same bytes; this codec asks it where the tables sit so that
	// SubClassPostAccessBlock can address a stat BY ID rather than by counting slots (#393).
	//
	// The trailer is what is left when the payload is not a whole number of words. It is 0-3 bytes by construction,
	// and it is carried rather than interpreted.
	public static class SubClassPostAccessBlockCodec
	{
		// The three framing words ahead of the flat value view.
		private const int HeaderWords = 3;

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
			var value = Read(ref r, data);
			r.EnsureAtEnd();
			return value;
		}

		internal static void Write(FaolanStream s, SubClassPostAccessBlock v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.Marker0);
			s.WriteUInt32(v.Marker1);
			s.WriteUInt32(v.BlockType);
			foreach (var value in v.Values)
				s.WriteUInt32(value);

			s.WriteArray(v.Trailer);
		}

		// `region` is the WHOLE block, which the stat-table walk needs: SnapshotTailRegionCodec reads the same bytes
		// from the first framing word, so its list offsets are block-relative and map straight onto `Values`.
		private static SubClassPostAccessBlock Read(ref SpanReader r, in ReadOnlySpan<byte> region)
		{
			// The two leading EMPTY framed lists (List D / List E) are what says a post-access block starts here.
			var marker0 = r.ExpectU32Be(ListFrame.Empty, "post-access List D frame");
			var marker1 = r.ExpectU32Be(ListFrame.Empty, "post-access List E frame");
			var blockType = r.ReadU32Be();

			var valueCount = r.Remaining / 4;
			var values = new List<uint>(valueCount);
			for (var i = 0; i < valueCount; i++)
				values.Add(r.ReadU32Be());

			var trailer = r.ReadRemaining().ToArray();

			return new SubClassPostAccessBlock
			{
				Marker0 = marker0,
				Marker1 = marker1,
				BlockType = blockType,
				Values = values,
				StatTables = StatTablesOf(region, values.Count),
				HasRepeatedStatTable = HasAdjacentRepeatedRun(values),
				Trailer = trailer
			};
		}

		// True when the values carry an adjacent repeated run of at least 8 words - the signature of the post-access
		// stat table's doubled region (base + current copy of the same 74 pairs).
		private static bool HasAdjacentRepeatedRun(List<uint> values)
		{
			for (var startIndex = 0; startIndex < values.Count; startIndex++)
			{
				for (var length = (values.Count - startIndex) / 2; length >= 8; length--)
				{
					if (RangesEqual(values, startIndex, startIndex + length, length))
						return true;
				}
			}

			return false;
		}

		private static bool RangesEqual(List<uint> values, int leftStart, int rightStart, int length)
		{
			for (var i = 0; i < length; i++)
			{
				if (values[leftStart + i] != values[rightStart + i])
					return false;
			}

			return true;
		}

		// Where the [statId][value] tables sit inside the flat `Values` view, as (start, wordCount) pairs. Derived
		// from the region walk rather than from fixed indexes - scanning the flat array instead is not a smaller
		// version of this, it is wrong: HealthMax's id is 1, and the word 1 appears all over the array as ordinary
		// data. A region that does not walk yields no tables, and a lookup by id then finds nothing rather than
		// finding the wrong thing.
		private static List<(int Start, int Words)> StatTablesOf(in ReadOnlySpan<byte> region, int valueCount)
		{
			var tables = new List<(int Start, int Words)>();

			SnapshotTailRegion walked;
			try
			{
				walked = SnapshotTailRegionCodec.Parse(region);
			}
			catch (CodecParseException)
			{
				return tables; // the region does not walk - see SnapshotTailRegionCodec's header for why that happens
			}

			foreach (var list in walked.Lists)
			{
				if (list.Stride != 8 || list.Words.Count == 0)
					continue;

				var start = (list.Offset - HeaderWords * 4) / 4;
				if (start >= 0 && start + list.Words.Count <= valueCount)
					tables.Add((start, list.Words.Count));
			}

			return tables;
		}
	}
}
