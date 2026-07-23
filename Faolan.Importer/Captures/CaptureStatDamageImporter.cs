using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
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
		private const uint StatDamageSubOpcode = (uint)GameServer0X2000Opcodes.StatDamageIIR;

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

				// The StatDamageIIR content is exactly what CombatEffectBuilder.StatDamage models (delta@37 / statId@41
				// / spellBaseId@61) - parse it typed (StatDamage) instead of re-reading those three offsets by hand.
				var dmg = StatDamage.Read(new FaolanStream(inner));
				if (dmg.SpellBaseId == 0 || dmg.Delta == 0)
					return;

				deltas.TryAdd(dmg.SpellBaseId, []);
				deltas[dmg.SpellBaseId][dmg.Delta] = deltas[dmg.SpellBaseId].GetValueOrDefault(dmg.Delta) + 1;
				types.TryAdd(dmg.SpellBaseId, []);
				types[dmg.SpellBaseId][dmg.StatId] = types[dmg.SpellBaseId].GetValueOrDefault(dmg.StatId) + 1;
			}
			catch
			{
				// not a parseable StatDamageIIR packet
			}
		}
	}
}
