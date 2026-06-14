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
	// Keyed by the capture's BASE filename (no .tshark.json) + NPC name. Captures/NPCs not listed stay unconditional.
	public static class ConversationConditions
	{
		// Casilda gives the "Casilda" quest, id 13479 (capture: …randochar-login-get-casilda-quest, QuestFullUpdate
		// 0x63268258 id=13479 after the ApproachCasilda destiny step). Her dialogue + her chained/unchained state flip
		// on it. (Separate from the "Shipwrecked" quest, given after the Kalanthes conversation — id not yet captured.)
		private const int CasildaFirstQuest = 13479;

		// Quest-state greetings. The OFFER (not-yet-accepted) greeting stays the UNCONDITIONAL fallback - a fresh char
		// fails the two conditions below, so the runtime serves the fallback (offer). Accepting the quest at the NPC's
		// grant node (ConversationNode.GrantQuestId, harvested generically) flips HasQuest, so she then shows in-progress:
		//   IN PROGRESS (accepted, not done) = `HasQuest(X) and not HasCompletedQuest(X)`
		//   DONE                             = `HasCompletedQuest(X)`
		// (These two are mutually exclusive, so runtime order doesn't matter.)
		private static readonly List<(string CaptureBaseName, string NpcName, string Condition)> Rules =
		[
			// Casilda AFTER accepting but BEFORE completing (in-progress greeting; this char already had the quest).
			("account-stricken998-character-xxx-login", "Casilda", $"return player:HasQuest({CasildaFirstQuest}) and not player:HasCompletedQuest({CasildaFirstQuest})"),
			// Casilda AFTER it is complete: the turn-in capture + the post-quest login (the unchained, different greeting).
			("account-stricken998-character-xxx-compat_compeltefirstquest", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
			("account-stricken998-character-xxx-login-after_first_quest_kalanthes_chat", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
		];

		// Tags each conversation entry from `captureBaseName` whose NPC matches a rule with that rule's condition. An
		// entry that already has a condition (e.g. an auto-derived offer gate) is left as-is - the manual rules only
		// fill the in-progress/completed states. Returns new entries (records are immutable).
		public static IEnumerable<ConversationEntry> Apply(string captureBaseName, IEnumerable<ConversationEntry> entries)
		{
			foreach (var e in entries)
			{
				if (!string.IsNullOrEmpty(e.ConditionScript))
				{
					yield return e;
					continue;
				}

				var rule = Rules.FirstOrDefault(r => r.CaptureBaseName == captureBaseName && r.NpcName == e.NpcName);
				yield return rule.Condition != null
					? e with { ConditionScript = rule.Condition }
					: e;
			}
		}
	}
}
