namespace Faolan.Core.Enums
{
	// Where a MapArea's (playfield, AreaNameId) pair came from, and with it how its region was bound -
	// provenance, not confidence. It is a column rather than something read off the geometry because a row built from
	// the client has no captured hull at all, and the "0 samples" that would have described it reads as "no evidence
	// backs this area" when it means "not capture-derived".
	//
	// It is also what says how `RegionId` was arrived at, which the two routes do completely differently and which
	// their gates have to tell apart:
	//
	//   * `Captured` with a region = matched geometrically, by finding the client volume the captured firing hull sits
	//     inside. Trustworthy because the volume separates that banner from every other one on the playfield - which
	//     is the property MapRegionGeometryTests holds it to.
	//   * `ClientName` / `Corroborated` = matched by name, region to text. Trustworthy because it is an identity
	//     rather than an inference, and separation is neither expected nor required: the client's own volumes overlap
	//     and nest, and the runtime picks the smallest containing one.
	public enum AreaBannerSource
	{
		// Harvested from captured gameplay: retail sent this AreaNameId on this playfield (AreaNameExtractor). The id
		// is wire truth; the bounds that come with it are a hull over the positions it was seen firing at, so they
		// under-cover by construction - which is why a row like this wants a client trigger volume where one can be
		// matched, and falls back to the hull plus WorldSimulation's slack where one cannot.
		Captured = 0,

		// Recovered from the client alone: the crossing-notifier region's own name is the banner text, found by
		// inverting elf28 over LDB category 50025 (AreaBannerNames). The id is the token parameter that hashes to that
		// string, and the region it belongs to is the one whose name matched - so the pair carries an exact trigger
		// volume and no hull at all. Reaches playfields no capture has ever visited.
		ClientName = 1,

		// Both: retail was captured sending this id on this playfield, and the client's own region of that name
		// recovers it. The two routes share no evidence - one is a position track against a wire packet, the other a
		// hash inversion against a string table - so an agreement is the strongest row in the table, and a
		// disagreement would be the loudest thing either route could say. Nothing produces a disagreement.
		Corroborated = 2
	}
}
