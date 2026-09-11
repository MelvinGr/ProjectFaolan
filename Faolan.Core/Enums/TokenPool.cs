namespace Faolan.Core.Enums
{
	/// <summary>
	///     <c>tokenID</c>, the first byte of <c>DispatchUpdateTokenAmount</c> (s2c 0x204D) - which of the game's
	///     parallel token wallets a <c>gained</c>/<c>spent</c> pair belongs to.
	/// </summary>
	/// <remarks>
	///     These are the client's own names, carved out of RDB 1000520 ("Token/relic defs", one 760-byte record). Its
	///     entries are fixed-width around the name - <c>[u8][u8][u8 tokenId][u16 nameLen][name][7 unread bytes]</c> -
	///     and the id byte takes 29 distinct values across the 29 entries, which is what identifies it as the id
	///     rather than one of the other fixed bytes (<c>docs/re/token-pool-ids.tsv</c>). The block is sparse: 1..17,
	///     19, 30..36, 50, 60..62.
	///     <para />
	///     Four are seen on the wire, and they read the way the names promise: a brand-new character is told
	///     <see cref="VeteranPoints" /> 0/0 at its first world entry, a login-reward claim credits
	///     <see cref="TeleportCoins" /> or <see cref="LotteryToken" />, and
	///     <see cref="AltAdvancementGeneralToken" /> runs 1 to 2 for one character. The other 25 are named by the
	///     client and unobserved.
	///     <para />
	///     What grants alternate-advancement tokens is server-side and unrecovered: the corpus holds 32 quest
	///     completions (26 of them Destiny steps, 13 on the two lines that did pay) and exactly 2 paid, with no
	///     property of the quest, the line, the level-up or the daily counter separating them - see
	///     <c>docs/re/alternate-advancement-perks.md</c>. Open work tracked in Gitea #150.
	/// </remarks>
	public enum TokenPool : byte
	{
		VeteranPoints = 1,
		AltAdvancementPvPToken = 2,
		AltAdvancementPvEToken = 3,
		AltAdvancementGeneralToken = 4,
		SimpleRelicIII = 5,
		RareRelic = 6,
		SimpleRelicI = 7,
		SimpleRelicII = 8,
		WorldPvPToken = 9,
		PvPMinigameToken = 10,
		SimpleTrophy1 = 11,
		SimpleTrophy2 = 12,
		SimpleTrophy3 = 13,
		RareTrophy = 14,
		EsteemToken = 15,
		MarkOfAcclaim = 16,
		SimpleRelic = 17,
		DragonTear = 19,
		VictoryToken = 30,
		PortentRelic = 31,
		LotteryToken = 32,
		SimpleRelicV = 33,
		ShardOfPureIce = 34,
		EmeraldEssence = 35,
		SimpleRelicVI = 36,
		TeleportCoins = 50,
		AtlanteanShard = 60,
		OnslaughtToken = 61,
		SkullgateToken = 62
	}
}
