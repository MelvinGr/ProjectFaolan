using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Harvests damage-over-time (DoT) effects from captured StatDamageIIR records and writes them onto the Spell
	///     table as Spell.DotDelta / Spell.DotType and Spell.DotIntervalMs.
	/// </summary>
	/// <remarks>
	///     StatDamageIIR is the health-effect carrier: it carries the signed per-tick delta, the damage-school flag and
	///     the base spell id (wire id >> 8, which is Spell.Id). A bleed debuff fires it once per tick, so the same
	///     (spell -> delta) recurs; the most common delta per spell wins. Only spells already in the Spell table are
	///     updated (the base ids are RDB-cataloged). Idempotent.
	///     The record carries no period, so the interval is measured rather than read: consecutive ticks of one spell on
	///     one target are one tick train, and the gap between them is that spell's interval. The judgement about which
	///     gaps establish an interval lives in <see cref="DotIntervalEstimator" />; this class only builds the trains.
	///     A train is scoped to one capture. The key includes the target and caster entity handles, and handles are
	///     session-volatile and recycled - the same handle in two captures is two different creatures, so pooling ticks
	///     across captures would splice unrelated trains and invent gaps that were never observed. Pooling the resulting
	///     gaps across captures is safe: a gap is a duration.
	/// </remarks>
	public static class CaptureStatDamageImporter
	{
		private const uint StatDamageSubOpcode = StatDamageCodec.Opcode;

		private const long TicksPerSecond = 10_000_000;

		public static async Task<(int Spells, int Updated, int Intervals)> ImportAsync(IDatabaseContext db, IReadOnlyList<IReadOnlyList<CapturedPacket>> captures,
			ILogger? logger = null)
		{
			// spellId -> (delta -> count), (type -> count), so the dominant reading can be picked.
			var deltas = new Dictionary<uint, Dictionary<int, int>>();
			var types = new Dictionary<uint, Dictionary<uint, int>>();

			// spellId -> every observed inter-tick gap, in seconds, pooled across captures.
			var gaps = new Dictionary<uint, List<double>>();

			var seen = 0;
			foreach (var capture in captures)
			{
				// (spell, target, caster) -> the tick times of that one train, within this capture only.
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

				// Only write an interval the gaps actually establish. A spell whose train was too short or too ragged
				// keeps whatever it already had - a weak measurement must never overwrite a good one, and must never
				// clear one back to the fallback.
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
				if (parsed.Method != (uint)GameAgentMethods.PassBlob)
					return;

				var inner = parsed.BodyReader().ReadArrayUInt32Length();
				if (inner.Length < 65 || inner.ReadU32Be(0) != StatDamageSubOpcode)
					return;

				seen++;

				var dmg = StatDamageCodec.Parse(inner);
				if (dmg.SpellBaseId == 0 || dmg.Delta == 0)
					return;

				deltas.TryAdd(dmg.SpellBaseId, []);
				deltas[dmg.SpellBaseId][dmg.Delta] = deltas[dmg.SpellBaseId].GetValueOrDefault(dmg.Delta) + 1;
				types.TryAdd(dmg.SpellBaseId, []);
				types[dmg.SpellBaseId][(uint)dmg.School] = types[dmg.SpellBaseId].GetValueOrDefault((uint)dmg.School) + 1;

				var key = (dmg.SpellBaseId, dmg.Handle, dmg.Caster);
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
