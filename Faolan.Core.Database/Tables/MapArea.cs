using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One named SUB-AREA of a playfield: the region that floats the top-of-screen area banner when a player walks
	// into it (#348). A map has MANY - Old Tarantia alone fires six (Bridge of Vilerus I, The East Gate, The West
	// Gate, ...) - which is why this is a child table and not the single `Map.AreaNameId` scalar it replaces.
	//
	// ⚠️ **The TEXT is the CLIENT'S.** We put `AreaNameId` on the wire and nothing else (`SendAreaNameBanner` ->
	// `AreaNameBanner`), and the client renders the string - "The West Gate" sits in English LDB page 974162. An
	// earlier version of this comment said the banner text was "server-side"; that is wrong, and so is the reading of
	// the evidence it cited. Neither this table nor `MapRegion` carries display text and neither should.
	//
	// What IS ours is the pair of things the client cannot answer: WHICH area the player is in, and hence which id to
	// send. The client's bounded-area record does not name a banner id, so the region -> AreaNameId link had to be
	// reconstructed, and it was reconstructed GEOMETRICALLY (see RegionId below, #513).
	//
	// The hull below is CAPTURE-SOURCED for the same reason - `AreaNameExtractor` pairs every captured
	// FormatFeedbackIIR with the player's position at that moment - and it stays the fallback for the 10 banners the
	// geometric match could not corroborate.
	//
	// ⚠️ Two claims that have already been wrong here once each, so they are worth stating carefully:
	//   - "the fire position lands in no client region except a collision volume" - FALSE. It lands in `BA_Bridge`,
	//     a 9-vertex polygon named after the bridge the banner announces. The client ships the trigger volumes.
	//   - "none of the 22 AreaNameIds appears in any of the 1,103 LDB pages" - TRUE, and evidence of nothing. The id
	//     is a parameter inside the localized token, not an LDB key, so it was never going to appear. See
	//     AreaNameId below for the hash that does resolve it.
	//
	// There is deliberately no NAME column. The wire only ever carries AreaNameId and the CLIENT renders the text,
	// so a name would be for our logs alone - and it would be a stale copy of something the client RDB can answer
	// exactly, in seven languages, from the id alone (see AreaNameId). Read one with
	// `--report ldb-strings --report-arg "id:#<elf28 of the token>"` rather than storing it.
	[PrimaryKey(nameof(MapId), nameof(AreaNameId))]
	public class MapArea
	{
		// The area's text id as the banner's XML carries it, and what goes on the wire. Correct as stored - it is a
		// PARAMETER inside the localized token, not an LDB key:
		//
		//   <localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|3474586'>
		//
		// The client's LDB id is `elf28(token)` over that WHOLE string, so the number here is never itself an index
		// into the LDB. Searching the LDB for it therefore always fails, and that failure means nothing.
		//
		// ⚠️ It has meant something twice, wrongly. The absence was read as "these ids are per-BUILD and a client
		// patch renumbered them", which is false - the client has not been patched, and the arithmetic settles it:
		//
		//   elf28("DisplayedText(3)|GenericDisplayAreaName|3474586") == 19303910 == cat 50025 "Castle Approach"
		//
		// So resolving one is a pure hash, with no per-page "pin" needed (the #291 caveat below does not apply to
		// these): hash the token, look the result up in category 50025.
		// `--report ldb-strings --report-arg "id:#<n>"` does the lookup.
		//
		// That also CORROBORATES the geometric region match by name, which nothing else could: 3373519 resolves to
		// "Bridge of Vilerus I" and is linked to `BA_Bridge`, 3385037/3385038 to "The East Gate"/"The West Gate" on
		// `BA_EastGate`/`BA_WestGate`, and 3147973 to "The Southern Bridge" on the region of that exact name.
		public uint AreaNameId { get; set; }
		public uint MapId { get; set; }

		// The client's REAL trigger volume for this banner (#513), or null where we could not corroborate one - those
		// fall back to the captured hull below plus WorldSimulation's slack.
		//
		// The volume replaces an approximation with the thing itself: `BA_Bridge` is a 9-vertex polygon spanning
		// x 924-960, z 718-845 and only 10 units tall, against a hull over seven sampled walk positions padded by a
		// flat runtime slack. SetNull rather than cascade - losing the volume must degrade to the hull, never delete
		// the area.
		public uint? RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion? Region { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		public float MaxX { get; set; }
		public float MaxY { get; set; }
		public float MaxZ { get; set; }

		// The axis-aligned bounds of the captured sightings - the interim region shape. A HULL OVER SAMPLES, not the
		// retail trigger volume: it can only cover ground a captured player actually walked, so it UNDER-covers by
		// construction and WorldSimulation widens it with AreaBannerSlack rather than treating it as a hard polygon.
		// It tightens with every new dump; no chat marker needed - a plain dump carries the banner and the position.
		public float MinX { get; set; }
		public float MinY { get; set; }
		public float MinZ { get; set; }

		// How many captured banners back this area. 1 = a single sighting, so the bounds are a point and only the
		// slack does any work; more samples = a real strip (Bridge of Vilerus I has seven, spanning z 711..848 -
		// a bridge deck, not scatter).
		//
		// DIAGNOSTIC ONLY - nothing branches on it. It is kept because it is the one thing that says how much the
		// FALLBACK hull is worth, and 10 of the 22 areas are still on that path after #513; a `samples=1` row and a
		// `samples=18` row are not the same kind of evidence even though the columns look alike.
		//
		// ⚠️ **Do not carry it into client-sourced rows** (#679). A row built from the client's own region + the
		// recovered banner id has no samples, and writing 0 there would read as "no evidence backs this area" when
		// it means "not capture-derived at all" - a value that looks like data but is really provenance. When those
		// rows land, this becomes a Source enum (the shape `NpcSpawnArea.ResolvedBy` already uses) and the count
		// either folds into it or goes. Melvin's call, 2026-08-06: fold it into #679 rather than removing it now.
		public uint SampleCount { get; set; }
	}
}
