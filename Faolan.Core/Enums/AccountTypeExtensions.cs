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
		// ⛔ `GmPrivileges.GodMode` (0x200) is NOT a UI permission and must not ride along with the role. It is
		// CLIENT-SIDE INVULNERABILITY: the client's own HealthCurrent handler opens with
		//
		//     GetStat(0xD7); if (gmLevel & 0x200) { if (newValue < oldValue) return; }   // FUN_010fd220 @010fde3a
		//
		// so with the bit set the client DISCARDS every server-driven health decrease. Damage still lands
		// server-side and the character still dies - the bar just never moves, which is exactly what #902 was.
		// Proven in-client 2026-08-31 with Frida on the local player's stat setter: the write arrives carrying our
		// value and the stored health is unchanged, and clearing stat 215 makes the very next decrease apply
		// (`CMsg_StatChange()..(27 9999 old:25000)`).
		//
		// The bit therefore follows the SERVER's own `.godmode` toggle (`Account.GmGodMode`), which is the
		// authoritative one the simulation already honours - not the account TYPE. A GM who has not switched god
		// mode on takes damage on screen like anybody else.
		public static uint ToClientGmLevel(this AccountType type, bool godMode = false)
		{
			var role = type switch
			{
				AccountType.Administrator => GmRoles.All,
				AccountType.GameMaster => GmRoles.Support2,
				_ => 0u
			};

			return godMode
				? role
				: role & ~(uint)GmPrivileges.GodMode;
		}
	}
}
