using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests damage-over-time (DoT) effects from captured StatDamageIIR (0xE9F24184) records and writes them onto
	// the Spell table as Spell.DotDelta / Spell.DotType (#274). StatDamageIIR is the health-effect carrier: its
	// affected stat (inner +29) is 0x1B = HealthCurrent, inner +37 is the signed per-tick delta, inner +41 the
	// damage-school flag, inner +61 the BASE spell id (wire id >> 8, == Spell.Id). A bleed debuff fires it once per
	// tick, so the same (spell -> delta) recurs; we take the most common delta per spell. Only spells already in the
	// Spell table are updated (the base ids are RDB-cataloged). Idempotent.
	public static class CaptureStatDamageImporter
	{
		private const uint StatDamageSubOpcode = 0xE9F24184;

		public static async Task<(int Spells, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			// spellId -> (delta -> count), (type -> count) so we pick the dominant reading.
			var deltas = new Dictionary<uint, Dictionary<int, int>>();
			var types = new Dictionary<uint, Dictionary<uint, int>>();

			var seen = 0;
			foreach (var packet in rawPackets)
				TryRead(packet, deltas, types, ref seen);

			if (deltas.Count == 0)
				return (0, 0);

			var ids = deltas.Keys.ToList();
			var byId = (await db.Spells.Where(s => ids.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id);

			var updated = 0;
			foreach (var (spellId, deltaCounts) in deltas)
			{
				if (!byId.TryGetValue(spellId, out var spell))
					continue; // base id not cataloged (no RDB record) - skip

				var delta = deltaCounts.OrderByDescending(kv => kv.Value).First().Key;
				var type = types[spellId].OrderByDescending(kv => kv.Value).First().Key;
				if (spell.DotDelta == delta && spell.DotType == type)
					continue;

				spell.DotDelta = delta;
				spell.DotType = type;
				updated++;
			}

			if (updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureStatDamageImporter: {Spells} DoT spells seen, {Updated} written.", deltas.Count, updated);
			return (deltas.Count, updated);
		}

		private static void TryRead(byte[] packetBytes, Dictionary<uint, Dictionary<int, int>> deltas, Dictionary<uint, Dictionary<uint, int>> types, ref int seen)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
				if (inner.Length < 65 || BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(0, 4)) != StatDamageSubOpcode)
					return;

				seen++;

				var delta = BinaryPrimitives.ReadInt32BigEndian(inner.AsSpan(37, 4));
				var type = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(41, 4));
				var spellId = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(61, 4));
				if (spellId == 0 || delta == 0)
					return;

				deltas.TryAdd(spellId, new Dictionary<int, int>());
				deltas[spellId][delta] = deltas[spellId].GetValueOrDefault(delta) + 1;
				types.TryAdd(spellId, new Dictionary<uint, int>());
				types[spellId][type] = types[spellId].GetValueOrDefault(type) + 1;
			}
			catch
			{
				// not a parseable StatDamageIIR packet
			}
		}
	}
}
