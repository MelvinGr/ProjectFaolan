namespace Faolan.Core.Network.Codecs.Quest
{
	// Quest CONTENT constants that several subsystems key off. Not a codec and not wire grammar - they are facts about
	// the shipped game data, kept here beside the quest codecs because that is where every caller already looks.
	public static class QuestContent
	{
		// The category of the area journal-title remoteformat.
		public const uint TitleCategory = 50300;

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 ("Shipwrecked") in its tails, with its
		// FULL multi-objective journal record, for every character (snapshot_embeds_strie_quest). The snapshot is the
		// authoritative delivery for it - the server must NOT re-send a (thinner, single-objective) QuestFullUpdate for
		// it on login, or it clobbers the snapshot's richer entry. Persisted via EnsureBeachStarterQuest; skipped in
		// RestorePlayerQuests.
		public const uint BeachStarterQuestId = 10238;
	}
}
