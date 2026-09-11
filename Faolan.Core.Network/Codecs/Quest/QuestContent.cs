namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Quest content constants several subsystems key off. Not a codec and not wire grammar but facts about the shipped
	///     game data, kept beside the quest codecs because that is where every caller already looks.
	/// </summary>
	public static class QuestContent
	{
		/// <summary>
		///     The Tortage beach world-entry snapshot embeds quest 10238 ("Shipwrecked") in its tails, with its full
		///     multi-objective journal record, for every character.
		/// </summary>
		/// <remarks>
		///     The snapshot is the authoritative delivery for it: re-sending the thinner, single-objective QuestFullUpdate on
		///     login clobbers the snapshot's richer entry. It is persisted via EnsureBeachStarterQuest and skipped in
		///     RestorePlayerQuests.
		/// </remarks>
		public const uint BeachStarterQuestId = 10238;
	}
}
