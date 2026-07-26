namespace Faolan.Core.Enums
{
	public static class AccountTypeExtensions
	{
		// The client GM Level (character stat 0x0D7 / player_stats.gm_level) a given account type maps to. A non-zero
		// value flips the retail client into GM mode: the GM-Level-gated command set appears in /help (e.g.
		// /ChangeGameServer). The command surface is TIERED - a higher level unlocks more (e.g. /kill needs more than a
		// plain GameMaster's level). The exact per-command thresholds are still being reverse-engineered, so these
		// values are a first cut (GameMaster gets the standard GM set; Administrator gets a clearly-higher level to
		// reach the privileged commands). See the follow-up Gitea issue for the tier mapping.
		public static uint ToClientGmLevel(this AccountType type) => type switch
		{
			AccountType.Administrator => 10,
			AccountType.GameMaster => 5,
			_ => 0
		};
	}
}
