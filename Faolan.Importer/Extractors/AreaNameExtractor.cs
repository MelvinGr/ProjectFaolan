using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// A harvested map area name: the client localized-string id of the top-of-screen FIFO banner, plus the player
	// position it fired at (#348). One SIGHTING - a map has many areas and each fires repeatedly, so these are
	// aggregated into MapArea rows by MergeAreas.
	public sealed record MapAreaName(uint MapId, uint AreaNameId, float X, float Y, float Z);

	// Harvests EVERY area banner from captured gameplay, with the position it fired at. After a MapChange
	// (PlayfieldFIIR 0x5D85BFC7) the server sends a FormatFeedbackIIR (0x8D5A8B6B) FIFO banner whose remoteformat
	// carries `GenericDisplayAreaName|<textId>` - the localized area-name string id. Retail sends one whenever the
	// player crosses into a SUB-AREA, so a playfield has many: Old Tarantia (2000) alone fires six distinct ids.
	//
	// The position comes from the last C2S CharDCMoveIIR (0x4517AA81) before the banner - the client's own move, so
	// by definition the player's. That pairing IS the region data: the boundary triggers are SERVER-side (scanning
	// all 410,433 local RDB records for a banner id found only chance collisions in multi-MB textures, and the
	// "Bridge of Vilerus I" position lands in no client region but a collision volume), so where retail actually
	// sent each banner is the only evidence of where the areas are.
	//
	// This used to keep only the FIRST banner per map entry and store it as `Map.AreaNameId` - one scalar for a
	// one-to-many. That silently picked an arbitrary one of the six for Old Tarantia (it landed on 3373522 = "The
	// West Gate"); MapArea replaces it.
	public static class AreaNameExtractor
	{
		private static readonly Regex AreaId = new("GenericDisplayAreaName[|/](\\d+)", RegexOptions.Compiled);

		public static List<MapAreaName> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var results = new List<MapAreaName>();
			uint currentMap = 0;
			(float X, float Y, float Z)? pos = null;

			// Banners seen on this playfield before ANY c2s move, waiting for the next one. The ENTRY banner fires
			// right after the MapChange - before the client has moved - so a "last move before the banner" rule drops
			// exactly the sighting that names the area the player spawns into. The next move is inside the same area
			// (the player is standing in it when the banner shows), so it places the sighting just as well.
			var pending = new List<uint>();

			foreach (var p in packets)
			{
				// NB both directions: the banner is s2c but the POSITION comes from the client's own c2s move, so
				// this can't filter to downloads the way it did when it only read banners.
				if (!p.IsIirCarrier)
					continue;

				if (p is { SubOpcode: PlayfieldFCodec.Opcode, IsDownload: true })
				{
					// A short/partial one is not a map announcement we can trust - PlayfieldFCodec.Parse is strict
					// (#530 rule 4).
					if (!p.TryParse(PlayfieldFCodec.Parse, out var change) || change.MapId == currentMap)
						continue;

					currentMap = change.MapId;
					pos = null; // a stale position from the previous playfield would place the banner nowhere
					pending.Clear(); // ditto: a pending banner belongs to the playfield it fired on
					continue;
				}

				if (p is { SubOpcode: CharDCMoveCodec.Opcode, IsDownload: false })
				{
					// A C2S move is the client reporting its OWN position, so no handle check is needed.
					if (!p.TryParse(CharDCMoveCodec.Parse, out var move))
						continue;

					var moved = (move.Position.X, move.Position.Y, move.Position.Z);
					pos = moved;
					foreach (var areaId in pending)
						results.Add(new MapAreaName(currentMap, areaId, moved.X, moved.Y, moved.Z));
					pending.Clear();
					continue;
				}

				if (currentMap == 0 || !p.IsDownload || p.SubOpcode != FormatFeedbackCodec.Opcode)
					continue;

				if (!p.TryParse(FormatFeedbackCodec.Parse, out var feedback))
					continue;

				var m = AreaId.Match(Encoding.Latin1.GetString(feedback.Xml));
				if (!m.Success)
					continue;

				var id = uint.Parse(m.Groups[1].Value);
				if (pos is { } at)
					results.Add(new MapAreaName(currentMap, id, at.X, at.Y, at.Z));
				else
					pending.Add(id); // placed by the next move on this playfield
			}

			return results;
		}

		// Aggregates the raw sightings into one MapArea per (map, areaNameId): the bounds hull over every position
		// that banner was seen firing at.
		//
		// NOT a vote. The old Merge picked ONE id per map by majority, because Map.AreaNameId could hold only one -
		// which silently discarded five of Old Tarantia's six areas. Every distinct id is a distinct area and they
		// all survive here.
		//
		// The hull is an interim shape, not the retail volume: it can only cover ground a captured player actually
		// walked. With one sighting it is a POINT (MinX == MaxX), which is how a degenerate row is spotted without a
		// second column that can disagree with the bounds; the caller pairs it with a radius rather than treating it
		// as a hard polygon. It tightens as more marked dumps land, and #679's client-recovered rows replace it
		// outright wherever the region's own name is the banner text.
		public static List<MapArea> MergeAreas(IEnumerable<MapAreaName> all)
		{
			var byArea = new Dictionary<(uint Map, uint Area), MapArea>();
			foreach (var s in all)
			{
				if (!byArea.TryGetValue((s.MapId, s.AreaNameId), out var area))
				{
					byArea[(s.MapId, s.AreaNameId)] = new MapArea
					{
						MapId = s.MapId,
						AreaNameId = s.AreaNameId,
						MinX = s.X,
						MaxX = s.X,
						MinY = s.Y,
						MaxY = s.Y,
						MinZ = s.Z,
						MaxZ = s.Z,
						Source = AreaBannerSource.Captured
					};
					continue;
				}

				area.MinX = Math.Min(area.MinX, s.X);
				area.MaxX = Math.Max(area.MaxX, s.X);
				area.MinY = Math.Min(area.MinY, s.Y);
				area.MaxY = Math.Max(area.MaxY, s.Y);
				area.MinZ = Math.Min(area.MinZ, s.Z);
				area.MaxZ = Math.Max(area.MaxZ, s.Z);
			}

			return byArea.Values.ToList();
		}




	}
}
