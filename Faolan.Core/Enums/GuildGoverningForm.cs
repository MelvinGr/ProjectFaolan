namespace Faolan.Core.Enums
{
	/// <summary>
	///     The seven guild governing forms; the id is the one the wire carries as the <c>i_nGoverningForm</c> byte of
	///     CreateGuild(guildName, governingForm, tax, lookingForMember).
	/// </summary>
	/// <remarks>
	///     Decoded from the client's GuildGoverningFormDataTable (RDB 1000517), whose parse callback reads
	///     [u32 count][u32 version=1] then count x ([u32 id][string]); see docs/rdb/client-data-tables.md.
	///     <c>Milita</c> is the client's own spelling, kept so a name lookup against the table matches.
	/// </remarks>
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
