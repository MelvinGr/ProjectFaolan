namespace Faolan.Core.Enums
{
	// The realm's LANGUAGE - the last u32 of each per-realm record in SetDimensionList (Dimension_t+0x40), and what
	// retail's language-segregated EU dimensions were keyed on.
	//
	// ⭐ It was called RealmLanguage until #930, which had no basis at all: "country" does not occur ONCE in the
	// 34 MB AgeOfConan.exe, case-insensitively, and the name arrived verbatim in a 2021 commit with no capture,
	// comment or RE artifact behind it. What the client actually has is language, everywhere:
	//   - `PlayerAgentProxy_t::VerifyLanguageSetting(const int nClientLanguage)` and
	//     `PlayerInterfaceDispatch_t::DispatchResetLanguage(nNewLanguage)` - the id is an int in both;
	//   - the login LDB page (Data/text/en/00000105.tdbc) carries, in the same block as every dimension error
	//     ("The Dimension is down.", "Your account does not have access to the selected dimension."):
	//     "Your account does not have any valid LANGUAGE FLAGS. Please contact customer support." and
	//     "You did not have access to your selected language. It has been reset to English." (the latter is
	//     DispatchResetLanguage's own text). So dimension access is gated on the account's language flags, which is
	//     exactly what a per-dimension language is for.
	//
	// ⭐ The VALUES are the client's own id space, recovered from FUN_011D7740 - the function that turns the running
	// client's 2-letter language code into the int it hands to VerifyLanguageSetting. It is a hard-coded table, not a
	// load order and not anything the .tdbl files carry:
	//     en=0  fr=1  es=2  de=3  pl=4  ru=5  ko=6   (anything else falls back to 0)
	// Its inverse FUN_011D7800 maps the same ids back to codes and is what DispatchResetLanguage(nNewLanguage) feeds,
	// so both directions share this space. All seven are below, in the client's order.
	//
	// ⭐ The four that predate the decode (English/French/Spanish/German) turned out to be RIGHT - they match the
	// client's table exactly. Only the NAME was ever wrong (#930); the values were carried over untouched and then
	// confirmed, which is why they were left alone rather than "fixed" on a guess in the meantime.
	//
	// ⚠️ This is NOT the field the client resolves a dimension's language from. That is the BITFIELD at
	// Dimension_t+0x24 (FUN_00A69180: bit 0 or 1 -> English, 0x400 -> Russian, 0x800 -> Polish, 0x100000 -> Korean).
	// The value here lands at +0x40, which the client reads only to test NON-ZERO - it counts the realms that have one
	// and shows or hides its language-flag button row. See #933.
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
