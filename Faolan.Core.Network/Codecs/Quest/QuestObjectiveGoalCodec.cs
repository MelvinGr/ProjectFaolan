using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One @609 objective-detail record - a single quest goal as the client's QuestGoal_c reads it. Composed by
	///     <see cref="QuestObjectiveRegionCodec" /> for the snapshot tails and, one record deep, by
	///     <see cref="QuestFullUpdateCodec" />, whose objective run is this exact 21-word record.
	/// </summary>
	/// <remarks>
	///     Wire: 19 u32, a 6-byte tail, then two u16-framed text records.
	///     <code>
	///     [1 lead][k][instance][questId][goalId][n][goalIdRepeat][requiredCount]
	///     [reserved x7][subId][mapId][markerX][markerY][markerRadius]
	///     [u8 hideMapMarker][u8 tailFlag][u32 tailWord]
	///     [u16 len][objective, remoteformat 50304]  [u16 len][text record, remoteformat 51200 / localized 51000]
	///     </code>
	///     <para>
	///         The tail is carved the way QuestGoal_c's stream reader (FUN_00925dd0) reads it: those eight bytes are
	///         <c>[u8][u8][u32][u16 len]</c> - two bools (FUN_0048f6d0, into +0x6c and +0x6d), a u32 (+0x70), then the
	///         ordinary u16-framed ReadString twice. Carving them as <c>[u32][u32 len]</c> instead round-trips every captured
	///         body, because the u32 is always 0 and no objective is 64 KiB long, while misnaming all three fields.
	///     </para>
	///     <para>
	///         The reader lands the 21 words on the object from +0x24 upward in wire order (requiredCount +0x38, solvedTimes
	///         +0x3c, ..., subId +0x58, mapId +0x5c, markerX +0x60, markerY +0x64, markerRadius +0x68) and the tail as
	///         +0x6c, +0x6d, +0x70. WaypointQuestGoal (FUN_013a26c0, GUI\MapWaypointHandler.cpp) then, for every goal whose
	///         solved count is short of its required count, reads +0x5c as the goal's playfield and: places the marker at
	///         <c>((float)+0x60, 0, (float)+0x64)</c> and hands <c>(float)+0x68</c> to the waypoint as a separate scalar
	///         (CustomWP_c slot [10], FUN_0139f520) when that is the current playfield and the byte at +0x6c is 0; places
	///         nothing for a same-playfield goal when +0x6c is set; and, for a goal on another playfield, asks FUN_0146a9f0
	///         where that playfield is reachable from here and marks that instead, whatever the flag says.
	///     </para>
	///     <para>
	///         That scalar is the marker's radius, named by the client twice over: DispatchAddWayPoint's field list
	///         (docs/re/aoc-client-rpc-methods.json) is <c>i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel,
	///         i_nToolTip</c>, and its reader (FUN_004c9860) shows <c>i_vRadius</c> is one word despite the vector prefix,
	///         matching the waypoint object's layout of id [4..5], position [6..8], scalar [10]; and the
	///         <c>addmapmarker</c> console command registers four parameters - marker type, name, position and radius - with
	///         a handler (FUN_00a12680) that calls the same FUN_0139f520.
	///     </para>
	///     <para>
	///         The 16-word struct at the object offsets +0x38/+0x3C is not this record but the Quest_c journal record's
	///         objlist element, the quest's reward item (see <see cref="QuestRewardItem" />), whose last two words are the
	///         item's level and the reward's quantity. Three things separate them: the objlist is framed by a plain u32
	///         count the client clamps to 100 while this region is ListFrame-framed; an objlist element is 16 words
	///         and carries no text while this record is 21 words plus two text records; and FUN_0091df50 reads the four
	///         objlists as part of the Quest_c record, with this region following it.
	///     </para>
	///     <para>
	///         +0x6d and +0x70 have no consumer in the image, measured rather than assumed: a co-offset scan on the odd
	///         byte pair (0x6d, 0x70) within 80 instructions gives 53 functions, against 1,238 for the aligned pair, and
	///         exactly three of the 53 belong to this object - the reader FUN_00925dd0, the constructor FUN_00925390 which
	///         zeroes +0x6d, and the writer FUN_00925800, the serializer twin that emits +0x6d through the same
	///         byte-swap-and-append every other field goes through. The pair is read, stored and re-serialized and nothing
	///         else touches it; they are 0 on every captured goal. Open work tracked in Gitea #464.
	///     </para>
	/// </remarks>
	public static class QuestObjectiveGoalCodec
	{
		/// <summary>Every goal record opens with this word; it is what tells a goal from the region that follows the list.</summary>
		public const uint GoalLead = 1;

		private const int ReservedWords = 7;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

			// The trailing text record is a u16-length-prefixed string, not [0x00][u8 len]. Those are the same bytes
			// below 256 - the lead 0x00 is the length's high byte - and the longest captured record is 119 bytes, so
			// the corpus cannot tell them apart. The client settles it: BinaryStream::ReadString (FUN_00cbf840, called
			// from FUN_0048f840) reads a u16 in its default mode, and this record goes through that same primitive.
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
			s.WriteString(v.TextRecord); // u16-framed - see Read
		}
	}

	/// <summary>
	///     One @609 goal record. See QuestObjectiveGoals for the semantic factory that synthesizes the structural words.
	/// </summary>
	public sealed record QuestObjectiveGoal
	{
		public uint GoalId { get; init; }
		public uint MapId { get; init; }
		public uint QuestId { get; init; }
		public uint SubId { get; init; } // a secondary text id (= the trailing record's id)

		// The two text records.
		public string Objective { get; init; } = ""; // <remoteformat category=50304>
		public string TextRecord { get; init; } = ""; // <remoteformat 51200>/<localized 51000>
		public uint GoalIdRepeat { get; init; }
		public uint Instance { get; init; } // the live player's client instance (re-addressed)
		public uint K { get; init; } // goal separator second word (9 / 12 in the template)
		public uint Lead { get; init; } = QuestObjectiveGoalCodec.GoalLead; // [1]
		public uint MarkerX { get; init; }
		public uint MarkerY { get; init; }
		public uint N { get; init; }

		public uint RequiredCount { get; init; } // the goal's required amount (kills, collects, ...); 1/3/4/8/12/64 on the wire

		/// <summary>
		///     The goal marker's radius in world units, named by the client. Nonzero on 24 of the 158 goals in the corpus
		///     (quest 12411 goal 3555 holds 60) and stable per goal across every capture, so it is quest data to be stored
		///     rather than progress. Byte-ranged: a maximum of 170 over 15 distinct round values, and 0 - a plain pin with no
		///     extent - on everything else.
		/// </summary>
		public uint MarkerRadius { get; init; }

		/// <summary>
		///     The byte at QuestGoal_c +0x6c. When set, the waypoint code (FUN_013a26c0) places no map marker for a goal on
		///     the current playfield; its 17 captured carriers are dungeon objectives.
		/// </summary>
		public bool HideMapMarker { get; init; }

		/// <summary>The byte at +0x6d and the u32 at +0x70: read by the client, stored, and 0 on every captured goal.</summary>
		public byte TailFlag { get; init; }
		public uint TailWord { get; init; }

		/// <summary>
		///     The seven reserved words: a lead word plus three optional (marker, token) slots - [1..2], [3..4], [5..6] -
		///     each a marker word next to a random 4-character tag (0x36314C56 "61LV", 0x30474454 "0GDT", ...).
		/// </summary>
		/// <remarks>
		///     The lead word [0] is QuestGoal_c::SolvedTimes, the goal's progress counter: how many of
		///     <see cref="RequiredCount" /> the character has done. It is per-character state rather than quest definition
		///     data, which is why it is not on QuestGoalDefinition; QuestObjectiveRegions.ForQuest takes it from the live
		///     character. The client names it: FUN_00925dd0 maps the record onto the object 1:1 from +0x24 upward in wire
		///     order, so RequiredCount is +0x38 and this word +0x3c, and CoreQuestHolder::QuestGoalEvent (FUN_00810070)
		///     calls <c>SetSolvedTimes(goal, goal-&gt;0x38)</c> on e_QuestGoalEvent_GoalFinished and
		///     <c>SetSolvedTimes(goal, goal-&gt;0x3c + delta)</c> on the progress event, then pushes both words to the UI as
		///     the "x of y" readout. SetSolvedTimes (FUN_00926220) writes +0x3c and logs
		///     "QuestGoal_c::SetSolvedTimes() Character: %d QuestGoal: %d. Set SolvedTimes to: %d.". The corpus agrees: over
		///     4742 snapshot goal records the word never exceeds the goal's RequiredCount, lands exactly on it for every goal
		///     whose quest the captured character had finished and short of it where they had not, and all 118 goal records
		///     carried by a live QuestFullUpdate - the quest grant - carry 0.
		///     <para>
		///         The three pairs float; they are not fixed to [1..2] and [5..6], which is only where they sit in most
		///         captures. Measured over the 170 shipped goals, every nonzero word in a marker position is a marker, and the
		///         slots are independent: 63 goals use none, 63 use [1] alone, 30 use [5] alone, 13 use [1] and [5], and goal
		///         11252 uses [1] and [3].
		///     </para>
		///     <para>
		///         The marker is a type tag on the identity beside it rather than a constant. 70099 (118 slots) is the
		///         client's unset-id sentinel - FUN_006dd970, the GameCode/Criteria evaluator, guards every id compare with
		///         <c>!= 0x111d3</c> and logs "user criteria failed" on a match - so the pair says "not an LDB reference" and
		///         the identity is the object's 4-character ACGHash name. 50003 (3 slots: goals 11252, 12555, 12766) is a real
		///         LDB category (nc_trade.tradename, per the client's own descriptor table) and the identity is an LDB refId.
		///     </para>
		///     <para>
		///         The slots refer to the goal's own two objects: [1..2] is its target, the same object TrailingCategory and
		///         TrailingRefId name, and [5..6] is its NPC. That is not visible in one row, so QuestGoalReferenceSlotTests
		///         gates it over every shipped goal: all three 50003 slots hold the goal's own TrailingRefId verbatim, the
		///         token is a bijection with the referenced item or NPC rather than a per-entry random, every NPC goal carries
		///         [5..6], and the only item goals that do are the twelve Separator-20 Deliver goals, exactly the ones that
		///         need a recipient.
		///     </para>
		///     <para>
		///         A token-to-object map is not recoverable from the client, which is why the Deliver recipients are read out
		///         of the objective text. The client never dereferences a token: "ACGHash" occurs exactly once in
		///         AgeOfConan.exe, in FUN_005d7190's own log channel, and that function's three call sites
		///         (ACGPlayfieldGeneratorData_t's reader twice, QuestGoal_c's once) read a u32, check its four bytes are
		///         [0-9A-Z_], store it and compare it against nothing. It is not in the shipped content either: the decrypted
		///         item template for the one pair where both halves are held (item 193400, token "XE7W") does not contain it,
		///         and a raw scan of all 28 GB of RDB for six known tokens in both byte orders returns hits at chance level.
		///         It is an authoring-pipeline identity that rides the wire, so it is carried per goal like QuestcRecord's
		///         undecoded scalars.
		///     </para>
		/// </remarks>
		public uint[] Reserved { get; init; } = new uint[7];
	}
}
