using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     The @609 objective-detail region of the FullChar snapshot tails - the bytes that follow each Quest_c journal
	///     record (<see cref="QuestcRecordCodec" />). It is a per-goal objective-detail list: a
	///     <see cref="ListFrame" /> count, then one <see cref="QuestObjectiveGoal" /> per goal.
	/// </summary>
	/// <remarks>
	///     The lead word is an ordinary list count rather than a tag - a single-goal quest and the beach's two-goal quest
	///     carry different words. Grammar decoded and validated with tools/capture/decode_snapshot_609.py; see
	///     docs/re/fullplayer-snapshot-reader.md.
	/// </remarks>
	public static class QuestObjectiveRegionCodec
	{
		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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
			// The lead word is the count: read exactly that many goals rather than consuming while the next word
			// happens to be GoalLead. A tolerant peek accepts output the client rejects - a record whose goal count
			// disagrees with the goals actually written round-trips here and then desyncs the client's cursor, which
			// dies at the next StatList_t ("inconsistent IdentityList", statlist.cpp:134 = FUN_0139d450). Reader and
			// writer sharing a tolerant model is what hides a wire bug.
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

	/// <summary>One quest's @609 objective-detail region.</summary>
	public sealed record QuestObjectiveRegion
	{
		public List<QuestObjectiveGoal> Goals { get; init; } = [];
	}
}
