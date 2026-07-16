namespace Faolan.Core
{
	// The retail Age of Conan content CDN hosts (still live and publicly readable, which is what lets the importer
	// pull on-demand RDB records the local NN.rdbdata files don't carry - le.idx file==0xFF entries).
	//
	// Single source of truth for the three upstreams: Faolan.Web.Content proxies all three (its `/{host}/...` mirror
	// routes), and the RDB importers fetch through either that mirror or GameUpdate directly. They used to be spelled
	// out in both places - Faolan.Web.Content's upstream map and a private const in RdbRespawnImporter - so a host
	// change had to be made twice. Faolan.Core is the BASE layer both sides already reference.
	public static class AocContentCdn
	{
		// The game-content host: RDB resources (resurrection points, maps, ...) live here under /upm/rdb/res/...
		public const string GameUpdate = "http://aoc-eu-gameupdate.live.ageofconan.com";

		// The patcher/manifest host.
		public const string Update = "http://aoc-eu-update.live.ageofconan.com";

		// The control/config host.
		public const string Control = "http://aoc-eu-control.live.ageofconan.com";
	}
}
