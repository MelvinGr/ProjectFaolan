using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests damage-over-time (DoT) effects from captured StatDamageIIR (0xE9F24184) records and writes them onto
	// the Spell table as Spell.DotDelta / Spell.DotType (#274) and Spell.DotIntervalMs (#437). StatDamageIIR is the
	// health-effect carrier: its affected stat (inner +29) is 0x1B = HealthCurrent, inner +37 is the signed per-tick
	// delta, inner +41 the damage-school flag, inner +61 the BASE spell id (wire id >> 8, == Spell.Id). A bleed debuff
	// fires it once per tick, so the same (spell -> delta) recurs; we take the most common delta per spell. Only
	// spells already in the Spell table are updated (the base ids are RDB-cataloged). Idempotent.
	//
	// The record carries no period, so the INTERVAL is measured rather than read: consecutive ticks of one spell on
	// one target are one tick train, and the gap between them IS that spell's interval. The judgement about which
	// gaps establish an interval lives in DotIntervalEstimator; this class only has to build the trains correctly.
	//
	// ⚠️ A train is scoped to ONE capture. The key includes the target and caster entity handles, and handles are
	// SESSION-VOLATILE and recycled - the same handle in two captures is two different creatures, so pooling ticks
	// across captures would splice unrelated trains and invent gaps that were never observed. Pooling the resulting
	// GAPS across captures is safe and is what gives most spells their sample count: a gap is a duration.
	public static class CaptureStatDamageImporter
	{
		private const uint StatDamageSubOpcode = (uint)GameServer0X2000Opcodes.StatDamageIIR;

		private const long TicksPerSecond = 10_000_000;

		public static async Task<(int Spells, int Updated, int Intervals)> ImportAsync(IDatabaseContext db, IReadOnlyList<IReadOnlyList<CapturedPacket>> captures,
			ILogger? logger = null)
		{
			// spellId -> (delta -> count), (type -> count) so we pick the dominant reading.
			var deltas = new Dictionary<uint, Dictionary<int, int>>();
			var types = new Dictionary<uint, Dictionary<uint, int>>();

			// spellId -> every observed inter-tick gap, in seconds, pooled across captures.
			var gaps = new Dictionary<uint, List<double>>();

			var seen = 0;
			foreach (var capture in captures)
			{
				// (spell, target, caster) -> the tick times of that ONE train, within THIS capture only.
				var trains = new Dictionary<(uint Spell, uint Target, uint Caster), List<long>>();
				foreach (var packet in capture)
					TryRead(packet, deltas, types, trains, ref seen);

				foreach (var (key, times) in trains)
				{
					if (times.Count < 2)
						continue;

					times.Sort();
					gaps.TryAdd(key.Spell, []);
					for (var i = 1; i < times.Count; i++)
						gaps[key.Spell].Add((times[i] - times[i - 1]) / (double)TicksPerSecond);
				}
			}

			if (deltas.Count == 0)
				return (0, 0, 0);

			var ids = deltas.Keys.ToList();
			var byId = (await db.Spells.Where(s => ids.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id);

			var updated = 0;
			var intervals = 0;
			foreach (var (spellId, deltaCounts) in deltas)
			{
				if (!byId.TryGetValue(spellId, out var spell))
					continue; // base id not cataloged (no RDB record) - skip

				var delta = deltaCounts.OrderByDescending(kv => kv.Value).First().Key;
				var type = types[spellId].OrderByDescending(kv => kv.Value).First().Key;
				var changed = false;
				if (spell.DotDelta != delta || spell.DotType != type)
				{
					spell.DotDelta = delta;
					spell.DotType = type;
					changed = true;
				}

				// Only WRITE an interval the gaps actually establish. A spell whose train was too short or too ragged
				// keeps whatever it already had - a weak measurement must never overwrite a good one, and must never
				// clear one back to the 2 s fallback.
				if (gaps.TryGetValue(spellId, out var g) && DotIntervalEstimator.Estimate(g) is { } ms && spell.DotIntervalMs != ms)
				{
					spell.DotIntervalMs = ms;
					intervals++;
					changed = true;
				}

				if (changed)
					updated++;
			}

			if (updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureStatDamageImporter: {Spells} DoT spells seen, {Updated} written, {Intervals} intervals measured.", deltas.Count, updated, intervals);
			return (deltas.Count, updated, intervals);
		}

		private static void TryRead(CapturedPacket packet, Dictionary<uint, Dictionary<int, int>> deltas, Dictionary<uint, Dictionary<uint, int>> types,
			Dictionary<(uint Spell, uint Target, uint Caster), List<long>> trains, ref int seen)
		{
			try
			{
				var parsed = Rpc.Parse(packet.Bytes);
				if (parsed.Opcode != (ushort)GameAgentOpcodes.PassBlob)
					return;

				var inner = parsed.BodyReader().ReadArrayUInt32Length();
				if (inner.Length < 65 || inner.ReadU32Be(0) != StatDamageSubOpcode)
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
				types[dmg.SpellBaseId][(uint)dmg.School] = types[dmg.SpellBaseId].GetValueOrDefault((uint)dmg.School) + 1;

				var key = (dmg.SpellBaseId, dmg.Target, dmg.Caster);
				trains.TryAdd(key, []);
				trains[key].Add(packet.Ticks);
			}
			catch
			{
				// not a parseable StatDamageIIR packet
			}
		}
	}
}
