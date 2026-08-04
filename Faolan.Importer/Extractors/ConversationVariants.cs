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
	// The gate does not need to be hand-written, and since #639 none of it is: the captured QUEST STATE at the greeting
	// (`ConversationEntry.QuestStates` - every quest the capture has seen a full-update or a state flip for, in its
	// latest state) is the discriminator. It replaced a five-row table keyed on capture FILE NAMES, which silently
	// dropped any Casilda screen from a capture nobody had listed.
	//
	// ⚠️ But gating EVERY entry on its state would be wrong: greet a vendor while some unrelated quest happens to be
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

		// Assigns a quest gate to the entries that need one, and returns the whole set for Merge.
		//
		// `report` receives one line per group that was split, and one per group that conflicts but could NOT be split
		// (its variants share the same captured quest state - most often a variant gated on something that is not quest
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
				// An entry that already carries a gate was classified deliberately (the server-opened conversations,
				// which ConversationExtractor gates on the quest beat they belong to); leave it exactly as it is.
				var ungated = group.Where(e => e.ConditionQuestId == null).ToList();
				result.AddRange(group.Where(e => e.ConditionQuestId != null));
				if (ungated.Count == 0)
					continue;

				// One screen, captured consistently - one unconditional row, as before.
				var classes = ungated.GroupBy(Signature).ToList();
				if (classes.Count < 2)
				{
					result.AddRange(ungated);
					continue;
				}

				var (gates, unsplit) = Discriminate(classes);
				var shared = unsplit > 0
					? $"; {unsplit} share a captured state and are left to Merge's packet-count tiebreak"
					: "";

				report?.Add(gates.Count == 0
					? $"{group.Key.Identity} '{group.Key.AnswerPath}': {classes.Count} variants, none the captured quest state can tell apart "
					  + "- keeping the richest"
					: $"{group.Key.Identity} '{group.Key.AnswerPath}': {classes.Count} variants split on quest state ("
					  + string.Join(", ", classes.Select(c => gates.TryGetValue(c.Key, out var g)
						  ? $"{g.Quest} {g.State}"
						  : "fallback")) + ")" + shared);

				foreach (var c in classes)
				{
					if (!gates.TryGetValue(c.Key, out var gate))
					{
						result.AddRange(c); // the least-informed variant is what a player with no chain progress gets
						continue;
					}

					foreach (var e in c)
						result.Add(e with
						{
							ConditionQuestId = gate.Quest,
							ConditionQuestState = gate.State
						});
				}
			}

			return result;
		}

		// Picks one (quest, state) gate per signature class, plus how many classes the captured state could NOT tell
		// apart from the one before them.
		//
		// The classes are ordered by how much of the player's quest state the capture knew - a variant seen deep in a
		// chain knows more quests than the one a fresh character got - and the LEAST-informed class becomes the
		// unconditional fallback. Every other class is gated on a (quest, state) pair its captures agree on and that no
		// less-or-equally-informed class carries. Two variants late in the same chain can therefore end up with gates
		// that BOTH pass at once (Casilda's escort greeting is 12407 Active; her later ones add 12369/12373 on top of a
		// still-active 12407); the runtime resolves that by serving the variant gated on the quest the player accepted
		// most recently, which is the beat they are actually on (ConversationService.PickConversationScreen).
		//
		// ⚠️ A class with no pair left INHERITS the previous class's gate rather than failing the whole group. Those two
		// are the same screen as far as the captures can say - most often one capture of it carried a trailing close
		// window and the other did not - so handing both the same gate lets Merge keep the richer, which is exactly the
		// old behaviour, WITHOUT dropping the gates every other variant in the group had earned.
		private static (Dictionary<string, (uint Quest, QuestState State)> Gates, int Unsplit) Discriminate(List<IGrouping<string, ConversationEntry>> classes)
		{
			// A capture that never showed us a quest state says nothing about which variant this is, so it cannot
			// contribute pairs; a class of nothing BUT such entries can only ever be the fallback.
			var pairs = classes.ToDictionary(c => c.Key, Agreed);
			var reach = classes.ToDictionary(c => c.Key, c => c.Max(e => e.QuestStates?.Count ?? 0));
			var ordered = classes.Select(c => c.Key).OrderBy(k => reach[k]).ThenBy(k => k, System.StringComparer.Ordinal).ToList();

			var gates = new Dictionary<string, (uint Quest, QuestState State)>();
			var unsplit = 0;
			for (var i = 1; i < ordered.Count; i++)
			{
				var key = ordered[i];
				// Absent from every class the runtime would otherwise confuse this one with: the fallback and everything
				// less informed. A more-informed class is allowed to share the pair - it carries a gate of its own that
				// is strictly later in the chain, and the most-recent-quest rule prefers it.
				var blocked = ordered.Take(i).SelectMany(k => Carried(classes.First(c => c.Key == k))).ToHashSet();
				// ACTIVE first: a conditioned variant is about the quest the player is ON, and that is also the gate the
				// runtime's most-recent-quest tiebreak can order. Gating Casilda's 12369 greeting on "10238 Completed"
				// (equally true, and the lowest id available) would be true from far earlier in the chain and would lose
				// the tiebreak to a later quest, serving the wrong line.
				var pick = pairs[key].Where(p => !blocked.Contains(p)).OrderBy(p => p.State == QuestState.Active ? 0 : 1).ThenBy(p => p.Quest).ToList();
				if (pick.Count == 0)
				{
					unsplit++;
					if (gates.TryGetValue(ordered[i - 1], out var inherited))
						gates[key] = inherited;

					continue;
				}

				gates[key] = pick[0];
			}

			return (gates, unsplit);
		}

		// The (quest, state) pairs EVERY informative capture of this variant agrees on. Intersecting rather than
		// unioning is what keeps a pair that one walk happened to carry (an unrelated quest the player picked up) from
		// being read as the thing that selects the variant.
		private static HashSet<(uint Quest, QuestState State)> Agreed(IGrouping<string, ConversationEntry> c)
		{
			HashSet<(uint, QuestState)>? agreed = null;
			foreach (var e in c)
			{
				if (e.QuestStates is not { Count: > 0 } states)
					continue;

				var here = states.Select(kv => (kv.Key, kv.Value)).ToHashSet();
				if (agreed == null)
					agreed = here;
				else
					agreed.IntersectWith(here);
			}

			return agreed ?? [];
		}

		// Every pair ANY capture of this variant carried - what it may not be confused with.
		private static HashSet<(uint Quest, QuestState State)> Carried(IGrouping<string, ConversationEntry> c)
		{
			return c.SelectMany(e => e.QuestStates ?? new Dictionary<uint, QuestState>()).Select(kv => (kv.Key, kv.Value)).ToHashSet();
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
