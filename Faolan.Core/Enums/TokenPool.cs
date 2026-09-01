namespace Faolan.Core.Enums
{
	// `tokenID`, the first byte of DispatchUpdateTokenAmount (s2c 0x204D) - which of the game's parallel token
	// wallets a `gained`/`spent` pair belongs to.
	//
	// These are the CLIENT's own names, carved out of RDB **1000520** ("Token/relic defs", one 760-byte record). Its
	// entries are fixed-width around the name - `[u8][u8][u8 tokenId][u16 nameLen][name][7 unread bytes]` - and the
	// id byte takes 29 distinct values across the 29 entries, which is what identifies it as the id rather than one
	// of the other fixed bytes (`docs/re/token-pool-ids.tsv`). The block is sparse: 1..17, 19, 30..36, 50, 60..62.
	//
	// Four are seen on the wire, and they read the way the names promise: a brand-new character is told
	// `VeteranPoints` 0/0 at its first world entry, a login-reward claim credits `TeleportCoins` or
	// `LotteryToken`, and `AltAdvancementGeneralToken` runs 1 -> 2 for one character. The other 25 are named by
	// the client and unobserved.
	//
	// ⛔ The AA one used to say "a Destiny quest completion credits it". Measured with a control that is wrong:
	// the corpus holds 32 quest completions (26 of them Destiny steps, 13 on the two lines that DID pay) and
	// exactly 2 paid, with no property of the quest, the line, the level-up or the daily counter separating
	// them - see docs/re/alternate-advancement-perks.md. The grant rule is server-side and unrecovered (#150).
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
