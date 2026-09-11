namespace Faolan.Core.Enums
{
	/// <summary>
	///     Realm reachability shown on the client's realm-select screen (second uint32 per realm in SendRealmList).
	/// </summary>
	/// <remarks>
	///     Client-derived and effectively binary: AgeOfConan.exe's realm-row renderer (FUN_011f4490) tests this field
	///     <c>== 2</c> as its sole "is this realm up?" check - 2 means online, any other value falls through to the
	///     Offline branch. There is no distinct third state here; the multi-colour realm-list gradient the client
	///     draws is the separate <see cref="RealmLoadStatus" /> population field.
	/// </remarks>
	public enum RealmOnlineStatus : byte
	{
		/// <summary>
		///     Down or unreachable. The client renders the realm name bright red (#FF1010, the ServerOffline label).
		///     This is the value for anything that is not the exact Online sentinel below.
		/// </summary>
		Offline = 0,

		/// <summary>
		///     Up. The exact value the client's <c>== 2</c> gate expects; the realm then renders in its load colour
		///     (green to orange to red by LoadStatus). This is what RealmStatusService writes while a realm's game
		///     host is running.
		/// </summary>
		Online = 2
	}
}
