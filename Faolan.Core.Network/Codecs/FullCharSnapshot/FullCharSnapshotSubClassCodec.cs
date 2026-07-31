using System;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot's SUB-CLASS - the tails behind the protobuf root:
	//
	//   [Quest_c journal][completed-quest journal][quest index][opaque gap][PFAccessWindow_ catalog][stat block]
	//
	// The journal, the completed journal and the index are typed regions (Codecs/Quest); the catalog and the stat
	// block have their own codecs here. Two regions are NOT decoded and are carried verbatim so the tails still
	// re-encode byte-exact: the gap between the index and the catalog (UnknownBeforeAccessWindowTable, opaque in
	// every capture we have - tools/capture/decode_snapshot_quests.py owns it), and, for tails whose stat block does
	// not open with the two empty list frames, the whole post-catalog remainder.
	//
	// ⚠️ FINDING THE CATALOG IS A LANDMARK SEARCH, and deliberately so. Between the index and the catalog sit bytes
	// with no known grammar, so there is nothing to read SEQUENTIALLY across them; the catalog is located by its
	// first `PFAccessWindow_` name string and the fixed 15-byte head that precedes it. Everything either side of
	// that gap IS read sequentially, and Parse still accounts for every byte - the gap is an explicitly carried
	// opaque region (#530's treatment for an undecoded tail), not a skipped one.
	public static class FullCharSnapshotSubClassCodec
	{
		// [u32 playfieldId][u8 sectionOrTier][u32 kind][u32 unknown][u16 nameLength] - the catalog entry head that
		// precedes the name the search anchors on.
		private const int EntryHeadLength = 15;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(FullCharSnapshotSubClass value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static FullCharSnapshotSubClass Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FullCharSnapshotSubClassCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static FullCharSnapshotSubClass Read(ref SpanReader r)
		{
			// The @609 per-goal objective region is NOT a separate top-level region: it is interleaved per quest and
			// QuestcRegionCodec already consumed each record's own (QuestcRecord.Objectives, #384). So the boundary
			// past the journal is the journal's own end.
			var quests = QuestcRegionCodec.Read(ref r);

			var anchor = IndexOfAscii(r.PeekRemaining(), PfAccessWindowEntryCodec.NamePrefix);

			// No catalog: the completed journal and the index still parse, and everything behind them is the opaque
			// region.
			if (anchor < 0)
			{
				var completedOnly = ReadCompletedJournal(ref r, r.Remaining);
				var indexOnly = ReadQuestIndex(ref r, r.Remaining);

				return new FullCharSnapshotSubClass
				{
					Quests = quests,
					CompletedQuests = completedOnly,
					QuestIndex = indexOnly,
					UnknownBeforeAccessWindowTable = r.ReadRemaining().ToArray(),
					AccessWindowEntries = [],
					UnknownAfterAccessWindowTable = [],
					PostAccessBlock = null
				};
			}

			// `anchor` is the name's index in the bytes AHEAD of the journal; the entry it belongs to starts 15
			// bytes before it. A catalog that would start inside the journal is clamped away (the journal wins).
			var toTable = Math.Max(anchor - EntryHeadLength, 0);

			var before = r.Position;
			var completed = ReadCompletedJournal(ref r, toTable);
			toTable -= r.Position - before;

			before = r.Position;
			var index = ReadQuestIndex(ref r, toTable);
			toTable -= r.Position - before;

			var gap = r.ReadBytes(toTable).ToArray();

			var entries = PfAccessWindowEntryCodec.ReadTable(ref r);

			var after = r.ReadRemaining().ToArray();
			SubClassPostAccessBlock? block;
			try
			{
				block = SubClassPostAccessBlockCodec.Parse(after);
			}
			catch (CodecParseException)
			{
				// Not a post-access block (the two leading empty list frames are what says one starts here) - the
				// bytes stay opaque rather than being forced into a model they do not fit.
				block = null;
			}

			return new FullCharSnapshotSubClass
			{
				Quests = quests,
				CompletedQuests = completed,
				QuestIndex = index,
				UnknownBeforeAccessWindowTable = gap,
				AccessWindowEntries = entries,
				UnknownAfterAccessWindowTable = after,
				PostAccessBlock = block
			};
		}

		internal static void Write(FaolanStream s, FullCharSnapshotSubClass v)
		{
			ArgumentNullException.ThrowIfNull(v);

			QuestcRegionCodec.Write(s, v.Quests);

			if (v.CompletedQuests != null)
				CompletedQuestJournalRegionCodec.Write(s, v.CompletedQuests);

			if (v.QuestIndex != null)
				QuestIndexRegionCodec.Write(s, v.QuestIndex);

			s.WriteArray(v.UnknownBeforeAccessWindowTable);
			PfAccessWindowEntryCodec.WriteTable(s, v.AccessWindowEntries);

			if (v.PostAccessBlock != null)
				SubClassPostAccessBlockCodec.Write(s, v.PostAccessBlock);
			else
				s.WriteArray(v.UnknownAfterAccessWindowTable);
		}

		// Where `text` first occurs in `ahead`, or -1. The catalog's anchor, and the only search in this file.
		private static int IndexOfAscii(in ReadOnlySpan<byte> ahead, string text)
		{
			var pattern = Encoding.Latin1.GetBytes(text);

			for (var i = 0; i <= ahead.Length - pattern.Length; i++)
			{
				if (ahead.Slice(i, pattern.Length).SequenceEqual(pattern))
					return i;
			}

			return -1;
		}

		// Parses the COMPLETED-quest journal, which ALWAYS precedes the quest index (#390/#391). Its frame word is
		// (areas + 1) * 0x3F1, so a character with no completed quests still carries the empty frame 0x3F1 - the
		// word that used to be mis-read as the index's "IndexTag". Returns null - leaving the bytes to the opaque
		// region - only when the boundary word is not a ListFrame word at all.
		private static CompletedQuestJournalRegion? ReadCompletedJournal(ref SpanReader r, int limit)
		{
			if (limit < 4 || r.Remaining < 4 || !ListFrame.TryCountOf(r.PeekU32Be(), out _))
				return null;

			// The lead word already says a region STARTS here, so a parse failure means our model is wrong - not
			// that the region is absent. Swallowing it drops the bytes into the opaque blob, which is exactly how
			// the completed-quest journal stayed unmodelled while every snapshot still round-tripped (#390).
			// Slicing at `limit` makes the safety bound the reader's own: running past it underruns and names the
			// offset instead of silently reading into the next region.
			var slice = new SpanReader(r.PeekRemaining()[..limit], nameof(CompletedQuestJournalRegionCodec));
			var region = CompletedQuestJournalRegionCodec.Read(ref slice);
			r.Skip(slice.Position);
			return region;
		}

		// Parses the quest index, which follows the completed-journal frame and opens with its id-list marker
		// (count + 1) * 0x3F1 - NOT a 0x3F1 "IndexTag" (that phantom tag was the empty completed-journal frame,
		// #391). Its trailer runs to `limit` (the access-window table start).
		private static QuestIndexRegion? ReadQuestIndex(ref SpanReader r, int limit)
		{
			if (limit < 4 || r.Remaining < 4 || !ListFrame.TryCountOf(r.PeekU32Be(), out _))
				return null;

			// The marker says an index starts here, so failing to parse it is a MODEL error, not an absent region.
			var slice = new SpanReader(r.PeekRemaining()[..limit], nameof(QuestIndexRegionCodec));
			var region = QuestIndexRegionCodec.Read(ref slice);
			slice.EnsureAtEnd(); // the region consumes to the boundary (its trailer is the remainder)
			r.Skip(limit);
			return region;
		}
	}
}
