namespace Faolan.Core.Enums
{
	// Realm availability shown on the client's realm-select screen (2nd uint32 per realm in SendRealmList; the
	// crowding indicator is the SEPARATE LoadStatus field). CLIENT-DERIVED: AgeOfConan.exe renders this through a
	// switch (FUN_011f4500) that wraps each realm's label in colored HTML, and one caller (FUN_011f4490) gates on
	// `status == 2` as its "is up" test - which is why 2, not 1, is our "online" value. In our captures retail only
	// ever sent ONE realm, so only 0 and 2 were observed on the wire; 1 is a real client-accepted state we simply
	// never emit (documented here so the gap isn't a mystery, not because we send it).
	public enum RealmOnlineStatus : byte
	{
		// Down. Client renders the realm name bright red (#FF1010, the ServerOffline label).
		Offline = 0,

		// Up but restricted - reachable yet not fully open (the client's ServerCannotAcceptNewCharacters / lighter
		// "Recommended" state; renders green like Online). Inferred from the client's status switch, NOT observed in
		// our single-realm captures - we don't emit it today; kept to document the client's real value range.
		Locked = 1,

		// Up and open. The value the client's `== 2` "is online" gate expects; renders green (#5B933D). This is what
		// RealmStatusService writes while a realm's game host is running.
		Online = 2
	}
}
