using System;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factories for the @609 objective region - the construct-don't-replay generator (#186) that turns a quest's
	// per-goal DB rows into the records QuestObjectiveRegionCodec encodes. Not a codec: nothing here reads or writes
	// bytes, and the structural words a goal record carries (the [1] lead, the goal-id echo, the reserved pairs) are
	// synthesized here rather than being 20 positional parameters on the codec's surface.
	public static class QuestObjectiveRegions
	{
		// The region for one quest's goals, addressed to the live player's client instance (the records carry raw
		// entity references, re-addressed like the rest of the tails).
		//
		// PARTIAL goals are left OUT, and this region is gated on reproducing the capture EXACTLY
		// (ObjectiveRegionVerified; a region the client cannot parse asserts it on statlist.cpp:134), so skipping
		// them keeps every verified region byte-identical - the region stays exactly what it was before those rows
		// were harvested.
		//
		// ⚠️ The REASON is no longer "a mid-session harvest recovers none of the numeric fields a record is built
		// from" (#548): it recovers the kind tag, the count, the reserved pairs and the per-goal pin. What has never
		// been verified is that a region rebuilt to INCLUDE them reproduces the wire, and that is the bar here - so
		// widening this is a byte-exact regeneration job, not a comment change.
		public static QuestObjectiveRegion ForQuest(QuestDefinition def, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);

			return new QuestObjectiveRegion
			{
				Goals = [.. def.Goals.Where(g => !g.Partial).OrderBy(g => g.RegionOrder).ThenBy(g => g.Sequence).Select(g => ForGoal(def, g, instance))]
			};
		}

		// One goal's record. The MEANINGFUL fields come from the row; the structural ones are synthesized (Lead = 1)
		// or echoed, and the undecoded scalars are carried per goal from the capture.
		public static QuestObjectiveGoal ForGoal(QuestDefinition def, QuestGoalDefinition g, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);
			ArgumentNullException.ThrowIfNull(g);

			return new QuestObjectiveGoal
			{
				Lead = QuestObjectiveGoalCodec.GoalLead,
				// A goal with no captured kind cannot be built into a region the client will accept - but that is
				// gated upstream by ObjectiveRegionVerified, which only ever flips on for goals harvested from the
				// wire. Falling back to 0 here keeps the builder total; it never reaches a live client.
				K = g.Separator ?? 0,
				Instance = instance,
				QuestId = def.QuestId,
				GoalId = g.GoalId,
				N = g.Sequence,
				GoalIdRepeat = g.GoalIdEcho,
				RequiredCount = g.RequiredCount == 0
					? 1
					: g.RequiredCount,
				Reserved = [g.Extra0, g.Extra1, g.Extra2, g.Extra3, g.Extra4, g.Extra5, g.Extra6],
				SubId = g.SubId,
				MapId = g.MapId ?? 0,
				MarkerX = g.MarkerX ?? 0,
				MarkerY = g.MarkerY ?? 0,
				Unknown1 = g.Unknown1,
				Unknown2 = g.Unknown2,
				Objective = ObjectiveToken(g),
				TextRecord = TrailingToken(g)
			};
		}

		// A text record addresses its string either by numeric id or by a NAME token; both spellings occur per slot.
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
