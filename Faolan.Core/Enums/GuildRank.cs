namespace Faolan.Core.Enums
{
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
