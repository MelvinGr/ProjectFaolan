namespace Faolan.Core.Enums
{
	// Realm reachability shown on the client's realm-select screen (2nd uint32 per realm in SendRealmList).
	// CLIENT-DERIVED and effectively BINARY: AgeOfConan.exe's realm-row renderer (FUN_011f4490) tests this field
	// `== 2` as its sole "is this realm up?" check - 2 means online, ANY other value falls through to the Offline
	// branch. So there is no distinct third state here; the multi-colour realm-list gradient the client draws is the
	// SEPARATE LoadStatus/population field (Offline + 5 load levels), not this one. See RealmLoadStatus.
	public enum RealmOnlineStatus : byte
	{
		// Down / unreachable. The client renders the realm name bright red (#FF1010, the ServerOffline label). This
		// is the value for anything that isn't the exact "online" sentinel below.
		Offline = 0,

		// Up. The exact value the client's `== 2` gate expects; the realm then renders in its load colour (green →
		// orange → red by LoadStatus). This is what RealmStatusService writes while a realm's game host is running.
		Online = 2
	}
}
