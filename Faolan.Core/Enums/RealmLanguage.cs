namespace Faolan.Core.Enums
{
	/// <summary>
	///     The realm's language - the last u32 of each per-realm record in SetDimensionList (Dimension_t+0x40), and
	///     what retail's language-segregated EU dimensions were keyed on.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The client models this as a language everywhere: <c>PlayerAgentProxy_t::VerifyLanguageSetting(const int
	///         nClientLanguage)</c> and <c>PlayerInterfaceDispatch_t::DispatchResetLanguage(nNewLanguage)</c> take the id
	///         as an int, and the login LDB page (Data/text/en/00000105.tdbc) carries, in the same block as every
	///         dimension error, "Your account does not have any valid LANGUAGE FLAGS. Please contact customer support."
	///         and "You did not have access to your selected language. It has been reset to English." (the latter is
	///         DispatchResetLanguage's own text). Dimension access is gated on the account's language flags.
	///     </para>
	///     <para>
	///         The values are the client's own id space, from FUN_011D7740 - the hard-coded table that turns the
	///         running client's 2-letter language code into the int it hands to VerifyLanguageSetting (not a load
	///         order and not anything the .tdbl files carry): en=0 fr=1 es=2 de=3 pl=4 ru=5 ko=6, anything else 0. Its
	///         inverse FUN_011D7800 maps the same ids back to codes and feeds DispatchResetLanguage(nNewLanguage), so
	///         both directions share this space.
	///     </para>
	///     <para>
	///         This is not the field the client resolves a dimension's language from. That is the bitfield at
	///         Dimension_t+0x24 (FUN_00A69180: bit 0 or 1 -> English, 0x400 -> Russian, 0x800 -> Polish, 0x100000 ->
	///         Korean). The value here lands at +0x40, which the client reads only to test non-zero - it counts the
	///         realms that have one and shows or hides its language-flag button row. Open work tracked in Gitea #933.
	///     </para>
	/// </remarks>
	public enum RealmLanguage : byte
	{
		English = 0,
		French = 1,
		Spanish = 2,
		German = 3,
		Polish = 4,
		Russian = 5,
		Korean = 6
	}
}
