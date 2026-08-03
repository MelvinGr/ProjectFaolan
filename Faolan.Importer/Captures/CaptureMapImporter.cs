using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Discovers maps from captured MapChange packets (0x2000 / sub-opcode 0x5D85BFC7) and INSERTS any map not
	// already in the DB. Existing rows (curated names/positions) are left untouched EXCEPT for the three PER-MAP rule
	// words - PlayfieldFlags (the mount-permission bit), PlayfieldFieldA and PlayfieldFieldC (#451). Those ARE
	// refreshed from the capture on every map, new or existing, because each is stable per playfield: across the 151
	// captured MapChange records every record for a given map carries the same three, with no exceptions. The
	// hand-curated value was wrong for un-curated maps (e.g. Tarantia Noble District read as no-mount), and FieldA /
	// FieldC were builder constants, wrong for six of the twelve captured maps.
	// The OTHER Playfield* fields are deliberately NOT imported: the token and fieldB really are session/entry-volatile
	// (a capture shows many distinct values for the SAME map), so they stay curated.
	// Takes raw on-wire packet bytes (no pcap dependency).
	public static class CaptureMapImporter
	{
		private const uint MapChangeSubOpcode = PlayfieldFCodec.Opcode;

		public static async Task<(int Inserted, int Reflagged)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			// mapId -> the three per-map rule words (each null when the body was too short to carry it).
			var found = new Dictionary<uint, PlayfieldFIIR>();
			foreach (var packet in rawPackets)
				TryReadMapChange(packet, found);

			if (found.Count == 0)
				return (0, 0);

			var ids = found.Keys.ToList();
			var existing = await db.Maps.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

			var inserted = 0;
			var reflagged = 0;
			// A Map carries no position (it's the coordinate space) - the scan only discovers which map ids exist and
			// their per-area rule words. The per-map default spawn lives in RespawnPoints now.
			foreach (var (id, mc) in found)
			{
				if (existing.TryGetValue(id, out var map))
				{
					var before = (map.PlayfieldFlags, map.PlayfieldFieldA, map.PlayfieldFieldC);
					map.PlayfieldFlags = mc.Flags;
					map.PlayfieldFieldA = mc.FieldA;
					map.PlayfieldFieldC = mc.FieldC;
					if (before != (map.PlayfieldFlags, map.PlayfieldFieldA, map.PlayfieldFieldC))
						reflagged++;

					continue;
				}

				// Every word comes from a record that parsed WHOLE (#530 rule 4 - PlayfieldFCodec.Parse throws rather
				// than reporting a half-read body), so there is no "the capture did not carry it" case any more: a
				// zero FieldC is a REAL value on four maps and now only ever arrives as one.
				var row = new Map
				{
					Id = id,
					Name = string.Empty,
					PlayfieldFlags = mc.Flags,
					PlayfieldFieldA = mc.FieldA,
					PlayfieldFieldC = mc.FieldC
				};
				db.Maps.Add(row);
				inserted++;
			}

			if (inserted > 0 || reflagged > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureMapImporter: {Seen} maps seen, {Inserted} new inserted, {Reflagged} with per-map rule words updated.", found.Count, inserted, reflagged);
			return (inserted, reflagged);
		}

		// The MapChange inner body, read WHOLE by PlayfieldFCodec (the map id and the per-map rule words live in the
		// embedded PlayfieldProxy and the trailer). Keeps the LAST seen record per map; it's stable across captures.
		private static void TryReadMapChange(byte[] packetBytes, IDictionary<uint, PlayfieldFIIR> into)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Method != (uint)GameAgentMethods.PassBlob)
					return;

				var inner = packet.BodyReader().ReadArrayUInt32Length();
				var mc = PlayfieldFCodec.Parse(inner);
				into[mc.MapId] = mc;
			}
			catch
			{
				// not a parseable MapChange packet
			}
		}
	}
}
