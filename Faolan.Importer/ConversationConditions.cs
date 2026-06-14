using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer
{
	// Quest/tag conditions for captured NPC dialogue (issue #37 - the tag/quest dialogue VM). Some NPCs say
	// DIFFERENT things depending on quest state (e.g. Casilda's pre- vs post-first-quest greeting, and she's chained
	// only before the quest). The captured dialogue is the RESULT the retail server sent for THAT char's state, so we
	// tag each capture's screens for an NPC with the Lua condition (run live by LuaScriptVm) under which they apply.
	// The runtime then serves the matching variant; without a tag the dialogue would MERGE into one mixed screen.
	//
	// For a STATE-MANAGED NPC (any NpcName that appears in Rules) the classification is AUTHORITATIVE: only the
	// captures listed here contribute that NPC's dialogue. An unlisted capture's nodes for the NPC are DROPPED, so a
	// mixed-state capture (e.g. a mid-game char re-walking and picking up follow-up quests) or a loopback of our own
	// server can't pollute the per-state tree. The whole tree (every AnswerPath), not just the greeting, is gated.
	//
	// Keyed by the capture's BASE filename (no .tshark.json) + NPC name. Unmanaged NPCs (not listed) stay
	// unconditional regardless of capture.
	public static class ConversationConditions
	{
		// Casilda gives the "Casilda" quest, id 13479 (capture: …randochar-login-get-casilda-quest, QuestFullUpdate
		// 0x63268258 id=13479 after the ApproachCasilda destiny step). Her dialogue + her chained/unchained state flip
		// on it. (Separate from the "Shipwrecked" quest, given after the Kalanthes conversation — id not yet captured.)
		private const int CasildaFirstQuest = 13479;

		// Per-(capture, NPC) quest-STATE classification. Condition = the Lua gate under which the capture's whole tree
		// applies; "" marks the OFFER state (the unconditional fallback a fresh char gets, since it fails every gate).
		// Captures sharing a Condition UNION into one consistent tree (the offer captures below all grant 13479 at the
		// same path 0>0>0>0, so unioning them only fills in branches).
		//   OFFER (not yet accepted)         = "" (fallback)
		//   IN PROGRESS (accepted, not done) = `HasQuest(X) and not HasCompletedQuest(X)`
		//   DONE                             = `HasCompletedQuest(X)`
		// (The two non-empty gates are mutually exclusive, so runtime order doesn't matter; the offer fallback is
		// served only when both fail.)
		private static readonly List<(string CaptureBaseName, string NpcName, string Condition)> Rules =
		[
			// Casilda OFFER (fresh char, quest 13479 not yet accepted) - three clean offer walks that agree
			// (greeting -> 0 -> 0>0 -> 0>0>0 -> 0>0>0>0 grants 13479); they union into one offer tree.
			("account-stricken997-character-randochar-login-get-casilda-quest", "Casilda", ""),
			("account-stricken997-character-Strie-login-randomActions", "Casilda", ""),
			("account-stricken998-character-strickenbla-create-random-actions-combat-levelup-questcomplete", "Casilda", ""),
			// Casilda AFTER accepting but BEFORE completing (in-progress greeting; this char already had the quest).
			("account-stricken998-character-xxx-login", "Casilda", $"return player:HasQuest({CasildaFirstQuest}) and not player:HasCompletedQuest({CasildaFirstQuest})"),
			// Casilda AFTER it is complete: the turn-in capture + the post-quest login (the unchained, different greeting).
			("account-stricken998-character-xxx-compat_compeltefirstquest", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
			("account-stricken998-character-xxx-login-after_first_quest_kalanthes_chat", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
		];

		// An NPC is "state-managed" once it appears in any rule: its dialogue then comes ONLY from classified captures.
		private static readonly HashSet<string> ManagedNpcs = Rules.Select(r => r.NpcName).ToHashSet();

		// Applies the quest-state classification to one capture's extracted entries. For a managed NPC, an entry from a
		// capture with no rule is DROPPED (returns nothing); a classified capture's entries are tagged with that rule's
		// condition (whole tree). An entry that already carries a condition (e.g. an auto-derived offer gate) is left
		// as-is. Unmanaged NPCs pass through unchanged. Returns new entries (records are immutable).
		public static IEnumerable<ConversationEntry> Apply(string captureBaseName, IEnumerable<ConversationEntry> entries)
		{
			foreach (var e in entries)
			{
				if (!string.IsNullOrEmpty(e.ConditionScript))
				{
					yield return e;
					continue;
				}

				var managed = !string.IsNullOrEmpty(e.NpcName) && ManagedNpcs.Contains(e.NpcName);
				var rule = Rules.FirstOrDefault(r => r.CaptureBaseName == captureBaseName && r.NpcName == e.NpcName);

				if (managed && rule.NpcName == null)
					continue; // managed NPC, unclassified capture -> drop so it can't pollute the per-state tree

				yield return string.IsNullOrEmpty(rule.Condition)
					? e // unmanaged NPC, or the OFFER fallback (empty condition)
					: e with { ConditionScript = rule.Condition };
			}
		}
	}
}
