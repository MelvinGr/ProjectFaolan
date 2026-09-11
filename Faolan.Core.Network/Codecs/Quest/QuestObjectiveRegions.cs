using System;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factories for the @609 objective region: the generator that turns a quest's per-goal database rows into
	///     the records <see cref="QuestObjectiveRegionCodec" /> encodes. Not a codec - nothing here reads or writes bytes,
	///     and the structural words a goal record carries (the lead, the goal-id echo, the reserved pairs) are synthesized
	///     here rather than being twenty positional parameters on the codec's surface.
	/// </summary>
	public static class QuestObjectiveRegions
	{
		/// <summary>
		///     The region for one quest's goals, addressed to the live player's client instance; the records carry raw entity
		///     references, re-addressed like the rest of the tails.
		/// </summary>
		/// <remarks>
		///     Partial goals are left out. This region is gated on reproducing the capture exactly
		///     (ObjectiveRegionVerified), since a region the client cannot parse asserts on statlist.cpp:134, and skipping
		///     them keeps every verified region byte-identical. A mid-session harvest does recover the kind tag, the count,
		///     the reserved pairs and the per-goal pin, but a region rebuilt to include them has never been shown to
		///     reproduce the wire, and that is the bar here: widening this is a byte-exact regeneration job.
		///     <para>
		///         <paramref name="solvedTimesOf" /> resolves a goal's progress - QuestGoal_c::SolvedTimes, the first reserved
		///         word. That is per-character state and deliberately not on the definition row, so a caller with no character
		///         (the importer's byte-exact verifier replaying one capture, or a golden) passes the reading it is comparing
		///         against, while a caller building a fresh journal passes nothing and gets 0, which is what an unstarted goal
		///         carries.
		///     </para>
		/// </remarks>
		public static QuestObjectiveRegion ForQuest(QuestDefinition def, uint instance, Func<QuestGoalDefinition, uint>? solvedTimesOf = null)
		{
			ArgumentNullException.ThrowIfNull(def);

			return new QuestObjectiveRegion
			{
				Goals =
				[
					.. def.Goals.Where(g => !g.Partial).OrderBy(g => g.RegionOrder).ThenBy(g => g.Sequence)
						.Select(g => ForGoal(def, g, instance, solvedTimesOf?.Invoke(g) ?? 0))
				]
			};
		}

		/// <summary>
		///     One goal's record. The meaningful fields come from the row; the structural ones are synthesized or echoed, and
		///     the undecoded scalars are carried per goal from the capture. <paramref name="solvedTimes" /> is the live
		///     progress; see <see cref="ForQuest" />.
		/// </summary>
		public static QuestObjectiveGoal ForGoal(QuestDefinition def, QuestGoalDefinition g, uint instance, uint solvedTimes = 0)
		{
			ArgumentNullException.ThrowIfNull(def);
			ArgumentNullException.ThrowIfNull(g);

			return new QuestObjectiveGoal
			{
				Lead = QuestObjectiveGoalCodec.GoalLead,
				// A goal with no captured kind cannot be built into a region the client will accept, but that is gated
				// upstream by ObjectiveRegionVerified, which only flips on for goals harvested from the wire. Falling
				// back to 0 here keeps the builder total; it never reaches a live client.
				K = g.Separator ?? 0,
				Instance = instance,
				QuestId = def.QuestId,
				GoalId = g.GoalId,
				N = g.Sequence,
				GoalIdRepeat = g.GoalIdEcho,
				RequiredCount = g.RequiredCount == 0
					? 1
					: g.RequiredCount,
				// reserved[0] is the goal's live SolvedTimes; [1..6] are the three (marker, identity) reference slots,
				// which are definition data.
				Reserved = [solvedTimes, g.Extra1, g.Extra2, g.Extra3, g.Extra4, g.Extra5, g.Extra6],
				SubId = g.SubId,
				MapId = g.MapId ?? 0,
				MarkerX = g.MarkerX ?? 0,
				MarkerY = g.MarkerY ?? 0,
				MarkerRadius = g.MarkerRadius,
				HideMapMarker = g.HideMapMarker,
				Objective = ObjectiveToken(g),
				TextRecord = TrailingToken(g)
			};
		}

		/// <summary>
		///     A text record addresses its string either by numeric id or by a name token; both spellings occur per slot.
		/// </summary>
		private static string ObjectiveToken(QuestGoalDefinition g)
		{
			return g.ObjectiveToken is { Length: > 0 } t
				? QuestTextTokens.RemoteFormatToken(t, g.ObjectiveCategory, g.ObjectiveKey)
				: QuestTextTokens.RemoteFormat(g.ObjectiveRefId, g.ObjectiveCategory, g.ObjectiveKey);
		}

		private static string TrailingToken(QuestGoalDefinition g)
		{
			if (g.TrailingLocalized)
			{
				return g.TrailingToken is { Length: > 0 } lt
					? QuestTextTokens.LocalizedToken(lt, g.TrailingCategory, g.TrailingFlags ?? "", g.TrailingKey)
					: QuestTextTokens.Localized(g.TrailingRefId, g.TrailingCategory, g.TrailingFlags ?? "", g.TrailingKey);
			}

			return g.TrailingToken is { Length: > 0 } rt
				? QuestTextTokens.RemoteFormatToken(rt, g.TrailingCategory, g.TrailingKey)
				: QuestTextTokens.RemoteFormat(g.TrailingRefId, g.TrailingCategory, g.TrailingKey);
		}
	}
}
