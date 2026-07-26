namespace Faolan.Core.Enums
{
	public static class AccountTypeExtensions
	{
		// The client GM privilege MASK (character stat 0x0D7 / player_stats.gm_level) an account type maps to. It is a
		// bitmask, not a level - see GmPrivileges, where the bit names and the client's `(gmLevel & required) != 0`
		// filter are both recovered from the exe. Any non-zero value flips the retail client into GM mode; WHICH
		// commands appear in /help then depends on which bits are set.
		//
		// An Administrator gets the client's own `all` role (every bit, including the seven it never names). A
		// GameMaster gets `support2` - the widest of the shipped non-developer roles: it carries the moderation set
		// (kick, cloak, GM chat and tag, broadcast, name/nick changes, teleports, god mode) without the Developer bit,
		// which is what gates the ~90 debug commands (/consumememory, /memoryprofile, /fakeevent, ...). Those are
		// client-side development tooling rather than GM powers, so they stay behind Administrator.
		public static uint ToClientGmLevel(this AccountType type) => type switch
		{
			AccountType.Administrator => GmRoles.All,
			AccountType.GameMaster => GmRoles.Support2,
			_ => 0
		};
	}
}
