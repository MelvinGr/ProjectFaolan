using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One named SUB-AREA of a playfield: the region that floats the top-of-screen area banner when a player walks
	// into it (#348). A map has MANY - Old Tarantia alone fires six (Bridge of Vilerus I, The East Gate, The West
	// Gate, ...) - which is why this is a child table and not the single `Map.AreaNameId` scalar it replaces.
	//
	// CAPTURE-SOURCED, because the client does not name these: scanning all 410,433 local RDB records for a banner's
	// text id found only chance 4-byte collisions in multi-MB textures, and none of the 22 AreaNameIds appears in any
	// of the 1,103 LDB pages. So the banner TEXT really is server-side, and the sighting hull below is reconstructed
	// from where retail ACTUALLY sent each banner - `AreaNameExtractor` pairs every captured FormatFeedbackIIR with
	// the player's position at that moment.
	//
	// ⚠️ **The GEOMETRY is a different question, and this comment used to get it wrong.** It said the fire position
	// "lands in no client region except a collision volume", which is false: it lands in `BA_Bridge`, a 9-vertex
	// polygon named after the bridge the banner announces. The client ships the trigger volumes; only the text is
	// ours to supply. 12 of the 22 are now linked through `RegionId` below (#513) - see
	// docs/rdb/bounded-areas-1000053.md for how the client's own scry script disambiguated them.
	//
	// There is deliberately no NAME column. The wire only ever carries AreaNameId and the CLIENT renders the text,
	// so a name would be for our logs alone - and filling it needs the client RDB plus a hand-found "pin" per LDB
	// page (`textId = pageBase + index`, #291; the base is not derivable from the page header yet). One pin covers
	// Old Tarantia; the other maps' ids sit in pages nobody has pinned, so the column would have been empty for 3 of
	// 4 maps. The id IS the key. To read a name, decode it via #291 rather than storing a stale copy.
	[PrimaryKey(nameof(MapId), nameof(AreaNameId))]
	public class MapArea
	{
		// The client's localized-string id for this area - the value the banner's XML carries. The stable key: a
		// playfield's areas are identified by it, and it is what actually goes on the wire.
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
		// radius does any work; more samples = a real strip (Bridge of Vilerus I has five, spanning z 711..848 -
		// a bridge deck, not scatter).
		public uint SampleCount { get; set; }
	}
}
