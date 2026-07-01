using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Extractors
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
		// The Casilda chain (#261/#264): 13479 (kill) -> 13563 (loot) -> 13480 (free) -> 12407 (escort). She is
		// "freed" when 13480 is completed; her dialogue flips per chain stage.
		private const int CasildaFreeQuest = 13480;

		// RequiredContextId = the quest that must have been ACTIVE at the captured greeting (ConversationEntry.
		// QuestContextId) for this rule to apply; AnyContext (-1) = apply regardless. This splits ONE capture that talks
		// to an NPC at several chain stages into per-state rows (#264): the Hoxxling gameplay capture talks to Casilda
		// both at the offer AND after you have the key, both as the "" greeting - only the captured quest state tells
		// them apart.
		private const long AnyContext = -1;

		// An NPC is "state-managed" once it appears in any rule: its dialogue then comes ONLY from classified captures.
		private static readonly List<(string CaptureBaseName, string NpcName, long RequiredContextId, string Condition)> Rules =
		[
			// Casilda OFFER (fresh char, quest 13479 not yet accepted) = the canonical fresh first-quest capture:
			// greeting -> 0 -> 0>0 -> 0>0>0 -> 0>0>0>0 grants 13479, then the next pick CLOSES (6 screens, matches
			// retail). The Strie/strickenbla walks are deliberately NOT unioned in here: they continue past the grant
			// to a FOLLOW-UP quest 13480 (at 0>0>0>0>0), which made the accept grant two quests / fire two
			// notifications. Keeping the offer to randochar alone reproduces retail's single-quest first-offer flow.
			("Ranochar_mage-necromancer_login_get-casilda-quest", "Casilda", AnyContext, ""),
			// Casilda AFTER accepting but BEFORE completing (in-progress greeting; this char already had the quest).
			("Stihetfv_soldier-conqueror_login", "Casilda", AnyContext, $"return player:HasQuest({CasildaFirstQuest}) and not player:HasCompletedQuest({CasildaFirstQuest})"),
			// Casilda FREEING dialogue (#264): the gameplay capture talks to her with the key (quest 13480 active at the
			// greeting - QuestContextId 13480), the "I'll protect you now" one-off. Gate it on HasQuest(13480); the
			// runtime serves it while 13480 is still active, THEN the talk completes the stage (-> escort 12407). Only the
			// context-13480 entries are kept; that capture's offer/other-state Casilda screens are dropped (covered above).
			("Hoxxling_mage-heraldofxotli_login_gameplay", "Casilda", CasildaFreeQuest, $"return player:HasQuest({CasildaFreeQuest}) and not player:HasCompletedQuest({CasildaFreeQuest})"),
			// Casilda AFTER she is freed (chain stage 13480 completed): the unchained post-quest greeting. Was gated on
			// HasCompletedQuest(13479) (true from stage 1, so it wrongly showed during the whole chain, #264) - move it to
			// HasCompletedQuest(13480) so it only shows once she's actually been freed.
			("Stihetfv_soldier-conqueror_login_compat_compeltefirstquest", "Casilda", AnyContext, $"return player:HasCompletedQuest({CasildaFreeQuest})"),
			("Stihetfv_soldier-conqueror_login_after_first_quest_kalanthes_chat", "Casilda", AnyContext, $"return player:HasCompletedQuest({CasildaFreeQuest})")
		];

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
				// Match the rule for this (capture, NPC) whose RequiredContextId is AnyContext or equals the entry's
				// captured quest state (#264). A context-specific rule only claims its own state's screens; the rest of
				// that capture's screens for the NPC fall through to "no rule" and are dropped (managed-NPC hygiene).
				var rule = Rules.FirstOrDefault(r => r.CaptureBaseName == captureBaseName && r.NpcName == e.NpcName && (r.RequiredContextId == AnyContext || r.RequiredContextId == e.QuestContextId));

				if (managed && rule.NpcName == null)
					continue; // managed NPC, unclassified capture/state -> drop so it can't pollute the per-state tree

				yield return string.IsNullOrEmpty(rule.Condition)
					? e // unmanaged NPC, or the OFFER fallback (empty condition)
					: e with { ConditionScript = rule.Condition };
			}
		}
	}
}
