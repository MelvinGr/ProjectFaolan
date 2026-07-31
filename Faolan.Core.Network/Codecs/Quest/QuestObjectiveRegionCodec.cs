using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The @609 OBJECTIVE-DETAIL region of the 0x737A6DF9 FullChar snapshot tails - the bytes that follow each Quest_c
	// journal record (QuestcRecordCodec). Grammar decoded + validated 2026-06-16 (tools/capture/decode_snapshot_609.py,
	// docs/re/fullplayer-snapshot-reader.md): a per-GOAL objective-detail list, NOT a QuestFullUpdate block (that
	// earlier claim was disproven - 559 B vs 869 B).
	//
	// Wire:  [u32 (goals + 1) * 0x3F1]  then one QuestObjectiveGoal per goal.
	//
	// The lead word is an ordinary ListFrame count, NOT a "ChapterTag": the beach quest's 2 goals make it 0xBD3 and a
	// 1-goal quest (Casilda 13479) makes it 0x7E2. #255.
	public static class QuestObjectiveRegionCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestObjectiveRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestObjectiveRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestObjectiveRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestObjectiveRegion Read(ref SpanReader r)
		{
			// STRICT: the lead word IS the count - read exactly that many goals rather than consuming while the next
			// word happens to be GoalLead. The tolerant peek made this reader accept output the CLIENT rejects: a
			// record whose goal count disagreed with the goals actually written round-tripped here and then desynced
			// the client's cursor, which died at the next StatList_t (assert "(nSize%1009)==0 && nSize>0, inconsistent
			// IdentityList", statlist.cpp:134 = FUN_0139d450). Reader and writer sharing a tolerant model is exactly
			// what hides a wire bug - see the snapshot-prefix and packed-repeated cases.
			var count = r.ReadListCount();
			var goals = new List<QuestObjectiveGoal>(count);
			for (var i = 0; i < count; i++)
			{
				if (r.Remaining < 4 || r.PeekU32Be() != QuestObjectiveGoalCodec.GoalLead)
					throw CodecParseException.Unexpected(nameof(QuestObjectiveRegionCodec), r.Position, $"goal {i} of {count} (lead word {QuestObjectiveGoalCodec.GoalLead})", "a non-goal word");

				goals.Add(QuestObjectiveGoalCodec.Read(ref r));
			}

			return new QuestObjectiveRegion
			{
				Goals = goals
			};
		}

		internal static void Write(FaolanStream s, QuestObjectiveRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(ListFrame.ForCount(v.Goals.Count));
			foreach (var goal in v.Goals)
				QuestObjectiveGoalCodec.Write(s, goal);
		}
	}

	// One quest's @609 objective-detail region.
	public sealed record QuestObjectiveRegion
	{
		public List<QuestObjectiveGoal> Goals { get; init; } = [];
	}
}
