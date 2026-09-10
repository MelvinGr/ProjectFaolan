namespace Faolan.Core.Enums
{
	// Realm population/crowding, shown as the coloured realm name on the client's realm-select screen (a SEPARATE
	// field from RealmOnlineStatus). CLIENT-DERIVED: for an online realm, AgeOfConan.exe's row renderer (FUN_011f4490)
	// computes a display code = LoadStatus + 1 and feeds it to a 6-case switch (FUN_011f4500); Offline is display 0.
	//
	// ⚠️ The renderer reads it from **Dimension_t+0x30**, which is the fourth u32 after the realm name - not the
	// fifth. The decode below was right and was attached to the wrong writer line for as long as it existed, so
	// SendRealmList sent a `== Full` flag in the rendered slot and this enum in one nothing reads (#931, fixed).
	// The chain is explicit rather than a jump table - `test ecx,ecx` then `cmp ecx,1/2/3/4`, each arm taking
	// `lea eax,[ecx+1]`, with `xor eax,eax` for anything else - which is what proves the slot holds 0..4 and not
	// a boolean.
	// The five load levels below are display codes 1..5, each with a fixed HTML colour + Launcher label:
	//   0 Recommended  #5B933D green   (ServerRecommended)
	//   1 Normal       #5B933D green   (ServerNormal)
	//   2 Heavy        #5B933D green   (ServerHeavy)
	//   3 VeryHeavy    #FE5604 orange  (ServerHeavy - same label text as 2, brighter warning colour)
	//   4 Full         #B41D1D red     (ServerFull)
	// The client accepts all five (0..4); our seeded realms only ever use 0 (Recommended) - the higher levels would
	// be driven by a live population count we don't compute yet. (Was Medium/Medium2/Heavy/Full 0..3, which mislabelled
	// value 3 as "Full" - it's actually the orange Heavy; the real red Full is 4, previously missing.)
	public enum RealmLoadStatus : byte
	{
		Recommended = 0,
		Normal = 1,
		Heavy = 2,
		VeryHeavy = 3,
		Full = 4
	}
}
