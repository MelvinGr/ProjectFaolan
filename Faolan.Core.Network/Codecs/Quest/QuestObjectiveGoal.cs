using System;

namespace Faolan.Core.Network.Codecs.Quest
{
	public sealed class QuestObjectiveGoal : ICodec<QuestObjectiveGoal>
	{
		private const int ReservedWords = 7;
		public uint GoalId { get; init; }
		public uint MapId { get; init; }
		public uint QuestId { get; init; }
		public uint SubId { get; init; } // a secondary text id (= the trailing record's id)
		public byte[] Objective { get; init; } = []; // <remoteformat category=50304> ascii (u32-length-prefixed)
		public byte[] TextRecord { get; init; } = []; // <remoteformat 51200>/<localized 51000> ascii (00 + u8 length)
		public uint GoalIdRepeat { get; init; }
		public uint Instance { get; init; } // the live player's client instance (re-addressed)
		public uint K { get; init; } // goal separator second word (9 / 12 in the template)
		public uint Lead { get; init; } // [1]
		public uint MarkerX { get; init; }
		public uint MarkerY { get; init; }
		public uint N { get; init; }
		public uint RequiredCount { get; init; } // the goal's required amount (kills/collects/etc.); 1/3/4/8/12/64 seen on the wire (decode 2026-06-19, #47)
		public uint Zero1 { get; init; }
		public uint Zero2 { get; init; }
		public uint[] Reserved { get; init; } = new uint[ReservedWords]; // 7 words, all 0 in the template

		public static QuestObjectiveGoal Read(FaolanStream r)
		{
			var lead = r.ReadUInt32();
			var k = r.ReadUInt32();
			var instance = r.ReadUInt32();
			var questId = r.ReadUInt32();
			var goalId = r.ReadUInt32();
			var n = r.ReadUInt32();
			var goalIdRepeat = r.ReadUInt32();
			var requiredCount = r.ReadUInt32();
			var reserved = new uint[ReservedWords];
			for (var i = 0; i < ReservedWords; i++)
				reserved[i] = r.ReadUInt32();
			var subId = r.ReadUInt32();
			var mapId = r.ReadUInt32();
			var markerX = r.ReadUInt32();
			var markerY = r.ReadUInt32();
			var zero1 = r.ReadUInt32();
			var zero2 = r.ReadUInt32();

			var objective = r.ReadArrayUInt32Length();
			var leadByte = r.ReadByte();
			if (leadByte != 0)
				throw new InvalidOperationException($"@609 goal: text-record lead 0x{leadByte:X} != 0");

			var text = r.ReadArrayByteLength();

			return new QuestObjectiveGoal
			{
				Lead = lead,
				K = k,
				Instance = instance,
				QuestId = questId,
				GoalId = goalId,
				N = n,
				GoalIdRepeat = goalIdRepeat,
				RequiredCount = requiredCount,
				Reserved = reserved,
				SubId = subId,
				MapId = mapId,
				MarkerX = markerX,
				MarkerY = markerY,
				Zero1 = zero1,
				Zero2 = zero2,
				Objective = objective,
				TextRecord = text
			};
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(Lead);
			w.WriteUInt32(K);
			w.WriteUInt32(Instance);
			w.WriteUInt32(QuestId);
			w.WriteUInt32(GoalId);
			w.WriteUInt32(N);
			w.WriteUInt32(GoalIdRepeat);
			w.WriteUInt32(RequiredCount);
			foreach (var word in Reserved)
				w.WriteUInt32(word);
			w.WriteUInt32(SubId);
			w.WriteUInt32(MapId);
			w.WriteUInt32(MarkerX);
			w.WriteUInt32(MarkerY);
			w.WriteUInt32(Zero1);
			w.WriteUInt32(Zero2);
			w.WriteArrayUInt32Length(Objective);
			w.WriteByte(0);
			w.WriteArrayByteLength(TextRecord);
		}

		// Builds one goal record from its MEANINGFUL fields, synthesizing the constants (Lead/One = 1, GoalIdRepeat =
		// GoalId, the 7 reserved words + Zero1/Zero2 = 0). So a compact per-goal definition (goalId, n, k, subId, map,
		// marker + the objective/trailing text records) regenerates the full @609 record - the construct-don't-replay
		// generator for the objective region (#186), mirroring QuestcRecord.BuildSingle for the journal region. The
		// objective/trailing text come from the quest's per-goal text refs (the same RemoteFormat/Localized records the
		// journal uses); `instance` is re-addressed to the live player like the rest of the tails.
		// extra5/extra6 are the last two of the 7 "reserved" words: 0 for the first goal, but the second goal carries
		// a number + a 4-char tag there (70099 / 0x36314C56 = "61LV"; still undecoded - carried per goal like
		// QuestcRecord's undecoded scalars). The first five reserved words are always 0.
		public static QuestObjectiveGoal BuildSingle(uint instance, uint questId, uint goalId, uint n, uint k, uint subId, uint mapId, uint markerX, uint markerY, byte[] objective, byte[] textRecord,
			uint extra5 = 0, uint extra6 = 0, uint requiredCount = 1)
		{
			ArgumentNullException.ThrowIfNull(objective);
			ArgumentNullException.ThrowIfNull(textRecord);
			return new QuestObjectiveGoal
			{
				Lead = 1,
				K = k,
				Instance = instance,
				QuestId = questId,
				GoalId = goalId,
				N = n,
				GoalIdRepeat = goalId,
				RequiredCount = requiredCount,
				Reserved = [0, 0, 0, 0, 0, extra5, extra6],
				SubId = subId,
				MapId = mapId,
				MarkerX = markerX,
				MarkerY = markerY,
				Zero1 = 0,
				Zero2 = 0,
				Objective = objective,
				TextRecord = textRecord
			};
		}
	}
}
