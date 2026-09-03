using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// One @609 OBJECTIVE-DETAIL record - a single quest GOAL as the client's QuestGoal_c reads it. Composed by
	// QuestObjectiveRegionCodec (the snapshot tails) and, one record deep, by QuestFullUpdateCodec: the live
	// full-update's "stat/objective run" is this exact 21-word record, which it used to hand-roll (#530 wave 5).
	//
	// Wire (19 u32, a 6-byte tail, then two u16-framed text records):
	//   [1 lead][k][instance][questId][goalId][n][goalIdRepeat][requiredCount]
	//   [reserved ×7][subId][mapId][markerX][markerY][unknown1]
	//   [u8 hideMapMarker][u8 tailFlag][u32 tailWord]
	//   [u16 len][objective <remoteformat 50304>]  [u16 len][text record <remoteformat 51200>/<localized 51000>]
	//
	// ⭐ The tail is carved the way `QuestGoal_c`'s stream reader (`FUN_00925dd0`) reads it, and it used to be a
	// SHIFTED carve here: `[u32 unknown2][u32 objectiveLen]`. Those eight bytes are `[u8][u8][u32][u16 len]` - the
	// reader takes two bools (`FUN_0048f6d0`, into +0x6c/+0x6d), a u32 (+0x70), then the ordinary u16-framed
	// `ReadString` twice. The old carve round-tripped every captured body only because the u32 is always 0 and no
	// objective is 64 KiB long; the "flag word 0x01000000" was the first bool. #464's register had predicted exactly
	// this shape ("two adjacent BYTES with zero padding either side") from the corpus alone.
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
	// ⭐ And the tail's CONSUMER is the map waypoint code, which names two of its three fields (#464, 2026-08-25).
	// `FUN_00925dd0` lands the 21 words on the object from +0x24 upward in wire order (requiredCount +0x38,
	// solvedTimes +0x3c, ..., subId +0x58, mapId +0x5c, markerX +0x60, markerY +0x64, unknown1 +0x68) and the tail
	// as +0x6c/+0x6d/+0x70. `WaypointQuestGoal` (`FUN_013a26c0`, `GUI\MapWaypointHandler.cpp`) then, for every goal
	// whose solved != required, reads +0x5c as the goal's playfield and:
	//   * if that is the current playfield AND the byte at +0x6c is 0, places the marker at the vector
	//     `((float)+0x60, 0, (float)+0x64)` - markerX/markerY on the ground plane - and hands `(float)+0x68` to the
	//     waypoint as a SEPARATE scalar (`CustomWP_c` slot [10], `FUN_0139f520`), not as a coordinate;
	//   * if the byte at +0x6c is set, places NOTHING for a same-playfield goal - so it is the "no map marker" flag,
	//     which is what its 17 carriers (dungeon objectives with degenerate (1,1) markers) always looked like;
	//   * if the goal is on ANOTHER playfield, asks `FUN_0146a9f0` where that playfield is reachable from here and
	//     marks that instead, whatever the flag says.
	// So `HideMapMarker` is named from the client, and so is the scalar - it is the marker's RADIUS (#464,
	// 2026-08-25). Two independent namings agree, neither of them a reading of the values:
	//
	//   * `DispatchAddWayPoint`'s FIELD LIST, which was already in the repo - docs/re/aoc-client-rpc-methods.json
	//     carries it with the other 400 s2c dispatch methods (`i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel,
	//     i_nToolTip`). What was missing was the LINK, not the name: its reader (`FUN_004c9860`) shows `i_vRadius`
	//     is ONE word - the `i_v` prefix reads as a vec3 and the catalog says to check the size - and that is the
	//     waypoint object's own layout: id [4..5], position [6..8], scalar [10]. The word after the position;
	//   * the `addmapmarker` console command registers four parameters as "Type of marker (destination, pvp,
	//     custom)", "Name of marker", "Position of marker", **"Radius of marker"**, and its handler
	//     (`FUN_00a12680`) calls `FUN_0139f520` - the same function that writes +0x28.
	//
	// ⛔ +0x6d and +0x70 have NO CONSUMER in the image, and that is now a measured result rather than an absence of
	// looking (2026-09-01). +0x6d is the distinctive one - a byte at an ODD offset - so a co-offset scan on
	// `(0x6d, 0x70)` within 80 instructions is specific where the earlier `(0x6c, 0x70)` was not: 53 functions,
	// against 1,238 for the aligned pair. Exactly three of the 53 are this object's:
	//   * `FUN_00925dd0` - the READER, which is where they came from;
	//   * `FUN_00925390` - the constructor, which zeroes +0x6d;
	//   * `FUN_00925800` - the WRITER, the serializer twin of the reader: it emits +0x6d through the same
	//     `FUN_00cba060(buf, size)` byte-swap-and-append every other field goes through.
	// The other 50 are unrelated structs that happen to share the offsets. So the pair is read, stored and
	// re-serialized and nothing else in the client touches it - the same "propagated, no found consumer" class as
	// `QuestJournalArea.TrailerFlag` (which turned out to be a named field with no reader, #929), and a fourth round of offset scanning will not change that.
	//
	// They are stored and 0 on every captured goal. Open work tracked in Gitea #464.
	public static class QuestObjectiveGoalCodec
	{
		// Every goal record opens with this word - it is what tells a goal from the region that follows the list.
		public const uint GoalLead = 1;

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
			var markerRadius = r.ReadU32Be();
			var hideMapMarker = r.ReadU8() != 0;
			var tailFlag = r.ReadU8();
			var tailWord = r.ReadU32Be();

			var objective = r.ReadStringU16Length();

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
				MarkerRadius = markerRadius,
				HideMapMarker = hideMapMarker,
				TailFlag = tailFlag,
				TailWord = tailWord,
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
			s.WriteUInt32(v.MarkerRadius);
			s.WriteByte(v.HideMapMarker ? (byte)1 : (byte)0);
			s.WriteByte(v.TailFlag);
			s.WriteUInt32(v.TailWord);
			s.WriteString(v.Objective); // u16-framed, like the trailing record - see the header
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

		// The goal marker's RADIUS in world units, named by the client (#464) - see the header. Nonzero on 24 of the
		// 158 goals in the corpus (quest 12411 goal 3555 holds 60), stable per goal across every capture, so it is
		// quest data to be stored and not progress. BYTE-RANGED: max 170, 15 distinct round values, and 0 - a plain
		// pin with no extent - on everything else.
		public uint MarkerRadius { get; init; }

		// The byte at QuestGoal_c +0x6c: set, the waypoint code places no map marker for a goal on the current
		// playfield (`FUN_013a26c0`) - its 17 captured carriers are dungeon objectives. Used to be read here as the
		// u32 0x01000000 ("Unknown2"), which was this byte followed by TailFlag and half of TailWord.
		public bool HideMapMarker { get; init; }

		// The byte at +0x6d and the u32 at +0x70 - read by the client, stored, 0 on every captured goal.
		public byte TailFlag { get; init; }
		public uint TailWord { get; init; }

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
