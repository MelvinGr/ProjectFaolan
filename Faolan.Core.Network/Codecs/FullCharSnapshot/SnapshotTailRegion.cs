using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The decoded tail region SnapshotTailRegionCodec reads and writes - see that file for the grammar and its
	// provenance. A plain value: it does not serialize itself (#530 rule 1).
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
					if (list is { Stride: 8, Words.Count: >= 4 } && IsDoubled(list))
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
