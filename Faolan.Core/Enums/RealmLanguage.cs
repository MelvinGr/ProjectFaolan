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
	// ⚠️ The VALUES below are NOT verified and are carried over unchanged from the old enum - renaming a field is not
	// evidence about its encoding. They match nothing observed: the client bundles seven languages and orders its LDB
	// pages fr, en, de, es, ru, pl, ko, and `LoginCharacter` sends the client's own language as a string CODE ("en")
	// beside a separate uint16 regionCode. Nothing was found reading Dimension_t+0x40, so the client may not consume
	// this at all. Establishing the real id space is Gitea #932; do not "fix" these on a guess - the committed
	// faolan.db and every declared realm carry them.
	public enum RealmLanguage : byte
	{
		English = 0,
		French = 1,
		Spanish = 2,
		German = 3
	}
}
