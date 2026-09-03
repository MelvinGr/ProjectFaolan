using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot's SUB-CLASS - the tails behind the protobuf root:
	//
	//   [Quest_c journal][completed-quest journal][quest index][PFAccessWindow_ catalog][stat block]
	//
	// The journal, the completed journal and the index are typed regions (Codecs/Quest); the catalog and the stat
	// block have their own codecs here. Every region is read SEQUENTIALLY - the old landmark search for the catalog's
	// first `PFAccessWindow_` name (and the "opaque gap" it implied) is gone with #464's re-carve: the quest index is
	// self-delimiting (no terminal word; that was the catalog's own ListFrame), so the catalog's frame word is simply
	// the next thing in the stream, and the stat block's two empty list frames are the next thing after the catalog.
	// Nothing is carried verbatim: the post-catalog remainder used to keep a byte[] fallback "for tails whose stat
	// block does not open with the two empty list frames", and no captured tail is one.
	public static class FullCharSnapshotSubClassCodec
	{
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
			var completed = CompletedQuestJournalRegionCodec.Read(ref r);
			var index = QuestIndexRegionCodec.Read(ref r);
			var entries = PfAccessWindowEntryCodec.ReadTable(ref r);
			var block = SubClassPostAccessBlockCodec.Read(ref r);

			return new FullCharSnapshotSubClass
			{
				Quests = quests,
				CompletedQuests = completed,
				QuestIndex = index,
				AccessWindowEntries = entries,
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

			PfAccessWindowEntryCodec.WriteTable(s, v.AccessWindowEntries);
			SubClassPostAccessBlockCodec.Write(s, v.PostAccessBlock);
		}
	}
}
