using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factory for the live QuestFullUpdate: turns a QuestDefinition into the QuestcRecord the message carries.
	///     Not a codec.
	/// </summary>
	/// <remarks>
	///     The record it builds is the live-grant shape, the one the Casilda golden pins. It differs from the snapshot
	///     journal's shape (QuestcRecords.ForQuest) only in the scalars it fills - FieldEc 1, BandLevel 3, a 0x50 quest-line
	///     reward, a 0x64 XP reward, no cash and a single synthesized goal record, against the per-quest Snapshot* columns -
	///     because both write the same record.
	/// </remarks>
	public static class QuestFullUpdates
	{
		/// <summary>
		///     The <c>n</c> slot (QuestcRecord.QuestLineFlags) the live full-update carries. It is neither a
		///     <c>ListFrame</c> word - 1042 is not a multiple of the frame unit - nor a goal count; see
		///     QuestcRecord.QuestLineFlags.
		/// </summary>
		public const uint LiveQuestLineFlags = 0x412;

		/// <summary>
		///     The quest a full-update record is about, without decoding the record around it.
		/// </summary>
		/// <remarks>
		///     A named read of the record's own key word - the shape PlayfieldTokenScan and BehaviorTailScan also have - for
		///     a capture sweep that only wants to know which quest a grant signal names. Use
		///     <see cref="QuestFullUpdateCodec" />.Parse when the caller wants the journal content: gating a grant signal on
		///     the whole Quest_c record decoding would drop the signal for any record shape the grammar cannot yet account
		///     for, which is a harvesting loss rather than a decode result.
		///     <para>
		///         It reads sequentially and stops at the key word, so there are no offsets: the head is
		///         [subop][class][handle][reserved][0x01] and QuestcRecord opens with its listKey, which is the quest id
		///         (<see cref="QuestcRecordCodec" /> asserts the two agree).
		///     </para>
		/// </remarks>
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

		private const uint LiveBandLevel = 3;
		private const uint LiveField28 = 0x50;
		private const uint LiveFieldEc = 1;
		private const uint LiveFieldF0 = 0x64;
		private const uint LiveGoalSeparator = 0x0C;

		/// <summary>
		///     One quest's full journal entry for <paramref name="playerHandle" />.
		/// </summary>
		/// <remarks>
		///     The goal record's reserved words come from the goal row, like every other captured field: split into its two
		///     words a captured value reads (70099, "T7UT"), the (marker, random 4-character tag) pair
		///     QuestObjectiveGoal.Reserved documents at [1..2] and [5..6]. It is not an accept time, and writing
		///     (0, unix-seconds) there is a shape retail never sends.
		///     <para>
		///         The goal's other structural words stay synthesized below: this is the live-grant shape, and reading the
		///         whole row is QuestObjectiveRegions.ForGoal's job on the snapshot path.
		///     </para>
		///     <para>
		///         <paramref name="rewards" /> is the record's four reward lists (QuestRewardLists.ForQuest) - the same lists
		///         the snapshot's journal record carries, since the live full-update is one Quest_c record.
		///     </para>
		/// </remarks>
		public static QuestFullUpdateIIR ForQuest(uint playerHandle, QuestDefinition def, uint level, QuestRewardLists? rewards = null)
		{
			ArgumentNullException.ThrowIfNull(def);

			// Retail's full-update carries the quest's whole goal list, not just the primary: 28 of the 79 captured
			// QuestFullUpdate bodies hold two or more goals, every one of them grant-shaped (all goals at SolvedTimes
			// 0), including the beach starter sent with both its step-1 and step-2 goals on all 20 create captures and
			// one quest granted with 12 at once. The client's reader (FUN_0091df50) reads a count-framed goal list
			// capped at 100. Sequencing is not a reason to hold goals back: the captured grants state later steps up
			// front, with the step ordinal in each goal's own N.
			//
			// The list is built through the same per-row builder the snapshot region uses, behind the same gate the
			// snapshot applies - a goal with no captured kind word would build a K=0 record, and a region the client
			// cannot parse takes the login down - plus a text-completeness clause of its own, since a goal row
			// harvested without its text refs would build `id=0 key=!!!!` records that the kind-word gate cannot see.
			// A quest that fails either keeps the synthesized single-goal grant below, which is the shape the Casilda
			// golden pins and the only one its rows can state.
			if (CanBuildFullGoalList(def))
			{
				// Partial rows ride along when their wire fields are complete, because retail's live list includes them:
				// quest 12369's grant carries goal 7134, a row that is partial because nothing can advance it rather
				// than because the wire lacks it. The snapshot region excludes them, as its verified captures do.
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
				// reserved[0] is the goal's SolvedTimes, and a full update is the live grant, so it is 0 - what all 118
				// captured full-update goal records carry. [1..6] are the three (marker, identity) reference slots and
				// come from the row like every other captured field.
				Reserved = row != null
					? [0, row.Extra1, row.Extra2, row.Extra3, row.Extra4, row.Extra5, row.Extra6]
					: new uint[7],
				SubId = 0,
				// 0 is the wire's "no marker", the convention QuestObjectiveRegions also uses for a goal with a null
				// map. A quest with no map is a catalog quest no capture ever placed.
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

		/// <summary>
		///     Whether the quest's rows can state the full goal list: the snapshot region's own gate - every non-partial goal
		///     carries its wire kind word, or the region is capture-verified - plus a text-completeness clause of this path's
		///     own, since a goal row harvested without its text refs would build <c>id=0 key=!!!!</c> records that the
		///     kind-word gate cannot see. Every captured text carries a category (100, 50200, 51000 or 51200 in the corpus),
		///     so a zero category is the un-harvested default rather than content.
		/// </summary>
		public static bool CanBuildFullGoalList(QuestDefinition def)
		{
			ArgumentNullException.ThrowIfNull(def);
			return (def.ObjectiveRegionVerified || SnapshotTails.IsObjectiveRegionBuildable(def))
			       && def.Goals.Where(g => !g.Partial).All(g => g.ObjectiveCategory != 0 && g.TrailingCategory != 0);
		}

		/// <summary>
		///     One goal of the live grant: the snapshot region's per-row builder, with one live-context override.
		/// </summary>
		/// <remarks>
		///     The trailing record's spelling is per-variant, and the live variant differs from the stored one on exactly one
		///     category. A goal row stores the region variant its definition's F0 tags, since retail ships more than one
		///     spelling of a region (ObjectiveGoalExtractor.CapturedRegions), and the stored category-51200 trailing is the
		///     snapshot variant's <c>remoteformat</c>. Every captured live grant spells 51200 as <c>localized</c> - 19 of 19 -
		///     and the whole live census is category-uniform (51200, 51000 and 100 localized, 50200 remoteformat; 118 goals,
		///     no exceptions). The id, category and key are the same and only the wrapper changes, so the override rebuilds
		///     the record through the localized token builder from the same row fields.
		/// </remarks>
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

		/// <summary>
		///     The live-grant QuestcRecord around a goal list: the scalars the Casilda golden pins, shared by the full-list
		///     and synthesized-single-goal paths above.
		/// </summary>
		private static QuestcRecord LiveRecord(QuestDefinition def, uint level, QuestObjectiveRegion objectives, QuestRewardLists? rewards = null)
		{
			rewards ??= QuestRewardLists.Empty;
			return new QuestcRecord
			{
				QuestId = def.QuestId,
				Level = level,
				BandTolerance = 0,
				BandLevel = LiveBandLevel,
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
