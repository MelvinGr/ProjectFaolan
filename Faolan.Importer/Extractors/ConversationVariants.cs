using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;

namespace Faolan.Importer.Extractors
{
	// Splits an NPC's CONDITION-VARYING screens apart, from the captures alone.
	//
	// The problem it solves (#526): a screen's identity is (NpcName, TypeId, AnswerPath) plus its quest gate, and
	// without a gate every variant of one greeting collapses to a single key. `ConversationExtractor.Merge` then picks
	// between them by PACKET COUNT - so two genuinely different screens are treated as one screen captured better or
	// worse, and the longer wins, silently. That is how Turach's and Laranga's fresh-character greetings were lost:
	// Turach's root is three different answer sets depending on chain stage, and only the last one survived.
	//
	// The gate does not need to be hand-written. `ConversationEntry.QuestContextId` - the quest active at that greeting
	// - is already extracted, and it IS the discriminator: in the necromancer play2 capture Turach appears at contexts
	// 0 / 10320 / 12371 and Laranga at 0 / 10286, one-to-one with their five distinct trees.
	//
	// ⚠️ But gating EVERY entry on its context would be wrong: greet a vendor while some unrelated quest happens to be
	// active and the vendor's greeting becomes conditional on it. So the split is CONFLICT-DRIVEN - a group is only
	// gated when its entries actually disagree about what the screen says. An NPC captured consistently stays
	// unconditional, which is nearly all of them.
	public static class ConversationVariants
	{
		// The LDB references a screen carries, in order: `<remoteformat ... category="C" ... id="I">` in any order of
		// attributes. This is the SIGNATURE - what the screen says and what it offers.
		private static readonly Regex Reference = new(
			"<(?:remoteformat|localized)[^>]*?\\bcategory=\"?(\\d+)\"?[^>]*?\\bid=\"?(\\d+)\"?|" +
			"<(?:remoteformat|localized)[^>]*?\\bid=\"?(\\d+)\"?[^>]*?\\bcategory=\"?(\\d+)\"?",
			RegexOptions.Compiled);

		// Conflicting entries are gated on the quest that was ACTIVE at the greeting. Active is right by construction:
		// QuestContextId is the last QuestFullUpdate before it, and a full-update IS the grant.
		private const QuestState GateState = QuestState.Active;

		// Assigns a quest gate to the entries that need one, and returns the whole set for Merge.
		//
		// `report` receives one line per group that was split, and one per group that conflicts but could NOT be split
		// (its entries disagree while sharing a context - most often a variant gated on something that is not quest
		// state at all, like the [MALE]/[FEMALE] answer pair, #418). Those are left to Merge's existing packet-count
		// tiebreak rather than dropped: the goal is to import the BULK and hand-correct the tail, not to be exhaustive.
		public static List<ConversationEntry> SplitConflicts(IReadOnlyList<ConversationEntry> all, ICollection<string>? report = null)
		{
			var result = new List<ConversationEntry>(all.Count);
			var groups = all.GroupBy(e => (
				Identity: string.IsNullOrEmpty(e.NpcName) ? $"#{e.CapturedNpcHandle}" : e.NpcName,
				e.TypeId, e.AnswerPath, e.TriggerWorldObjectId));

			foreach (var group in groups)
			{
				// An entry that already carries a gate was classified deliberately; leave it exactly as it is.
				var ungated = group.Where(e => e.ConditionQuestId == null).ToList();
				result.AddRange(group.Where(e => e.ConditionQuestId != null));
				if (ungated.Count == 0)
					continue;

				var signatures = ungated.Select(Signature).Distinct().Count();
				if (signatures < 2)
				{
					result.AddRange(ungated); // captured consistently - one unconditional screen, as before
					continue;
				}

				// The entries disagree. Gate each on its own context; context 0 stays the unconditional fallback, which
				// is what a fresh character with no chain progress gets.
				var contexts = ungated.Select(e => e.QuestContextId).Distinct().Count();
				if (contexts < 2)
				{
					report?.Add($"{group.Key.Identity} '{group.Key.AnswerPath}': {signatures} variants share context "
					            + $"{ungated[0].QuestContextId} - cannot split, keeping the richest");
					result.AddRange(ungated);
					continue;
				}

				report?.Add($"{group.Key.Identity} '{group.Key.AnswerPath}': {signatures} variants split across "
				            + $"{contexts} quest contexts ({string.Join(", ", ungated.Select(e => e.QuestContextId).Distinct().OrderBy(c => c))})");

				foreach (var e in ungated)
					result.Add(e.QuestContextId == 0
						? e
						: e with
						{
							ConditionQuestId = e.QuestContextId,
							ConditionQuestState = GateState
						});
			}

			return result;
		}

		// What the screen SAYS, as its ordered LDB references.
		//
		// ⚠️ Deliberately not the packet bytes or their count. Two captures of the same screen differ routinely - a
		// dropped packet, unrelated traffic interleaved into the run - and comparing raw would manufacture a conflict
		// at almost every key. The LDB reference list is stable across those and is exactly the thing that differs when
		// the screen is genuinely a different one: Turach's roots share the same "Aye?" line and differ only in the
		// answers they offer.
		private static string Signature(ConversationEntry entry)
		{
			var refs = new List<string>();
			foreach (var packet in entry.Packets)
			{
				foreach (Match m in Reference.Matches(Encoding.Latin1.GetString(packet)))
					refs.Add(m.Groups[1].Success
						? $"{m.Groups[1].Value}:{m.Groups[2].Value}"
						: $"{m.Groups[4].Value}:{m.Groups[3].Value}");
			}

			return string.Join(",", refs);
		}
	}
}
