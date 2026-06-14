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
		// Casilda's first quest is 13479 ("Scavenger Lackey"); her dialogue + her chained/unchained state flip on it.
		private const int CasildaFirstQuest = 13479;

		private static readonly List<(string CaptureBaseName, string NpcName, string Condition)> Rules =
		[
			// Casilda BEFORE her first quest is complete (the chained, quest-giving greeting).
			("account-stricken998-character-xxx-login", "Casilda", $"return not player:HasCompletedQuest({CasildaFirstQuest})"),
			// Casilda AFTER it is complete: the turn-in capture + the post-quest login (the unchained, different greeting).
			("account-stricken998-character-xxx-compat_compeltefirstquest", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
			("account-stricken998-character-xxx-login-after_first_quest_kalanthes_chat", "Casilda", $"return player:HasCompletedQuest({CasildaFirstQuest})"),
		];

		// Tags each conversation entry from `captureBaseName` whose NPC matches a rule with that rule's condition; the
		// rest pass through unconditional (""). Returns new entries (records are immutable).
		public static IEnumerable<ConversationEntry> Apply(string captureBaseName, IEnumerable<ConversationEntry> entries)
		{
			foreach (var e in entries)
			{
				var rule = Rules.FirstOrDefault(r => r.CaptureBaseName == captureBaseName && r.NpcName == e.NpcName);
				yield return rule.Condition != null
					? e with { ConditionScript = rule.Condition }
					: e;
			}
		}
	}
}
