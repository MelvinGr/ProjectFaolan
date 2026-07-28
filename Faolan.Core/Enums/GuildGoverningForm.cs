namespace Faolan.Core.Enums
{
	// The seven guild governing forms, and the id is the one the wire carries: CreateGuild(const std::string&
	// i_cGuildName, uint8 i_nGoverningForm, uint8 i_nTax, uint8 i_nLookingForMember). Decoded from the client's own
	// GuildGoverningFormDataTable (RDB 1000517), whose parse callback reads [u32 count][u32 version=1] then
	// count x ([u32 id][string]) - see docs/rdb/client-data-tables.md.
	//
	// `Milita` is the client's own spelling, kept so a name lookup against the table matches.
	public enum GuildGoverningForm : byte
	{
		Cimmerian = 0,
		Acheron = 1,
		Stygian = 2,
		Aquilonian = 3,
		Nemedian = 4,
		HyborianMilita = 5,
		Harem = 6
	}
}
