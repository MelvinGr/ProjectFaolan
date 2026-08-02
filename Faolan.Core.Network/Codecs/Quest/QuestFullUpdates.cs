using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for the live QuestFullUpdate: turns a QuestDefinition into the QuestcRecord the message carries.
	// Not a codec.
	//
	// The record it builds is the LIVE-GRANT shape - the one the Casilda golden pins - which differs from the snapshot
	// journal's shape (QuestcRecords.ForQuest) in the scalars it fills: FieldEc 1, Field118 3, QuestLineReward 0x50,
	// XpReward 0x64, CashReward 0 and a single synthesized goal record, versus the per-quest Snapshot* columns. The two
	// were separate hand-rolled encoders until #530 wave 5 showed they write the same record.
	public static class QuestFullUpdates
	{
		// The `n` slot (QuestcRecord.QuestLineFlags) the live full-update carries. NOT a ListFrame word (1042 is not a
		// multiple of 1009) and NOT a goal count - see QuestcRecord.QuestLineFlags.
		public const uint LiveQuestLineFlags = 0x412;

		// The quest a full-update record is ABOUT, without decoding the record around it.
		//
		// A named read of the record's own key word - the `PlayfieldTokenScan` / `BehaviorTailScan` shape - for the
		// capture sweep that only wants to know WHICH quest a grant signal names. `QuestFullUpdateCodec.Parse` is the
		// right call when the caller wants the journal content; gating a grant signal on the whole Quest_c record
		// decoding would drop the signal for any record shape the grammar cannot yet account for, which is a
		// harvesting loss rather than a decode result.
		//
		// Reads sequentially and stops at the key word, so there are no offsets: the head is
		// `[subop][class][handle][reserved][0x01]` and `QuestcRecord` opens with its listKey, which IS the quest id
		// (QuestcRecordCodec asserts the two agree).
		public static bool TryReadQuestId(ReadOnlySpan<byte> record, out uint questId)
		{
			questId = 0;
			try
			{
				var r = new SpanReader(record, nameof(QuestFullUpdates));
				r.ExpectU32Be(QuestFullUpdateCodec.Opcode, "QuestFullUpdateIIR subop");
				r.Skip(sizeof(uint) + sizeof(uint) + sizeof(uint)); // entity class, player handle, reserved word
				r.ExpectU8(0x01, "record marker");
				questId = r.ReadU32Be();
				return true;
			}
			catch (CodecParseException)
			{
				return false;
			}
		}

		private const uint LiveField118 = 3;
		private const uint LiveField28 = 0x50;
		private const uint LiveFieldEc = 1;
		private const uint LiveFieldF0 = 0x64;
		private const uint LiveGoalSeparator = 0x0C;

		// One quest's full journal entry for `playerHandle`.
		//
		// The goal record's reserved words come from the GOAL ROW, like every other captured field (#537). They used to
		// be a `reserved` u64 parameter the live sender filled with `DateTimeOffset.UtcNow`, under the name
		// "acceptTime": split into its two words the captured value is (70099, "T7UT"), i.e. the (marker, random
		// 4-character tag) pair QuestObjectiveGoal.Reserved documents at [1..2] and [5..6] - not a time at all. The
		// clock wrote (0, unix-seconds) into that pair, a shape retail never sends.
		//
		// The goal's OTHER structural words stay synthesized below: this is the live-grant shape, and reading the whole
		// row here is QuestObjectiveRegions.ForGoal's job (the snapshot path), not a rename.
		public static QuestFullUpdateIIR ForQuest(uint playerHandle, QuestDefinition def, uint level)
		{
			ArgumentNullException.ThrowIfNull(def);

			var row = def.Goals.FirstOrDefault(g => g.GoalId == def.GoalId);

			var goal = new QuestObjectiveGoal
			{
				Lead = QuestObjectiveGoalCodec.GoalLead,
				K = LiveGoalSeparator,
				Instance = playerHandle,
				QuestId = def.QuestId,
				GoalId = def.GoalId,
				N = 1,
				GoalIdRepeat = def.GoalId,
				RequiredCount = 1,
				Reserved = row != null
					? [row.Extra0, row.Extra1, row.Extra2, row.Extra3, row.Extra4, row.Extra5, row.Extra6]
					: new uint[7],
				SubId = 0,
				// 0 is the wire's "no marker" - the same convention QuestObjectiveRegions uses for a goal with a
				// null map. A quest with no map is a catalog quest that no capture ever placed.
				MapId = def.MapId ?? 0,
				MarkerX = def.MarkerX,
				MarkerY = def.MarkerY,
				Objective = QuestTextTokens.Record(def, QuestTextSlot.Objective),
				TextRecord = QuestTextTokens.Record(def, QuestTextSlot.ObjectiveLocalized)
			};

			var record = new QuestcRecord
			{
				QuestId = def.QuestId,
				Level = level,
				Field114 = 0,
				Field118 = LiveField118,
				QuestLineFlags = LiveQuestLineFlags,
				Title = QuestTextTokens.Record(def, QuestTextSlot.Title),
				Summary = QuestTextTokens.Record(def, QuestTextSlot.Summary),
				Area = QuestTextTokens.Record(def, QuestTextSlot.QuestLineTitle),
				Region1 = QuestTextTokens.Record(def, QuestTextSlot.RegionName),
				Region2 = QuestTextTokens.Record(def, QuestTextSlot.MapName),
				FieldEc = LiveFieldEc,
				QuestLineId = def.QuestLineId,
				Field2c = def.QuestId,
				Field11c = 0,
				MoneyReward = def.MoneyReward,
				XpReward = LiveField28,
				FieldF0 = LiveFieldF0,
				FieldF8 = 0,
				Objectives = new QuestObjectiveRegion
				{
					Goals = [goal]
				}
			};

			return new QuestFullUpdateIIR(playerHandle, record);
		}
	}
}
