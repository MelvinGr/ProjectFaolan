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
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;

		public static async Task<(int Inserted, int Reflagged)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			// mapId -> the three per-map rule words (each null when the body was too short to carry it).
			var found = new Dictionary<uint, MapChangeCodec.Parsed>();
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
					map.PlayfieldFlags = mc.Flags ?? map.PlayfieldFlags;
					map.PlayfieldFieldA = mc.FieldA ?? map.PlayfieldFieldA;
					map.PlayfieldFieldC = mc.FieldC ?? map.PlayfieldFieldC;
					if (before != (map.PlayfieldFlags, map.PlayfieldFieldA, map.PlayfieldFieldC))
						reflagged++;

					continue;
				}

				// A brand-new map keeps the entity defaults for any word the capture did not carry, rather than
				// zeroing it - a zero FieldC is a REAL value on four maps, so it must not double as "unknown".
				var row = new Map { Id = id, Name = string.Empty };
				row.PlayfieldFlags = mc.Flags ?? row.PlayfieldFlags;
				row.PlayfieldFieldA = mc.FieldA ?? row.PlayfieldFieldA;
				row.PlayfieldFieldC = mc.FieldC ?? row.PlayfieldFieldC;
				db.Maps.Add(row);
				inserted++;
			}

			if (inserted > 0 || reflagged > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureMapImporter: {Seen} maps seen, {Inserted} new inserted, {Reflagged} with per-map rule words updated.", found.Count, inserted, reflagged);
			return (inserted, reflagged);
		}

		// MapChange inner (after the body's u32 length prefix): [subop4][0][0][byte][posX,posY,posZ BE floats]
		// [0x62][0xC79C][mapId4]...[flags4]. Offsets (inner frame): subop@0, mapId@30, flags@103 (the per-area rule
		// word - beach/interior 0, mountable city 1). Keeps the LAST seen flags per map; it's stable across captures.
		private static void TryReadMapChange(byte[] packetBytes, IDictionary<uint, MapChangeCodec.Parsed> into)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.BodyReader().ReadArrayUInt32Length();
				if (!MapChangeCodec.TryParse(inner, out var mc))
					return;

				// Prefer a packet that actually carried the trailer; don't let a later short packet blank a known value.
				if (mc.Flags is not null || !into.ContainsKey(mc.MapId))
					into[mc.MapId] = mc;
			}
			catch
			{
				// not a parseable MapChange packet
			}
		}
	}
}
