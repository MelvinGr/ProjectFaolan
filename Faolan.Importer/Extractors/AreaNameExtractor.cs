using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// A harvested map area name: the client localized-string id of the top-of-screen FIFO banner, plus the player
	// position it fired at (#348). One SIGHTING - a map has many areas and each fires repeatedly, so these are
	// aggregated into MapArea rows by MergeAreas.
	public sealed record MapAreaName(uint MapId, uint AreaNameId, float X, float Y, float Z);

	// One banner the wire carried on one playfield, with the bounds of every position it was seen firing at. Never
	// stored - see Merge.
	public sealed record CapturedBanner(uint MapId, uint AreaNameId, float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
	{
		// A hull over ONE sighting is a point: it says where the banner fired once, not where its region is.
		public bool SingleSighting => MinX == MaxX && MinZ == MaxZ;

		public float CentreX => (MinX + MaxX) / 2f;
		public float CentreZ => (MinZ + MaxZ) / 2f;
	}

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

		// The distinct banners the wire was seen carrying, each with the bounds of every position it fired at.
		//
		// Both halves are DERIVATION INPUTS since #680, and nothing here is stored. The (playfield, id) pair is the
		// valuable one: it PINS a playfield's id numbering, which is what lets an ambiguous name resolve and what a
		// name match far from that numbering is checked against. The hull is the weak one, and is only still built
		// because it is the sole route left for a banner whose region carries an internal name - Old Tarantia's
		// volumes are `BA_Bridge`, never the "Bridge of Vilerus I" the banner renders.
		//
		// ⚠️ It used to BE the region data, as a `MapArea` row the runtime tested with a slack radius. That was an
		// approximation by construction - it can only cover ground a captured player actually walked, and with one
		// sighting it is a point - and it is the weakest evidence in the subsystem: a banner fires ON ENTRY, so its
		// sample sits on the boundary, and a one-sighting hull is placed by whichever move happened to bracket the
		// banner. On map 1010 that demonstrably put two different banners in one volume, 600 units from where one of
		// them belongs.
		public static List<CapturedBanner> Merge(IEnumerable<MapAreaName> all)
		{
			var byArea = new Dictionary<(uint Map, uint Area), CapturedBanner>();
			foreach (var s in all)
			{
				if (!byArea.TryGetValue((s.MapId, s.AreaNameId), out var banner))
				{
					byArea[(s.MapId, s.AreaNameId)] = new CapturedBanner(s.MapId, s.AreaNameId, s.X, s.X, s.Y, s.Y, s.Z, s.Z);
					continue;
				}

				byArea[(s.MapId, s.AreaNameId)] = banner with
				{
					MinX = Math.Min(banner.MinX, s.X),
					MaxX = Math.Max(banner.MaxX, s.X),
					MinY = Math.Min(banner.MinY, s.Y),
					MaxY = Math.Max(banner.MaxY, s.Y),
					MinZ = Math.Min(banner.MinZ, s.Z),
					MaxZ = Math.Max(banner.MaxZ, s.Z)
				};
			}

			return byArea.Values.OrderBy(b => b.MapId).ThenBy(b => b.AreaNameId).ToList();
		}
	}
}
