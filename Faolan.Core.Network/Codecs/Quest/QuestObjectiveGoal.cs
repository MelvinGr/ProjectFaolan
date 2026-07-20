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
		// NOT always zero, despite the name it carried until 2026-07-20: nonzero on 24 of the 158 goals in the corpus
		// (quest 12411 goal 3555 holds 60). Stable per goal across every capture, so it is quest data to be stored,
		// not progress. Meaning still unknown - named for its position.
		public uint Unknown1 { get; init; }
		// NOT always zero: nonzero on 318 of the 2850 captured goals. Stable per goal, so stored rather than derived.
		public uint Unknown2 { get; init; }
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

			// The trailing text record is a u16-length-prefixed string, NOT [0x00][u8 len]. Those are the same bytes
			// below 256 - the "lead 0x00" is the length's high byte - and the longest captured record is 119 B, so the
			// corpus could never tell them apart (#388). The CLIENT settles it: BinaryStream::ReadString
			// (FUN_00cbf840 <- FUN_0048f840) reads a u16 in its default mode, and this record is read through that
			// same primitive (FUN_00922c90 = u32 + 3x ReadString). The sibling QuestcRecord already read it as u16.
			var text = r.ReadArrayUInt16Length();

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
				Unknown1 = zero1,
				Unknown2 = zero2,
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
			w.WriteUInt32(Unknown1);
			w.WriteUInt32(Unknown2);
			w.WriteArrayUInt32Length(Objective);
			w.WriteArrayUInt16Length(TextRecord); // u16-framed - see Read (#388)
		}

		// Builds one goal record from its MEANINGFUL fields, synthesizing the constants (Lead/One = 1, GoalIdRepeat =
		// GoalId, the 7 reserved words + Zero1/Zero2 = 0). So a compact per-goal definition (goalId, n, k, subId, map,
		// marker + the objective/trailing text records) regenerates the full @609 record - the construct-don't-replay
		// generator for the objective region (#186), mirroring QuestcRecord.BuildSingle for the journal region. The
		// objective/trailing text come from the quest's per-goal text refs (the same RemoteFormat/Localized records the
		// journal uses); `instance` is re-addressed to the live player like the rest of the tails.
		// The 7 "reserved" words hold TWO (marker, token) pairs - at [1..2] and [5..6] - each a constant 70099 next to
		// a RANDOM 4-character tag (0x36314C56 "61LV", 0x30474454 "0GDT", ...). Not always zero-except-the-last-two,
		// which is what the earlier note here claimed: quest 14615 carries [0,70099,810042964,0,0,70099,1415075403].
		// Carried per goal like QuestcRecord's undecoded scalars; the tags being random is why they must be stored
		// rather than derived (see the WTHD note in questc-region-decode).
		public static QuestObjectiveGoal BuildSingle(uint instance, uint questId, uint goalId, uint n, uint k, uint subId, uint mapId, uint markerX, uint markerY, byte[] objective, byte[] textRecord,
			uint extra5 = 0, uint extra6 = 0, uint requiredCount = 1, uint extra1 = 0, uint extra2 = 0, uint unknown1 = 0,
			uint extra0 = 0, uint extra3 = 0, uint extra4 = 0, uint goalIdEcho = 0, uint unknown2 = 0)
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
				GoalIdRepeat = goalIdEcho,
				RequiredCount = requiredCount,
				Reserved = [extra0, extra1, extra2, extra3, extra4, extra5, extra6],
				SubId = subId,
				MapId = mapId,
				MarkerX = markerX,
				MarkerY = markerY,
				Unknown1 = unknown1,
				Unknown2 = unknown2,
				Objective = objective,
				TextRecord = textRecord
			};
		}
	}
}
