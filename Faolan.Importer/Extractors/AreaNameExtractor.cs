using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

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
		private const uint FeedbackSubOpcode = (uint)GameServer0X2000Opcodes.FormatFeedbackIIR; // 0x8D5A8B6B
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR; // 0x5D85BFC7
		private const uint MoveSubOpcode = (uint)GameServer0X2000Opcodes.CharDCMoveIIR; // 0x4517AA81
		private const uint Opcode2000 = 0x2000;
		private static readonly Regex AreaId = new("GenericDisplayAreaName[|/](\\d+)", RegexOptions.Compiled);

		public static List<MapAreaName> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var results = new List<MapAreaName>();
			uint currentMap = 0;
			(float X, float Y, float Z)? pos = null;

			foreach (var p in packets)
			{
				// NB both directions: the banner is s2c but the POSITION comes from the client's own c2s move, so
				// this can't filter to downloads the way it did when it only read banners.
				if (p.Opcode != Opcode2000)
					continue;

				var body = WorldObjectExtractor.GetBody(p.Bytes);
				if (body.Length < 30)
					continue; // a CharDCMove body is shorter than the 38 a MapChange needs; each check re-validates its own length

				if (p.SubOpcode == MapChangeSubOpcode && p.IsDownload && body.Length >= 38 && MapChangeCodec.TryParse(body.AsSpan(4), out var mc))
				{
					var map = mc.MapId;
					if (map != currentMap)
					{
						currentMap = map;
						pos = null; // a stale position from the previous playfield would place the banner nowhere
					}

					continue;
				}

				if (p.SubOpcode == MoveSubOpcode && !p.IsDownload && TryReadMovePosition(body, out var moved))
				{
					pos = moved;
					continue;
				}

				if (currentMap == 0 || !p.IsDownload || p.SubOpcode != FeedbackSubOpcode)
					continue;

				var m = AreaId.Match(Encoding.Latin1.GetString(body));
				if (!m.Success || pos is not { } at)
					continue; // no position yet (the entry banner precedes the first move) - can't place it

				results.Add(new MapAreaName(currentMap, uint.Parse(m.Groups[1].Value), at.X, at.Y, at.Z));
			}

			return results;
		}

		// Aggregates the raw sightings into one MapArea per (map, areaNameId): the bounds hull over every position
		// that banner was seen firing at, plus how many sightings back it.
		//
		// NOT a vote. The old Merge picked ONE id per map by majority, because Map.AreaNameId could hold only one -
		// which silently discarded five of Old Tarantia's six areas. Every distinct id is a distinct area and they
		// all survive here.
		//
		// The hull is an interim shape, not the retail volume: it can only cover ground a captured player actually
		// walked. With one sighting it is a point; the caller pairs it with a radius rather than treating it as a
		// hard polygon. It tightens as more marked dumps land.
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
						SampleCount = 1
					};
					continue;
				}

				area.MinX = Math.Min(area.MinX, s.X);
				area.MaxX = Math.Max(area.MaxX, s.X);
				area.MinY = Math.Min(area.MinY, s.Y);
				area.MaxY = Math.Max(area.MaxY, s.Y);
				area.MinZ = Math.Min(area.MinZ, s.Z);
				area.MaxZ = Math.Max(area.MaxZ, s.Z);
				area.SampleCount++;
			}

			return byArea.Values.ToList();
		}

		private static float ReadFloat(byte[] b, int at)
		{
			return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at, 4)));
		}

		// CharDCMoveIIR body: [u32 subop][u32 0xC350][u32 handle][u8 env][u8 flag1][float x][float y][float z] -
		// the position is ALWAYS present and always first (docs/re/chardcmove-grammar.md). Raw-body floats are
		// big-endian. A C2S move is the client reporting its OWN position, so no handle check is needed.
		private static bool TryReadMovePosition(byte[] body, out (float X, float Y, float Z) pos)
		{
			pos = default;
			var at = 18; // 4 subop + 4 C350 + 4 handle + 1 env + 1 flag1, after the 4-byte length word
			if (body.Length < at + 12)
				return false;

			pos = (ReadFloat(body, at), ReadFloat(body, at + 4), ReadFloat(body, at + 8));
			return true;
		}
	}
}
