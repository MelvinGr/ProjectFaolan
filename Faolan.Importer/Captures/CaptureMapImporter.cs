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
	/// <summary>
	///     Discovers maps from captured MapChange packets (0x2000 / sub-opcode 0x5D85BFC7) and inserts any map not
	///     already in the DB. Takes raw on-wire packet bytes (no pcap dependency).
	/// </summary>
	/// <remarks>
	///     Existing rows (curated names/positions) are left untouched except for the three per-map rule words:
	///     PlayfieldFlags (the mount-permission bit), PlayfieldFieldA and PlayfieldFieldC. Those are refreshed from the
	///     capture on every map, new or existing, because each is stable per playfield: every captured MapChange record
	///     for a given map carries the same three. The other Playfield* fields are deliberately not imported: the token
	///     and the blob's last word are session/entry-volatile (a capture shows many distinct values for the same map),
	///     so they stay curated.
	/// </remarks>
	public static class CaptureMapImporter
	{
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
			// A Map carries no position (it is the coordinate space) - the scan only discovers which map ids exist and
			// their per-map rule words. The per-map default spawn lives in RespawnPoints.
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

				// Every word comes from a record that parsed whole (PlayfieldFCodec.Parse throws rather than reporting a
				// half-read body), so there is no "the capture did not carry it" case: a zero FieldC is a real value.
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

		// The MapChange inner body, read whole by PlayfieldFCodec (the map id and the per-map rule words live in the
		// embedded PlayfieldProxy and the trailer). Keeps the last seen record per map; it is stable across captures.
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
