namespace Faolan.Core.Enums
{
	// A member's rung on their guild's rank ladder, as the wire member record carries it (DispatchGuildCreated /
	// GuildFullUpdate).
	//
	// ⚠️ THERE IS NO CONSTANT THAT MEANS "LEADER". The ladder is per GOVERNING FORM and the forms are different
	// lengths - Stygian has four rungs, Cimmerian and Harem five, the other four six - so the top rung is 3, 4 or 5
	// depending on the form (Faolan.Core.Constants.GuildRankLadder, from the client's RDB 1000518). `Leader = 4` was
	// proven from the create-guild capture and is still right FOR THAT GUILD: its CreateGuild body is
	// `0006 "Faolan" 06 00 00`, i.e. governing form 6 = Harem, whose leader index is indeed 4. One form's ladder had
	// been generalised to all seven.
	//
	// Kept as the fallback for a form outside the table; use GuildRankLadder.LeaderRank(form) to pick the real one.
	public enum GuildRank : byte
	{
		Member = 0,

		Leader = 4
	}
}
