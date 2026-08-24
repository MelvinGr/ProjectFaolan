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
	//
	// ⛔ **CORRECTED (#464).** This block used to say: *"the LIVE reader is pinned - `QuestFullUpdateIIR::ReadSubClass`
	// (FUN_00917e20) allocates a `Quest_c` and `FUN_0091df50` reads the goal list - per goal the record's last 16 u32
	// into an 18-word struct (n->+0x00, goalIdRepeat->+0x04, requiredCount->+0x18, r0->+0x1C, r1..r4->+0x08..+0x14,
	// r5->+0x30, r6->+0x34, subId/mapId/markerX/markerY->+0x20..+0x2C, unknown1->+0x38, unknown2->+0x3C)"*, and drew
	// two conclusions from it: that `reserved[5]`/`[6]` are the goal list's SORT KEYS, and that `unknown1`/`unknown2`
	// have object offsets `+0x38`/`+0x3C` for a consumer hunt.
	//
	// **That 16-word struct is not this record.** It is the `Quest_c` journal record's OBJLIST element - the quest's
	// reward item, now decoded as `QuestRewardItem`, whose last two words are the item's level and the reward's
	// quantity. Three things separate the two, none of them subtle:
	//
	//   * the objlist is framed by a PLAIN u32 count the client clamps to 100; this region is ListFramed as
	//     `(goals + 1) * 0x3F1`;
	//   * an objlist element is 16 words and carries no text; this record is 21 words PLUS two text records;
	//   * `FUN_0091df50` reads the four objlists as part of the `Quest_c` record, and this region follows it.
	//
	// So four rounds of offset scans over `+0x38`/`+0x3C` (single-offset, `+0x24`/`+0x38`, `+0x10c`/`+0x38`,
	// co-occurrence with NEAR) were hunting a consumer for the wrong struct's fields. Do not restart them on those
	// offsets.
	//
	// ⭐ What IS pinned, and is the honest starting point for the next attempt:
	//   * `Quest_c` holds FOUR POD vectors (+0x100/+0x104/+0x10C/+0x110), one per objlist, and TWO vectors of
	//     refcounted `QuestGoal_c*` at +0xAC..+0xB0 and +0xBC..+0xC0.
	//   * The `QuestGoal_c` objects EXIST BEFORE the record is read - `FUN_0091df50` only post-processes them
	//     (`QuestGoal_c::SetParent` = `FUN_009260c0`, then a `std::sort` with comparator `FUN_0091ce70`) - so they
	//     come from the client's own quest template, and it is THEY that the sort orders, not anything from this
	//     region.
	//   * The waypoint UI reads those objects too, never the wire PODs: `WaypointQuestGoal` (`FUN_013a26c0`,
	//     `GUI\MapWaypointHandler.cpp`, keyed `m_cWaypoints.find(goalID)`) walks +0xAC..+0xB0 and reads +0x38/+0x3C
	//     (required vs solved, the pair `CoreQuestHolder::QuestGoalEvent` names), +0x5c playfield, +0x60/+0x64/+0x68
	//     position, +0x6c flag, +0x20 parent, +0x80 name.
	//
	// The DATA leads for this record's two unknowns are unchanged and remain the best handle: `unknown1 > 0` implies
	// a marker on 24 of 24 shipped goals, with round map-unit values 3..170 on ReachArea/hunt-area goals - a marker
	// RADIUS shape; `unknown2`'s 17 carriers are all dungeon-objective goals, several with degenerate (1,1) markers -
	// a marker-display-modifier shape. Open work tracked in Gitea #464.
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
		// ⚠️ The marker is not one constant either, and it is a TYPE TAG on the identity beside it (#546):
		//   * 70099 (118 slots) is the client's "unset id" SENTINEL - `FUN_006dd970`, the `GameCode/Criteria`
		//     evaluator, guards every id compare with `!= 0x111d3` and logs "user criteria failed" on a match. So the
		//     pair says "not an LDB reference", and the identity is the object's 4-character ACGHash name.
		//   * 50003 (3 slots: goals 11252, 12555, 12766) is a real LDB category (`nc_trade.tradename`, per the
		//     client's own descriptor table) and the identity is an LDB refId.
		//
		// The slots REFER to the goal's own two objects: [1..2] is its TARGET - the same object `TrailingCategory` /
		// `TrailingRefId` names - and [5..6] is its NPC. That is not visible in one row, so it is gated by
		// `QuestGoalReferenceSlotTests` over every shipped goal: all three 50003 slots hold the goal's own
		// `TrailingRefId` verbatim (the Rosetta stone), the token is a bijection with the referenced item / NPC rather
		// than a per-entry random, every NPC goal carries [5..6], and the only ITEM goals that do are the twelve
		// `Separator` 20 Deliver goals - i.e. exactly the ones that need a recipient.
		//
		// ✅ The lead word [0] is `QuestGoal_c::SolvedTimes` - the goal's PROGRESS counter, i.e. how many of
		// `RequiredCount` the character has done (#550). It is per-CHARACTER state, not quest definition data, which is
		// why it is not on `QuestGoalDefinition`: `QuestObjectiveRegions.ForQuest` takes it from the live character.
		//
		// The client settles it. `FUN_00925dd0` is QuestGoal_c's stream reader and maps the record onto the object
		// 1:1 - the 21 words land at `+0x24` upward in wire order, so `RequiredCount` (w7) is `+0x38` and this word
		// (w8) is `+0x3c`. `CoreQuestHolder::QuestGoalEvent` (`FUN_00810070`) then names both: on
		// `e_QuestGoalEvent_GoalFinished` it calls `SetSolvedTimes(goal, goal->0x38)` - progress jumps to the target -
		// and on the progress event `SetSolvedTimes(goal, goal->0x3c + delta)`, after which it pushes BOTH words to
		// the UI as the "x of y" readout. `SetSolvedTimes` (`FUN_00926220`) writes `+0x3c` and logs
		// "QuestGoal_c::SetSolvedTimes() Character: %d QuestGoal: %d. Set SolvedTimes to: %d.".
		//
		// The corpus agrees on every reading: over 4742 snapshot goal records the word NEVER exceeds the goal's
		// RequiredCount, it lands exactly ON it for each of the goals whose quest the captured character had finished
		// (4 of 4, 2 of 2, 1 of 1 x12) and short of it where they had not (6 of 13, 1 of 5) - and all 118 goal records
		// carried by a live QuestFullUpdate, which is the quest GRANT, carry 0.
		//
		// ⚠️ A token -> object map is NOT RECOVERABLE from the client (#547), so the Deliver recipients are read out
		// of the objective TEXT and that is not a stopgap. The client never dereferences a token: `"ACGHash"` occurs
		// exactly ONCE in AgeOfConan.exe - in FUN_005d7190's own log channel - and that function has three call sites
		// (ACGPlayfieldGeneratorData_t's reader twice, QuestGoal_c's once), reads a u32, checks its four bytes are
		// `[0-9A-Z_]`, stores it, and compares it against nothing. Neither consumer looks it up afterwards. Nor is it
		// in the content we ship with: the decrypted item template for the one pair where we hold both halves (item
		// 193400, token "XE7W") does not contain it, and a raw scan of all 28 GB of RDB for six known tokens in both
		// byte orders returns hits at chance level. It is an authoring-pipeline identity that rides the wire.
		//
		// Carried per goal like QuestcRecord's undecoded scalars - a token is per-object, not derivable from the row.
		public uint[] Reserved { get; init; } = new uint[7];
	}
}
