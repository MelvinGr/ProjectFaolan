using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One named SUB-AREA of a playfield: the region that floats the top-of-screen area banner when a player walks
	// into it (#348). A map has MANY - Old Tarantia alone fires six (Bridge of Vilerus I, The East Gate, The West
	// Gate, ...) - which is why this is a child table and not the single `Map.AreaNameId` scalar it replaces.
	//
	// CAPTURE-SOURCED, because the client does not have this. The boundary triggers are SERVER-side: scanning all
	// 410,433 local RDB records for a banner's text id found only chance 4-byte collisions in multi-MB textures, and
	// the position where "Bridge of Vilerus I" fires lands in no client region except a collision volume. So the
	// region is reconstructed from where retail ACTUALLY sent each banner - `AreaNameExtractor` pairs every captured
	// FormatFeedbackIIR with the player's position at that moment.
	//
	// There is deliberately no NAME column. The wire only ever carries AreaNameId and the CLIENT renders the text,
	// so a name would be for our logs alone - and filling it needs the client RDB plus a hand-found "pin" per LDB
	// page (`textId = pageBase + index`, #291; the base is not derivable from the page header yet). One pin covers
	// Old Tarantia; the other maps' ids sit in pages nobody has pinned, so the column would have been empty for 3 of
	// 4 maps. The id IS the key. To read a name, decode it via #291 rather than storing a stale copy.
	[PrimaryKey(nameof(MapId), nameof(AreaNameId))]
	public class MapArea
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The client's localized-string id for this area - the value the banner's XML carries. The stable key: a
		// playfield's areas are identified by it, and it is what actually goes on the wire.
		public uint AreaNameId { get; set; }

		// The axis-aligned bounds of the captured sightings - the interim region shape. A HULL OVER SAMPLES, not the
		// retail trigger volume: it can only cover ground a captured player actually walked, so it UNDER-covers by
		// construction and WorldSimulation widens it with AreaBannerSlack rather than treating it as a hard polygon.
		// It tightens with every new dump; no chat marker needed - a plain dump carries the banner and the position.
		public float MinX { get; set; }
		public float MaxX { get; set; }
		public float MinY { get; set; }
		public float MaxY { get; set; }
		public float MinZ { get; set; }
		public float MaxZ { get; set; }

		// How many captured banners back this area. 1 = a single sighting, so the bounds are a point and only the
		// radius does any work; more samples = a real strip (Bridge of Vilerus I has five, spanning z 711..848 -
		// a bridge deck, not scatter).
		public uint SampleCount { get; set; }
	}
}
