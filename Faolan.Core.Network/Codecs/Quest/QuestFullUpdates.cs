using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

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
		// `rewards` is the record's four reward lists (QuestRewardLists.ForQuest, #893) - the same lists the snapshot's
		// journal record carries, since the live full-update IS one Quest_c record.
		public static QuestFullUpdateIIR ForQuest(uint playerHandle, QuestDefinition def, uint level, QuestRewardLists? rewards = null)
		{
			ArgumentNullException.ThrowIfNull(def);

			// ⭐ Retail's full-update carries the quest's WHOLE goal list, not just the primary (#866): 28 of the 79
			// captured QuestFullUpdate bodies hold 2+ goals - every one of them GRANT-shaped (all goals at
			// SolvedTimes 0), including the beach starter sent with both its step-1 and step-2 goals on all 20
			// create captures and one quest granted with 12 at once - and the client's reader (FUN_0091df50, #464)
			// reads a count-framed goal list capped at 100. Sequencing is not a reason to hold goals back: the
			// captured grants state later steps up front, with the step ordinal in each goal's own N.
			//
			// The list is built through the same per-row builder the snapshot region uses, behind the same gate the
			// snapshot applies (a goal with no captured kind word would build a K=0 record, and a region the client
			// cannot parse takes the login down - statlist.cpp:134) PLUS a text-completeness clause of its own: a
			// goal row harvested without its text refs would build `id=0 key=!!!!` records, and the kind-word gate
			// cannot see that. A quest that fails either keeps the synthesized single-goal grant below: the shape
			// the Casilda golden pins, and the only one its rows can state.
			if (CanBuildFullGoalList(def))
			{
				// PARTIAL rows ride along when their wire fields are complete: retail's live list includes them
				// (quest 12369's grant carries goal 7134, a row that is Partial because nothing can ADVANCE it, not
				// because the wire lacks it). The snapshot region keeps excluding them - its verified captures do.
				var region = new QuestObjectiveRegion
				{
					Goals =
					[
						.. def.Goals.Where(g => !g.Partial || g.Separator != null && g.ObjectiveCategory != 0 && g.TrailingCategory != 0)
							.OrderBy(g => g.RegionOrder).ThenBy(g => g.Sequence)
							.Select(g => LiveGoal(def, g, playerHandle))
					]
				};

				return new QuestFullUpdateIIR(playerHandle, LiveRecord(def, level, region, rewards));
			}

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
				// reserved[0] is the goal's SolvedTimes (#550) and a full update is the live GRANT, so it is 0 - which
				// is what all 118 captured full-update goal records carry, without exception. [1..6] are the three
				// (marker, identity) reference slots and come from the row like every other captured field.
				Reserved = row != null
					? [0, row.Extra1, row.Extra2, row.Extra3, row.Extra4, row.Extra5, row.Extra6]
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

			return new QuestFullUpdateIIR(playerHandle, LiveRecord(def, level, rewards: rewards, objectives: new QuestObjectiveRegion
			{
				Goals = [goal]
			}));
		}

		// Whether the quest's rows can state the FULL goal list: the snapshot region's own gate (every non-partial
		// goal carries its wire kind word, or the region is capture-verified) plus a text-completeness clause of
		// this path's own - a goal row harvested without its text refs would build `id=0 key=!!!!` records, and the
		// kind-word gate cannot see that. Every captured text carries a category (100/50200/51000/51200 in the
		// corpus), so a zero category is the un-harvested default, not content.
		public static bool CanBuildFullGoalList(QuestDefinition def)
		{
			ArgumentNullException.ThrowIfNull(def);
			return (def.ObjectiveRegionVerified || SnapshotTails.IsObjectiveRegionBuildable(def))
			       && def.Goals.Where(g => !g.Partial).All(g => g.ObjectiveCategory != 0 && g.TrailingCategory != 0);
		}

		// One goal of the live grant: the snapshot region's per-row builder, with ONE live-context override.
		//
		// ⚠️ The trailing record's SPELLING is per-variant, and the LIVE variant differs from the stored one on
		// exactly one category. A goal row stores the region variant its def's F0 tags (retail ships more than one
		// spelling of a region - ObjectiveGoalExtractor.CapturedRegions), and the stored category-51200 trailing is
		// the snapshot variant's `<remoteformat>`; every captured LIVE grant spells 51200 `<localized>` - 19 of 19,
		// and the whole live census is category-uniform (51200/51000/100 localized, 50200 remoteformat; 118 goals,
		// zero exceptions). Same id, category and key - only the wrapper changes, so the override rebuilds the
		// record through the localized token builder from the same row fields.
		private static QuestObjectiveGoal LiveGoal(QuestDefinition def, QuestGoalDefinition g, uint playerHandle)
		{
			var goal = QuestObjectiveRegions.ForGoal(def, g, playerHandle);
			if (g.TrailingLocalized || g.TrailingCategory != 51200)
				return goal;

			return goal with
			{
				TextRecord = g.TrailingToken is { Length: > 0 } token
					? QuestTextTokens.LocalizedToken(token, g.TrailingCategory, g.TrailingFlags ?? "", g.TrailingKey)
					: QuestTextTokens.Localized(g.TrailingRefId, g.TrailingCategory, g.TrailingFlags ?? "", g.TrailingKey)
			};
		}

		// The live-grant QuestcRecord around a goal list - the scalars the Casilda golden pins, shared by the
		// full-list and the synthesized-single-goal paths above.
		private static QuestcRecord LiveRecord(QuestDefinition def, uint level, QuestObjectiveRegion objectives, QuestRewardLists? rewards = null)
		{
			rewards ??= QuestRewardLists.Empty;
			return new QuestcRecord
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
				Obj1 = rewards.Obj1,
				Obj2 = rewards.Obj2,
				Obj3 = rewards.Obj3,
				Obj4 = rewards.Obj4,
				Objectives = objectives
			};
		}
	}
}
