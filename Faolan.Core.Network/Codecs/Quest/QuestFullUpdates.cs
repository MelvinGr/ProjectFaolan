using System;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for the live QuestFullUpdate: turns a QuestDefinition into the QuestcRecord the message carries.
	// Not a codec.
	//
	// The record it builds is the LIVE-GRANT shape - the one the Casilda golden pins - which differs from the snapshot
	// journal's shape (QuestcRecords.ForQuest) in the scalars it fills: FieldEc 1, Field118 3, Field28 0x50,
	// FieldF0 0x64, FieldF8 0 and a single synthesized goal record, versus the per-quest Snapshot* columns. The two
	// were separate hand-rolled encoders until #530 wave 5 showed they write the same record.
	public static class QuestFullUpdates
	{
		// The `n` slot (QuestcRecord.Unknown14) the live full-update carries. NOT a ListFrame word (1042 is not a
		// multiple of 1009) and NOT a goal count - see QuestcRecord.Unknown14.
		public const uint LiveUnknown14 = 0x412;

		// The u64 the goal record's reserved[5..6] pair carries in the snapshot tails.
		//
		// ⚠️ It is NOT a timestamp, despite the name it has carried since the first decode: split into its two words it
		// is (70099, 0x54375554) - exactly the (constant 70099, random 4-character tag) pair QuestObjectiveGoal.Reserved
		// documents at [1..2] and [5..6]. The live sender passes the current unix time here instead, which lands as
		// (0, seconds); that is what retail's own bytes are compared against by the goldens, so it is left alone here
		// and tracked as a decode/behaviour gap rather than "fixed" inside a refactor.
		public const ulong SnapshotQuestAcceptTime = 0x000111D354375554UL;

		private const uint LiveField118 = 3;
		private const uint LiveField28 = 0x50;
		private const uint LiveFieldEc = 1;
		private const uint LiveFieldF0 = 0x64;
		private const uint LiveGoalSeparator = 0x0C;

		// One quest's full journal entry for `playerHandle`. `reserved` fills the goal record's reserved[5..6] pair
		// (see SnapshotQuestAcceptTime).
		public static QuestFullUpdateIIR ForQuest(uint playerHandle, QuestDefinition def, uint level, ulong reserved)
		{
			ArgumentNullException.ThrowIfNull(def);

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
				Reserved = [0, 0, 0, 0, 0, (uint)(reserved >> 32), (uint)reserved],
				SubId = 0,
				MapId = def.MapId,
				MarkerX = def.MarkerX,
				MarkerY = def.MarkerY,
				Objective = QuestTextTokens.Record(def, QuestTextSlot.Objective),
				TextRecord = QuestTextTokens.Record(def, QuestTextSlot.ObjectiveLocalized)
			};

			var record = new QuestcRecord
			{
				QuestId = def.QuestId,
				State = level,
				Field114 = 0,
				Field118 = LiveField118,
				Unknown14 = LiveUnknown14,
				Title = QuestTextTokens.Record(def, QuestTextSlot.Title),
				Summary = QuestTextTokens.Record(def, QuestTextSlot.Summary),
				Area = QuestTextTokens.Record(def, QuestTextSlot.AreaTitle),
				Region1 = QuestTextTokens.Record(def, QuestTextSlot.RegionName),
				Region2 = QuestTextTokens.Record(def, QuestTextSlot.MapName),
				FieldEc = LiveFieldEc,
				AreaId = def.AreaId,
				Field2c = def.QuestId,
				Field11c = 0,
				Accept = 0,
				Field28 = LiveField28,
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
