using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Network.Constants;

namespace Faolan.Importer.Extractors
{
	// A harvested map area name: the client localized-string id of the top-of-screen FIFO banner the server shows on
	// ENTERING a playfield (#348). Keyed by map so it maps onto the Map row.
	public sealed record MapAreaName(uint MapId, uint AreaNameId);

	// Harvests each playfield's primary area name from captured gameplay: after a MapChange (PlayfieldFIIR 0x5D85BFC7),
	// the server sends a FormatFeedbackIIR (0x8D5A8B6B) FIFO banner whose remoteformat carries
	// `GenericDisplayAreaName|<textId>` - the localized area-name string id. We take the FIRST banner after each map
	// entry (the entry area; the spawn is fixed, so it's consistent per map - e.g. Old Tarantia 2000 -> 3373522 on every
	// entry, despite the playfield having several sub-areas). Sub-region boundary crossings (the other area names) need
	// per-map trigger geometry and are a follow-up.
	public static class AreaNameExtractor
	{
		private const uint Opcode2000 = 0x2000;
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;   // 0x5D85BFC7
		private const uint FeedbackSubOpcode = (uint)GameServer0X2000Opcodes.FormatFeedbackIIR; // 0x8D5A8B6B
		private static readonly Regex AreaId = new("GenericDisplayAreaName[|/](\\d+)", RegexOptions.Compiled);

		public static List<MapAreaName> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var results = new List<MapAreaName>();
			uint currentMap = 0;
			var haveBannerForMap = false;

			foreach (var p in packets)
			{
				if (p.Opcode != Opcode2000 || !p.IsDownload)
					continue;

				var body = WorldObjectExtractor.GetBody(p.Bytes);
				if (body.Length < 38)
					continue;

				if (p.SubOpcode == MapChangeSubOpcode)
				{
					var map = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					if (map != currentMap)
					{
						currentMap = map;
						haveBannerForMap = false; // only the FIRST area banner after this entry is the entry area
					}

					continue;
				}

				if (currentMap == 0 || haveBannerForMap || p.SubOpcode != FeedbackSubOpcode)
					continue;

				var m = AreaId.Match(Encoding.Latin1.GetString(body));
				if (!m.Success)
					continue;

				results.Add(new MapAreaName(currentMap, uint.Parse(m.Groups[1].Value)));
				haveBannerForMap = true;
			}

			return results;
		}

		// One area-name id per map: the majority across captures (consistent in the corpus; a defensive tie-break).
		public static List<MapAreaName> Merge(IEnumerable<MapAreaName> all)
		{
			var votes = new Dictionary<uint, Dictionary<uint, int>>();
			foreach (var a in all)
			{
				if (!votes.TryGetValue(a.MapId, out var byId))
					votes[a.MapId] = byId = new Dictionary<uint, int>();
				byId[a.AreaNameId] = byId.GetValueOrDefault(a.AreaNameId) + 1;
			}

			var merged = new List<MapAreaName>();
			foreach (var (mapId, byId) in votes)
			{
				var best = 0u;
				var bestVotes = -1;
				foreach (var (id, n) in byId)
				{
					if (n > bestVotes)
					{
						bestVotes = n;
						best = id;
					}
				}

				merged.Add(new MapAreaName(mapId, best));
			}

			return merged;
		}
	}
}
