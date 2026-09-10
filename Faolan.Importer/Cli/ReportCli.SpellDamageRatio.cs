using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-damage-ratio` (#924): every captured spell hit against what the spell's own template STATES for
	// the caster's level, as a ratio - and, the question the ticket actually turns on, **how many distinct CASTERS the
	// corpus holds**.
	//
	// ⭐ `StatDamageIIR` names the spell that caused it (`SpellBaseId`), so a health hit can be joined to the spell
	// catalog rather than aggregated by damage SCHOOL, which is what hid the split. Done per spell, the corpus divides
	// cleanly: at Tortage levels the stated hit IS the damage (ratio 1.00), and at level 80 it is ~4.7x.
	//
	// ⚠️ **The ratio cannot be turned into a rule from this corpus, and the last column is why.** A multiplier that is
	// per-CASTER cannot be separated from one that is per-LEVEL while every level-80 hit comes from one character, and
	// its inputs never travel: across every level-80 capture the only caster stat pushed is `MagicRating`. So this
	// report exists to be RE-RUN - the moment a second geared caster is harvested, the caster column moves and the
	// separation becomes possible. Recording "one caster" as a fact in prose is what goes stale.
	public static partial class ReportCli
	{
		// The effect kinds whose Amount is an instant, FLAT move of a pool at the wire scale - the same set
		// `SpellPoolEffects.FlatKinds` applies. Anything else states its magnitude in other units.
		private static readonly uint[] FlatDamageKinds = [53002, 53013, 53219];

		private static async Task SpellDamageRatio(IReadOnlyList<NamedCapture> captures)
		{
			Console.WriteLine("=== captured spell hits against what the spell STATES (#924) ===\n");

			// The catalog's own stated pool move, per spell, as a level RAMP. Negative amounts are damage; the wire
			// carries hundredths, so a stated -17600 is a displayed 176.
			Dictionary<uint, StatedRamp> stated;
			Dictionary<uint, string> names;
			await using (var db = OpenDb())
			{
				stated = (await db.SpellStatEffects.AsNoTracking()
						// ⛔ FLAT kinds only. `HitPercentage` (53007) states a percent OF THE TARGET'S POOL, not a
						// magnitude - so its "stated" number is 1, 2 or 5 and the observed hit is 1%, 2% or 5% of
						// whatever was hit. A first cut of this included them and read the resulting quotients as a
						// per-caster constant: 6341 for the necromancer, 6126 and 5949 for the demonologist. Those are
						// TARGET HEALTH POOLS (634,100 / 612,600 / 594,900), and they looked like exactly the finding
						// the ticket is after - two casters, same level, different constants, one of them moving
						// between sessions. See SpellPoolEffects for the kinds.
						.Where(e => e.StatId == CharacterStatId.HealthCurrent && e.Amount < 0 && FlatDamageKinds.Contains(e.EffectKind))
						.Select(e => new { e.SpellId, e.Amount, e.HighAmount, e.MaxAmount, e.MaxHighAmount, e.LowLevel, e.HighLevel, e.PeriodCentiseconds })
						.ToListAsync())
					.GroupBy(e => e.SpellId)
					.ToDictionary(g => g.Key, g =>
					{
						// ⚠️ The INSTANT row, not the first one. A spell that both hits and ticks carries the same
						// magnitudes twice, distinguished only by `PeriodCentiseconds` - taking either indifferently
						// compares a direct hit against a DoT's per-tick number.
						var e = g.FirstOrDefault(r => r.PeriodCentiseconds == 0) ?? g.First();

						return new StatedRamp(e.Amount, e.HighAmount ?? e.Amount, e.MaxAmount ?? e.Amount,
							e.MaxHighAmount ?? e.HighAmount ?? e.Amount, e.LowLevel ?? 0, e.HighLevel ?? 0);
					});

				names = await db.Spells.AsNoTracking().Where(s => s.Name != null).ToDictionaryAsync(s => s.Id, s => s.Name!);
			}

			// One row per (capture, caster handle, spell): the caster is what the separation turns on, so it is a
			// grouping key and never averaged away.
			var rows = new List<(string Capture, uint Caster, bool IsPlayer, uint PlayerLevel, uint SpellId, List<int> Hits)>();

			foreach (var capture in captures)
			{
				var playerHandles = new HashSet<uint>();
				var playerLevel = 0u;
				var perCasterSpell = new Dictionary<(uint Caster, uint Spell), List<int>>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true })
						continue;

					if (p.SubOpcode == FullPlayerCharacterCodec.Opcode && p.Record.Length >= 12)
					{
						playerHandles.Add(p.Record.ReadU32Be(8));
						var sheet = SubClassStatBlock.ExtractFromSnapshot(p.Record);
						var lvl = sheet?.Find(kv => kv.Id == (uint)CharacterStatId.Level).Value ?? 0;
						if (lvl is > 0 and <= 80 && playerLevel == 0)
							playerLevel = lvl;
					}
					else if (p.SubOpcode == StatCodec.Opcode && p.TryParse(StatCodec.Parse, out var stat)
					         && stat.StatId == (uint)CharacterStatId.Level && playerHandles.Contains(stat.Handle) && stat.Value is > 0 and <= 80)
					{
						playerLevel = stat.Value;
					}
					else if (p is { IsDownload: true } && p.SubOpcode == StatDamageCodec.Opcode && p.Body.Length > 4)
					{
						StatDamageIIR hit;
						try
						{
							hit = StatDamageCodec.Parse(p.Body.AsSpan(4));
						}
						catch
						{
							continue; // a truncated record - counted by its absence, not by a wrong number
						}

						if (hit.SpellBaseId == 0 || hit.StatId != (uint)CharacterStatId.HealthCurrent || hit.Delta >= 0)
							continue;

						var key = (hit.Caster, hit.SpellBaseId);
						if (!perCasterSpell.TryGetValue(key, out var hits))
							perCasterSpell[key] = hits = [];

						hits.Add(-hit.Delta);
					}
				}

				foreach (var ((caster, spell), hits) in perCasterSpell)
					rows.Add((capture.Name, caster, playerHandles.Contains(caster), playerLevel, spell, hits));
			}

			Console.WriteLine($"  {rows.Count} (capture, caster, spell) group(s) over {captures.Count} capture(s).\n");
			Console.WriteLine($"  {"capture",-40} {"caster",9} {"lvl",4} {"spell",-34} {"n",4} {"median",9} {"stated",9} {"ratio",6}");

			foreach (var r in rows.OrderBy(r => r.PlayerLevel).ThenBy(r => r.Caster).ThenByDescending(r => r.Hits.Count))
			{
				if (r.Hits.Count < 2)
					continue; // a single hit is a sample, not a median

				if (!r.IsPlayer)
					continue; // a pet's or a mob's hit is not the player's spell scaling

				var sorted = r.Hits.Order().ToArray();
				var median = sorted[sorted.Length / 2];
				var mid = StatedMid(stated, r.SpellId, r.PlayerLevel);
				var name = names.GetValueOrDefault(r.SpellId, $"spell {r.SpellId}");
				Console.WriteLine($"  {Trim(r.Capture, 40),-40} {r.Caster:X8} {r.PlayerLevel,4} {Trim(name, 34),-34} {r.Hits.Count,4} {median,9} "
				                  + $"{(mid is { } m ? m.ToString() : "-"),9} {(mid is { } m2 and > 0 ? $"{(double)median / m2:F2}" : "-"),6}");
			}

			// ⭐ THE COLUMN THE TICKET TURNS ON. Everything above is a ratio; this is whether the ratio can ever become
			// a rule from what we hold.
			Console.WriteLine();
			// ⚠️ PLAYER casters only. Every pet, guard and mob that lands a spell hit carries its own handle, which is
			// how a first cut of this counted 41 "casters" at the level cap - a number that would have unblocked the
			// ticket on the strength of the necromancer's own pets.
			var players = rows.Where(r => r.IsPlayer).ToList();
			Console.WriteLine($"  {"player level",12} {"player casters",15} {"all handles",12} {"captures",9}");
			foreach (var g in players.GroupBy(r => r.PlayerLevel).OrderBy(g => g.Key))
			{
				Console.WriteLine($"  {g.Key,12} {g.Select(r => r.Caster).Distinct().Count(),15} "
				                  + $"{rows.Where(r => r.PlayerLevel == g.Key).Select(r => r.Caster).Distinct().Count(),12} {g.Select(r => r.Capture).Distinct().Count(),9}");
			}

			// ⚠️ A caster only COUNTS if it lands a hit this report can ratio - i.e. one whose spell states a flat
			// magnitude. A second caster whose every captured hit is a percent-of-pool separates nothing.
			var atCap = players.Where(r => r.PlayerLevel >= 80).ToList();
			var comparable = atCap.Where(r => StatedMid(stated, r.SpellId, r.PlayerLevel) is > 0).Select(r => r.Caster).Distinct().Count();
			Console.WriteLine();
			Console.WriteLine($"  player casters at the level cap: {DistinctCasters(atCap)}, of which {comparable} land a FLAT-kind hit this can ratio");
			Console.WriteLine(comparable > 1
				? "  ⭐ MORE THAN ONE COMPARABLE CASTER - the per-caster multiplier can now be separated from the level.\n"
				  + "     #924 is unblocked: fit the ratio per caster and see whether it moves."
				: "  ⛔ ONE COMPARABLE CASTER. A per-CASTER multiplier and a per-LEVEL one are indistinguishable on a single\n"
				  + "     character, and the inputs never travel (only MagicRating is pushed at level 80).\n"
				  + "  ⚠️ Note what the row above does and does not say: the corpus DOES hold a second level-80 player caster,\n"
				  + "     so \"only one caster was captured\" is no longer the blocker. What is missing is narrower and more\n"
				  + "     findable - a session in which that caster lands a DIRECT-DAMAGE nuke, rather than only the\n"
				  + "     percent-of-pool spells its captures happen to use. Re-run this rather than trusting the prose.");
		}

		// A spell's stated pool move as the template holds it: a roll between `Amount` and `MaxAmount`, each ramping
		// to its own endpoint between `LowLevel` and `HighLevel`.
		// The distinct player casters in a set of rows.
		private static int DistinctCasters(IEnumerable<(string Capture, uint Caster, bool IsPlayer, uint PlayerLevel, uint SpellId, List<int> Hits)> rows)
		{
			return rows.Select(r => r.Caster).Distinct().Count();
		}

		private sealed record StatedRamp(int Low, int LowAtHighLevel, int High, int HighAtHighLevel, uint LowLevel, uint HighLevel);

		// The MIDDLE of what the spell's template states at `level`, in the WIRE's own hundredths - the same units a
		// StatDamage delta carries, so the ratio is unit-free. (Dividing this by 100 and comparing against a raw wire
		// median is how a first cut of this reported ratios of ~600.)
		private static int? StatedMid(IReadOnlyDictionary<uint, StatedRamp> stated, uint spellId, uint level)
		{
			if (!stated.TryGetValue(spellId, out var ramp))
				return null;

			var lo = Ramp(ramp.Low, ramp.LowAtHighLevel, ramp.LowLevel, ramp.HighLevel, level);
			var hi = Ramp(ramp.High, ramp.HighAtHighLevel, ramp.LowLevel, ramp.HighLevel, level);

			return (Math.Abs(lo) + Math.Abs(hi)) / 2;
		}

		private static int Ramp(int atLow, int atHigh, uint lowLevel, uint highLevel, uint level)
		{
			if (highLevel <= lowLevel || level <= lowLevel)
				return atLow;

			var t = Math.Min(level, highLevel) - lowLevel;

			return atLow + (int)((long)(atHigh - atLow) * t / (highLevel - lowLevel));
		}
	}
}
