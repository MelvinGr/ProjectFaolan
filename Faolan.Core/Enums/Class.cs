namespace Faolan.Core.Enums
{
	public enum Class : byte
	{
		PriestOfMitra = 0x18,
		TempestOfSet = 0x1C,
		BearShaman = 0x1D,
		Conqueror = 0x16,
		DarkTemplar = 0x1F,
		Guardian = 0x14,
		Barbarian = 0x12,
		Ranger = 0x27,
		Assassin = 0x22,
		HeraldOfXotli = 0x2B,
		Demonologist = 0x2C,
		Necromancer = 0x29
	}

	// The guild-creator's rank on the wire. 4 = leader, proven by the create-guild capture (the member record
	// in DispatchGuildCreated / GuildFullUpdate); the earlier 6 was a blind guess. No rank-comparison logic
	// depends on the exact value, so this only affects the wire member record (now retail-accurate). See #306.
	public enum GuildRank : byte
	{
		Member = 0,
		// TODO: fill in rest
		Leader = 4
	}
}
