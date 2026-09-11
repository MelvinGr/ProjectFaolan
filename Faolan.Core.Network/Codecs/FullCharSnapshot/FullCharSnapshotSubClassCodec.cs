using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The FullChar snapshot's sub-class - the tails behind the protobuf root:
	///     <c>[Quest_c journal][completed-quest journal][quest index][PFAccessWindow_ catalog][stat block]</c>.
	/// </summary>
	/// <remarks>
	///     The journal, the completed journal and the index are typed regions (Codecs/Quest); the catalog and the stat
	///     block have their own codecs here. Every region is read sequentially: the quest index is self-delimiting, so the
	///     catalog's frame word is simply the next thing in the stream, and the stat block's two empty list frames are the
	///     next thing after the catalog. Nothing is carried verbatim.
	/// </remarks>
	public static class FullCharSnapshotSubClassCodec
	{
		// Canonical: the only method here that writes bytes.
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
			// The @609 per-goal objective region is not a separate top-level region: it is interleaved per quest and
			// QuestcRegionCodec already consumes each record's own (QuestcRecord.Objectives), so the boundary past the journal is
			// the journal's own end.
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
