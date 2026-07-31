using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// One @609 OBJECTIVE-DETAIL record - a single quest GOAL as the client's QuestGoal_c reads it. Composed by
	// QuestObjectiveRegionCodec (the snapshot tails) and, one record deep, by QuestFullUpdateCodec: the live
	// full-update's "stat/objective run" is this exact 21-word record, which it used to hand-roll (#530 wave 5).
	//
	// Wire (21 u32 then two text records):
	//   [1 lead][k][instance][questId][goalId][n][goalIdRepeat][requiredCount]
	//   [reserved ×7][subId][mapId][markerX][markerY][unknown1][unknown2]
	//   [u32 len][objective <remoteformat 50304>]  [u16 len][text record <remoteformat 51200>/<localized 51000>]
	public static class QuestObjectiveGoalCodec
	{
		// Every goal record opens with this word - it is what tells a goal from the region that follows the list.
		public const uint GoalLead = 1;

		// The wire word for the Unknown2 flag: the client writes the byte 0x01 followed by three zero bytes.
		public const uint FlagSet = 0x01000000;

		private const int ReservedWords = 7;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestObjectiveGoal value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestObjectiveGoal Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestObjectiveGoalCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestObjectiveGoal Read(ref SpanReader r)
		{
			var lead = r.ReadU32Be();
			var k = r.ReadU32Be();
			var instance = r.ReadU32Be();
			var questId = r.ReadU32Be();
			var goalId = r.ReadU32Be();
			var n = r.ReadU32Be();
			var goalIdRepeat = r.ReadU32Be();
			var requiredCount = r.ReadU32Be();

			var reserved = new uint[ReservedWords];
			for (var i = 0; i < ReservedWords; i++)
				reserved[i] = r.ReadU32Be();

			var subId = r.ReadU32Be();
			var mapId = r.ReadU32Be();
			var markerX = r.ReadU32Be();
			var markerY = r.ReadU32Be();
			var unknown1 = r.ReadU32Be();
			var unknown2 = r.ReadU32Be();

			var objective = r.ReadStringU32Length();

			// The trailing text record is a u16-length-prefixed string, NOT [0x00][u8 len]. Those are the same bytes
			// below 256 - the "lead 0x00" is the length's high byte - and the longest captured record is 119 B, so the
			// corpus could never tell them apart (#388). The CLIENT settles it: BinaryStream::ReadString
			// (FUN_00cbf840 <- FUN_0048f840) reads a u16 in its default mode, and this record is read through that
			// same primitive (FUN_00922c90 = u32 + 3x ReadString). The sibling QuestcRecord already read it as u16.
			var text = r.ReadStringU16Length();

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
				Unknown1 = unknown1,
				Unknown2 = unknown2,
				Objective = objective,
				TextRecord = text
			};
		}

		internal static void Write(FaolanStream s, QuestObjectiveGoal v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.Lead);
			s.WriteUInt32(v.K);
			s.WriteUInt32(v.Instance);
			s.WriteUInt32(v.QuestId);
			s.WriteUInt32(v.GoalId);
			s.WriteUInt32(v.N);
			s.WriteUInt32(v.GoalIdRepeat);
			s.WriteUInt32(v.RequiredCount);
			foreach (var word in v.Reserved)
				s.WriteUInt32(word);
			s.WriteUInt32(v.SubId);
			s.WriteUInt32(v.MapId);
			s.WriteUInt32(v.MarkerX);
			s.WriteUInt32(v.MarkerY);
			s.WriteUInt32(v.Unknown1);
			s.WriteUInt32(v.Unknown2);
			s.WriteStringUInt32Length(v.Objective);
			s.WriteString(v.TextRecord); // u16-framed - see Read (#388)
		}
	}

	// One @609 goal record. See QuestObjectiveGoals for the semantic factory that synthesizes the structural words.
	public sealed record QuestObjectiveGoal
	{
		public uint GoalId { get; init; }
		public uint MapId { get; init; }
		public uint QuestId { get; init; }
		public uint SubId { get; init; } // a secondary text id (= the trailing record's id)

		// The two text records. They were held as byte[] while every producer built a string and called
		// Latin1.GetBytes to hand them over.
		public string Objective { get; init; } = ""; // <remoteformat category=50304>
		public string TextRecord { get; init; } = ""; // <remoteformat 51200>/<localized 51000>
		public uint GoalIdRepeat { get; init; }
		public uint Instance { get; init; } // the live player's client instance (re-addressed)
		public uint K { get; init; } // goal separator second word (9 / 12 in the template)
		public uint Lead { get; init; } = QuestObjectiveGoalCodec.GoalLead; // [1]
		public uint MarkerX { get; init; }
		public uint MarkerY { get; init; }
		public uint N { get; init; }

		public uint RequiredCount { get; init; } // the goal's required amount (kills/collects/etc.); 1/3/4/8/12/64 seen on the wire (decode 2026-06-19, #47)

		// NOT always zero, despite the name it carried until 2026-07-20: nonzero on 24 of the 158 goals in the corpus
		// (quest 12411 goal 3555 holds 60). Stable per goal across every capture, so it is quest data to be stored,
		// not progress.
		//
		// BYTE-RANGED: max 170 over the whole corpus, never above 255, and only 15 distinct values
		// (0,3,10,20,25,40,50,60,80,90,...). The round numbers suggest a percentage or threshold; the client writes a
		// single byte here in QuestGoal_c::WriteToStream. Meaning still unknown - named for its position.
		public uint Unknown1 { get; init; }

		// A BOOLEAN, not a number. It takes exactly two values across all 2850 captured goals: 0 and 0x01000000 -
		// which is the client's own `local_c[0] = 1` byte-swapped to `0x1000000` in QuestGoal_c::WriteToStream, i.e.
		// the flag byte lands FIRST and the remaining three are padding. Set on 318 goals. Meaning still unknown.
		public uint Unknown2 { get; init; }

		// The 7 "reserved" words are a lead word plus THREE optional (marker, token) SLOTS - [1..2], [3..4], [5..6] -
		// each a marker word next to a RANDOM 4-character tag (0x36314C56 "61LV", 0x30474454 "0GDT", ...). Measured
		// over the 170 shipped goals: every nonzero word in a marker position IS a marker, with no exceptions, and the
		// slots are genuinely independent (63 goals use none, 63 use [1] alone, 30 use [5] alone, 13 use [1]+[5], one
		// - goal 11252 - uses [1]+[3]). So the pairs FLOAT; an earlier note here pinned them to [1..2] and [5..6],
		// which is where they happen to sit in most captures.
		//
		// ⚠️ The marker is not one constant either: it is 70099 on 118 slots and 50003 on 3 (goals 11252, 12555,
		// 12766). The lead word [0] is a small number - 0 on 155 goals, then 1/2/4/6 - and is not a count of the
		// occupied slots (goal 3508 carries 4 with one pair; 11252 carries 0 with two).
		//
		// Carried per goal like QuestcRecord's undecoded scalars; the tags being random is why they must be stored
		// rather than derived (see the WTHD note in questc-region-decode). What the pairs MEAN is Gitea #546.
		public uint[] Reserved { get; init; } = new uint[7];
	}
}
