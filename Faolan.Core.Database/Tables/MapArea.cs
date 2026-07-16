using System.ComponentModel.DataAnnotations;
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
	// The NAME is resolved through the LDB: the banner's `category` + text id map onto (RDB 1030002 page, string
	// index) as `textId = pageBase + index` (#291). It is stored here so the server can log/inspect an area without
	// the client RDB - the wire only ever carries AreaNameId.
	[PrimaryKey(nameof(MapId), nameof(AreaNameId))]
	public class MapArea
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The client's localized-string id for this area - the value the banner's XML carries. The stable key: a
		// playfield's areas are identified by it, and it is what actually goes on the wire.
		public uint AreaNameId { get; set; }

		// The localized name (e.g. "Bridge of Vilerus I"), decoded from the LDB via the #291 base+index rule. Empty
		// when no pin has resolved that id's page yet. Server-side only - never sent.
		[MaxLength(128)]
		public string Name { get; set; } = "";

		// The axis-aligned bounds of the captured sightings, the interim region shape. This is a HULL OVER SAMPLES,
		// not the retail trigger volume: with few sightings it under-covers, so it is deliberately paired with
		// SampleRadius rather than used as a hard polygon. It tightens as more marked dumps land (walk in, type a
		// chat line, dump - the trick that produced the first pin).
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
