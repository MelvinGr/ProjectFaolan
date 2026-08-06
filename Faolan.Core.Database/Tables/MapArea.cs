using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One named SUB-AREA of a playfield: the region that floats the top-of-screen area banner when a player walks into
	// it (#348). A map has MANY - Old Tarantia alone fires six (Bridge of Vilerus I, The East Gate, The West Gate, ...)
	// - which is why this is a child table and not the single `Map.AreaNameId` scalar it replaces.
	//
	// ⚠️ **The TEXT is the CLIENT'S.** We put `AreaNameId` on the wire and nothing else (`SendAreaNameBanner` ->
	// `AreaNameBanner`), and the client renders the string - "The West Gate" sits in English LDB page 974162. An
	// earlier version of this comment said the banner text was "server-side"; that is wrong, and so is the reading of
	// the evidence it cited. Neither this table nor `MapRegion` carries display text and neither should.
	//
	// What IS ours is the pair of things the client cannot answer on its own: WHICH area the player is in, and hence
	// which id to send. The client's bounded-area record does not name a banner id - so the region -> AreaNameId link
	// had to be reconstructed, and it now comes from two independent directions (see Source):
	//
	//   * from a CAPTURE, pairing every captured FormatFeedbackIIR with the player's position at that moment
	//     (AreaNameExtractor), then matching that hull to the client volume containing it (#513);
	//   * from the CLIENT ALONE, because the banner id is a parameter inside the localized token and the LDB key is
	//     the elf28 hash of the whole token string - which inverts by sweeping the parameter, recovering both the id
	//     and its text, and the client's own region NAME is usually that text (#679, AreaBannerNames).
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
		// So resolving one is a pure hash, with no per-page "pin" needed (the #291 caveat does not apply to these):
		// hash the token, look the result up in category 50025. `--report ldb-strings --report-arg "id:#<n>"` does the
		// lookup, and `--report area-banner-names` does the whole table at once by INVERTING that hash.
		//
		// That also CORROBORATES the region match by name, which nothing else could: 3373519 resolves to
		// "Bridge of Vilerus I" and is linked to `BA_Bridge`, 3385037/3385038 to "The East Gate"/"The West Gate" on
		// `BA_EastGate`/`BA_WestGate`, and 3147973 to "The Southern Bridge" on the region of that exact name.
		public uint AreaNameId { get; set; }
		public uint MapId { get; set; }

		// The client's REAL trigger volume for this banner (#513/#679), or null where we could not find one - those
		// fall back to the captured hull below plus WorldSimulation's slack.
		//
		// The volume replaces an approximation with the thing itself: `BA_Bridge` is a 9-vertex polygon spanning
		// x 924-960, z 718-845 and only 10 units tall, against a hull over seven sampled walk positions padded by a
		// flat runtime slack. SetNull rather than cascade - losing the volume must degrade to the hull, never delete
		// the area.
		//
		// ⚠️ A `Source = ClientName` row ALWAYS has one (the region is where the match happened), so a null here means
		// a captured banner whose volume is still unidentified - which is the remaining gap, not a normal state.
		public uint? RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion? Region { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// Where the (MapId, AreaNameId) pair came from - see AreaBannerSource. This replaced a `SampleCount` (#679):
		// a client-sourced row has no samples at all, and writing 0 there would have read as "no evidence backs this
		// area" when it means "not capture-derived" - a value that looks like data but is really provenance.
		//
		// Nothing was lost with the count. What it was kept for was spotting a DEGENERATE fallback hull (one sighting
		// = a zero-area box that only fires because of AreaBannerSlack), and the bounds say that themselves:
		// `MinX == MaxX && MinZ == MaxZ` is exactly that row, without a second column that can disagree with them.
		public AreaBannerSource Source { get; set; }

		public float MaxX { get; set; }
		public float MaxY { get; set; }
		public float MaxZ { get; set; }

		// The axis-aligned bounds of the captured sightings - the interim region shape, and ALL ZERO on a client-sourced
		// row, which carries an exact volume instead and never falls back. A HULL OVER SAMPLES, not the retail trigger
		// volume: it can only cover ground a captured player actually walked, so it UNDER-covers by construction and
		// WorldSimulation widens it with AreaBannerSlack rather than treating it as a hard polygon. It tightens with
		// every new dump; no chat marker needed - a plain dump carries the banner and the position.
		public float MinX { get; set; }
		public float MinY { get; set; }
		public float MinZ { get; set; }
	}
}
