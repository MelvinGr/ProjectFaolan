using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Discovers maps from captured MapChange packets (0x2000 / sub-opcode 0x5D85BFC7) and INSERTS any map not
	// already in the DB, with the captured spawn position. Existing rows (curated names/positions) are left
	// untouched. The position is the character's position at map change - a representative spawn, not a canonical
	// origin - so it is only used to seed brand-new map rows. Takes raw on-wire packet bytes (no pcap dependency).
	public static class CaptureMapImporter
	{
		private const uint MapChangeSubOpcode = 0x5D85BFC7;

		public static async Task<int> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var found = new Dictionary<uint, (float X, float Y, float Z)>();
			foreach (var packet in rawPackets)
				TryReadMapChange(packet, found);

			if (found.Count == 0)
				return 0;

			var ids = found.Keys.ToList();
			var existing = await db.Maps.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync();
			var existingSet = existing.ToHashSet();

			var inserted = 0;
			// A Map carries no position (it's the coordinate space); the MapChange spawn position scanned into `found`
			// is only used to discover which map ids exist. The per-map default spawn lives in RespawnPoints now.
			foreach (var id in found.Keys)
			{
				if (existingSet.Contains(id))
					continue;

				db.Maps.Add(new Map
				{
					Id = id,
					Name = string.Empty
				});
				inserted++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureMapImporter: {Seen} maps seen in captures, {Inserted} new inserted.", found.Count, inserted);
			return inserted;
		}

		// MapChange inner (after the body's u32 length prefix): [subop4][0][0][byte][posX,posY,posZ BE floats]
		// [0x62][0xC79C][mapId4]. Offsets: subop@0, pos@13, mapId@30.
		private static void TryReadMapChange(byte[] packetBytes, IDictionary<uint, (float, float, float)> into)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
				if (inner.Length < 34 || BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(0, 4)) != MapChangeSubOpcode)
					return;

				var x = BinaryPrimitives.ReadSingleBigEndian(inner.AsSpan(13, 4));
				var y = BinaryPrimitives.ReadSingleBigEndian(inner.AsSpan(17, 4));
				var z = BinaryPrimitives.ReadSingleBigEndian(inner.AsSpan(21, 4));
				var mapId = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(30, 4));

				into[mapId] = (x, y, z);
			}
			catch
			{
				// not a parseable MapChange packet
			}
		}
	}
}
