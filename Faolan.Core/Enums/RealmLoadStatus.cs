namespace Faolan.Core.Enums
{
	/// <summary>
	///     Realm population/crowding, shown as the coloured realm name on the client's realm-select screen. A separate
	///     field from <see cref="RealmOnlineStatus" />.
	/// </summary>
	/// <remarks>
	///     Client-derived: for an online realm, AgeOfConan.exe's row renderer (FUN_011f4490) computes a display code
	///     of <c>LoadStatus + 1</c> and feeds it to a six-case switch (FUN_011f4500); Offline is display 0. The
	///     renderer reads the value from Dimension_t+0x30, the fourth u32 after the realm name. The chain is explicit
	///     rather than a jump table - <c>test ecx,ecx</c> then <c>cmp ecx,1/2/3/4</c>, each arm taking
	///     <c>lea eax,[ecx+1]</c>, with <c>xor eax,eax</c> for anything else - which is what proves the slot holds
	///     0..4 and not a boolean.
	///     <para />
	///     The five load levels are display codes 1..5, each with a fixed HTML colour and Launcher label:
	///     <list type="bullet">
	///         <item>0 Recommended - #5B933D green (ServerRecommended)</item>
	///         <item>1 Normal - #5B933D green (ServerNormal)</item>
	///         <item>2 Heavy - #5B933D green (ServerHeavy)</item>
	///         <item>3 VeryHeavy - #FE5604 orange (ServerHeavy, same label text as 2, brighter warning colour)</item>
	///         <item>4 Full - #B41D1D red (ServerFull)</item>
	///     </list>
	///     The client accepts all five; our seeded realms only ever use Recommended, as the higher levels would be
	///     driven by a live population count we do not compute.
	/// </remarks>
	public enum RealmLoadStatus : byte
	{
		Recommended = 0,
		Normal = 1,
		Heavy = 2,
		VeryHeavy = 3,
		Full = 4
	}
}
