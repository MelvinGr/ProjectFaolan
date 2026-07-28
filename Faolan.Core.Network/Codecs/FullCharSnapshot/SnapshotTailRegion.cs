using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot tails BEHIND the access-window table - what `FullCharSnapshotSubClass` carries as
	// `UnknownAfterAccessWindowTable` (Gitea #393). Recovered from the client's own reader
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
	//     the same list `KnownSpellList` models (#74), not a separate one.
	//   - the final list was documented as `count x [u32,u32]`. There are TWO trailing lists, and the first one's
	//     element is 17 bytes: `FUN_0048f720` reads 8 bytes, and the loop reads one raw byte ahead of the two.
	//
	// Both empty-everywhere lists (D and E) are still LISTS - `ForCount(0)` is 0x3F1, the trap this codebase keeps
	// re-learning - so they are parsed as such rather than asserted to be a constant.
	//
	// All 151 captured subclasses walk to their exact last byte and re-encode byte-exact (SnapshotTailRegionTests).
	public sealed class SnapshotTailRegion
	{
		// The framed lists, in wire order, each carrying its records as raw words. They are held generically because
		// the corpus cannot tell every one of them apart: D and E are empty in all 151 captures, so which of the
		// leading empty frames is which is not observable from the wire. What IS observable - and what the byte-exact
		// re-encode needs - is each list's frame, stride and contents.
		public List<SnapshotTailList> Lists { get; } = [];

		// The base reader's trailing list: [u8][u64][u64] per element.
		public List<SnapshotTailWideEntry> WideTail { get; } = [];

		// FullPlayerCharacterIIR's own trailing pair list. Its ids are RDB template ids (0xF426A = 1000042, the feat
		// id-map), so it reads as a per-character counter table.
		public List<SnapshotTailPair> PairTail { get; } = [];

		// The character's STAT TABLE, and the reason #393's "label the slots" question dissolves: the table is
		// SELF-LABELLING. Every element is [u32 statId][u32 value] and every one of the 22,348 ids across the corpus
		// is a defined CharacterStatId - HealthCurrent, HealthMax, Intelligence, StaminaMax, PvpMaxExp - so a value
		// is found BY ID rather than by counting slots. `SubClassPostAccessBlock` reads it that way now; it used to
		// carry a 150-member enum of `UnknownNN` slot names, three of which named the wrong stat.
		//
		// It is identified by CONTENT, not by position: it is the stride-8 list that is TWO IDENTICAL COPIES of the
		// same run - same ids AND same values in both halves, on all 151 captures. That doubling is what
		// SubClassPostAccessBlock.HasRepeatedStatTable detects, and why patching a vital has to write both halves.
		public SnapshotTailList? StatTable
		{
			get
			{
				foreach (var list in Lists)
					if (list.Stride == 8 && list.Words.Count >= 4 && IsDoubled(list))
						return list;

				return null;
			}
		}

		private static bool IsDoubled(SnapshotTailList list)
		{
			if (list.Words.Count % 4 != 0)
				return false;

			var half = list.Words.Count / 2;
			for (var i = 0; i < half; i++)
				if (list.Words[i] != list.Words[half + i])
					return false;

			return true;
		}

		public static bool TryParse(byte[] region, [NotNullWhen(true)] out SnapshotTailRegion? parsed)
		{
			parsed = null;
			if (region == null)
				return false;

			var result = new SnapshotTailRegion();
			if (!Walk(region, 0, result, 0))
				return false;

			parsed = result;
			return true;
		}

		public byte[] ToArray()
		{
			var stream = new FaolanStream();
			foreach (var list in Lists)
			{
				stream.WriteUInt32(ListFrame.ForCount(list.Words.Count / (list.Stride / 4)));
				foreach (var word in list.Words)
					stream.WriteUInt32(word);
			}

			stream.WriteUInt32((uint)WideTail.Count);
			foreach (var entry in WideTail)
				stream.WriteByte(entry.Flag).WriteUInt64(entry.A).WriteUInt64(entry.B);

			stream.WriteUInt32((uint)PairTail.Count);
			foreach (var pair in PairTail)
				stream.WriteUInt32(pair.Id).WriteUInt32(pair.Value);

			return stream.ToArray();
		}

		// Consumes framed lists until the two trailing lists land EXACTLY on the region's end. The lists are tried in
		// wire order and the walk backtracks, because a frame word alone does not say whether the list that follows
		// is the 8-byte stat shape or the 12-byte spell shape - landing on the exact end is what decides it. Bounded
		// at 8 lists: the reader has six, not dozens.
		private static bool Walk(byte[] region, int i, SnapshotTailRegion result, int depth)
		{
			if (TryTrailingLists(region, i, result))
				return true;
			if (depth >= 8)
				return false;

			foreach (var stride in new[] { 8, 12 })
			{
				if (i + 4 > region.Length || !ListFrame.TryCountOf(region.ReadU32Be(i), out var count) || i + 4 + (long)count * stride > region.Length)
					continue;

				var list = new SnapshotTailList
				{
					Stride = stride,
					Offset = i + 4
				};
				for (var w = 0; w < count * stride / 4; w++)
					list.Words.Add(region.ReadU32Be(i + 4 + 4 * w));

				result.Lists.Add(list);
				if (Walk(region, i + 4 + count * stride, result, depth + 1))
					return true;

				result.Lists.RemoveAt(result.Lists.Count - 1);
			}

			return false;
		}

		private static bool TryTrailingLists(byte[] region, int i, SnapshotTailRegion result)
		{
			if (i + 4 > region.Length)
				return false;

			var wide = region.ReadU32Be(i);
			if (wide > 0x1000)
				return false;

			var j = i + 4 + (int)wide * SnapshotTailWideEntry.ByteCount;
			if (j + 4 > region.Length)
				return false;

			var pairs = region.ReadU32Be(j);
			if (pairs > 0x1000 || j + 4 + (long)pairs * 8 != region.Length)
				return false;

			for (var n = 0; n < wide; n++)
			{
				var at = i + 4 + n * SnapshotTailWideEntry.ByteCount;
				result.WideTail.Add(new SnapshotTailWideEntry(region[at], region.ReadU64Be(at + 1), region.ReadU64Be(at + 9)));
			}

			for (var n = 0; n < pairs; n++)
			{
				var at = j + 4 + n * 8;
				result.PairTail.Add(new SnapshotTailPair(region.ReadU32Be(at), region.ReadU32Be(at + 4)));
			}

			return true;
		}
	}

	// One framed list in the tail region. `Stride` is 8 for the stat/pair shapes and 12 for the spell shape
	// ([u32 id][u32 0xFFFFFFFF][u32 type]); the words are held raw so the re-encode cannot lose anything the
	// labelling does not yet cover.
	public sealed class SnapshotTailList
	{
		public int Stride { get; init; } = 8;

		// Byte offset of this list's FIRST RECORD within the region (i.e. just past its own framing word). Lets a
		// caller line the list up with a flat view of the same bytes - which is what SubClassPostAccessBlock needs
		// to address stats by id without re-deriving where the tables are.
		public int Offset { get; init; }
		public List<uint> Words { get; } = [];

		// The stride-8 shape read as what it is: [u32 id][u32 value].
		public IEnumerable<SnapshotTailPair> Pairs
		{
			get
			{
				if (Stride != 8)
					yield break;

				for (var i = 0; i + 1 < Words.Count; i += 2)
					yield return new SnapshotTailPair(Words[i], Words[i + 1]);
			}
		}
	}

	// One element of the base reader's trailing list: a byte and two 8-byte reads (FUN_0048f720 reads 8).
	public readonly record struct SnapshotTailWideEntry(byte Flag, ulong A, ulong B)
	{
		public const int ByteCount = 17;
	}

	// One element of FullPlayerCharacterIIR's trailing pair list.
	public readonly record struct SnapshotTailPair(uint Id, uint Value);
}
