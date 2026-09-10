using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Extractors;
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
			Dictionary<uint, uint> kinds;
			await using (var db = OpenDb())
			{
				// Every spell's health-effect KIND, flat or not. The census below uses it to say why a spell carries no
				// ratio - "it states a percent" is a different answer from "we hold no effect row for it", and reading
				// a blank as the second when it is the first is what sent the first cut of this ticket wrong.
				kinds = (await db.SpellStatEffects.AsNoTracking()
						.Where(e => e.StatId == CharacterStatId.HealthCurrent && e.Amount < 0)
						.Select(e => new { e.SpellId, e.EffectKind })
						.ToListAsync())
					.GroupBy(e => e.SpellId)
					.ToDictionary(g => g.Key, g => g.First().EffectKind);

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

			// One entry per HIT that landed on a creature whose spawn record the capture also carries: the per-target
			// table below is the whole finding, and it needs the target's level beside the number.
			var perTarget = new List<(string Capture, uint Caster, uint PlayerLevel, uint SpellId, uint Target, string TargetName, uint TargetLevel, int Damage)>();

			foreach (var capture in captures)
			{
				var playerHandles = new HashSet<uint>();
				var playerLevel = 0u;
				var perCasterSpell = new Dictionary<(uint Caster, uint Spell), List<int>>();

				// ⭐ The TARGET's own level, off its own spawn record - which is what the per-target ratios below turn
				// out to be a function of. Never off NpcSpawns: a spawn row is a per-respawn observation.
				var targetLevel = new Dictionary<uint, uint>();
				var targetName = new Dictionary<uint, string>();
				foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
				{
					var spawnBody = e.Data is { Length: >= 12 } ? e.Data[(12 + (int)e.Data.ReadU32Be(8))..] : null;
					if (spawnBody != null && SpawnBodyReader.TryReadCharacterMessage(spawnBody, out var read) && read.Message.Stats is { } st)
						targetLevel[e.Handle] = st.Level;
					if (!string.IsNullOrEmpty(e.Name))
						targetName[e.Handle] = e.Name;
				}

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

						if (playerHandles.Contains(hit.Caster) && targetLevel.TryGetValue(hit.Handle, out var lvl))
						{
							perTarget.Add((capture.Name, hit.Caster, playerLevel, hit.SpellBaseId, hit.Handle,
								targetName.GetValueOrDefault(hit.Handle, ""), lvl, -hit.Delta));
						}

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
					continue; // a single hit is a sample, not a median - the CENSUS below keeps it

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
			SpellCasterCensus(rows, names, kinds, stated);
			SpellDamageByTarget(perTarget, names, stated);
			SpellDamageByTargetLevel(perTarget, stated);

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
			Console.WriteLine($"  player casters at the level cap: {DistinctCasters(atCap)}, of which {comparable} land a FLAT-kind hit this can ratio,");
			Console.WriteLine("  so only that many could ever be compared caster-to-caster.");
			Console.WriteLine("  ⛔ But that comparison is NOT what #924 needs any more - see the per-target table above. The ~4.7x is");
			Console.WriteLine("     per-TARGET, so a second caster separates nothing: the same caster already produces 2.53 to 8.02 on");
			Console.WriteLine("     one spell in one session. This block stays because it is how that was found - the caster count is");
			Console.WriteLine("     what the ticket asked for, and answering it is what showed the question was the wrong one.");
		}

		/// <summary>
		///     ⭐⭐ The ratio against the TARGET'S LEVEL, with critical hits taken out - the curve the per-target table
		///     above is a function of.
		/// </summary>
		/// <remarks>
		///     ⭐ <b>A crit is a clean x2 and has to come out first.</b> Every target's hits form a tight base cluster
		///     plus occasional outliers at almost exactly twice it - Pestilential Blast on Boros is ten hits of
		///     15,444-16,900 and one of 32,199; on Argus five of 15,402-16,531 and two of 33,562/33,769. Left in, those
		///     inflate a target's mean by up to 2x and scatter the curve; taken out, what is left per target is the
		///     spell's own stated roll at +/-5%.
		///     <para />
		///     ⭐⭐ <b>The ratio falls as the target's level rises</b>, which is what mitigation looks like, and it is
		///     why the ticket's ~4.7x is not a caster property: that figure is an average over a target mixture that is
		///     mostly LOW-level mobs. Against the level-80 creatures a level-80 player actually fights it is ~2.5.
		///     <para />
		///     ⚠️ The curve is not clean below the top: level 31 gives both 3.70 and 4.82 on the same spell, so target
		///     level is a strong predictor and not the whole rule. What IS solid is the floor at the top and the shape.
		/// </remarks>
		private static void SpellDamageByTargetLevel(IReadOnlyList<(string Capture, uint Caster, uint PlayerLevel, uint SpellId, uint Target, string TargetName, uint TargetLevel, int Damage)> hits,
			IReadOnlyDictionary<uint, StatedRamp> stated)
		{
			// Per (spell, target), the base cluster: the target's own median and everything within 1.5x of it. A crit
			// sits at ~2x and drops out; the roll's own +/-16% does not.
			var clean = new List<(uint TargetLevel, uint Target, double Ratio)>();
			foreach (var group in hits.Where(h => stated.ContainsKey(h.SpellId) && h.PlayerLevel >= 80).GroupBy(h => (h.SpellId, h.Target)))
			{
				var mid = StatedMid(stated, group.Key.SpellId, 80);
				if (mid is not > 0)
					continue;

				var values = group.Select(h => h.Damage).Order().ToArray();
				var median = values[values.Length / 2];
				foreach (var v in values.Where(v => v <= median * 1.5))
					clean.Add((group.First().TargetLevel, group.Key.Target, (double)v / mid.Value));
			}

			Console.WriteLine();
			Console.WriteLine("--- the ratio by TARGET LEVEL, crits removed (every level-80 caster hit that joins a spawn record) ---");
			Console.WriteLine($"  {"target level",13} {"hits",5} {"targets",8} {"mean ratio",11} {"min",7} {"max",7}");

			foreach (var band in clean.GroupBy(c => Band(c.TargetLevel)).OrderBy(g => g.Key))
			{
				var r = band.Select(c => c.Ratio).ToArray();
				Console.WriteLine($"  {BandName(band.Key),13} {r.Length,5} {band.Select(c => c.Target).Distinct().Count(),8} {r.Average(),11:F2} {r.Min(),7:F2} {r.Max(),7:F2}");
			}

			Console.WriteLine();
			Console.WriteLine("  ⭐⭐ The ratio FALLS as the target's level rises - flat at ~5.1-6.0 for anything under 78, then a STEP");
			Console.WriteLine("     down to 2.64 on the level 78-80 creatures a level-80 player actually fights. So #924's ~4.7x is an");
			Console.WriteLine("     average over a target mixture, not a property of the caster, and no second caster was needed to see it.");
			Console.WriteLine("  ⚠️ Which END is the caster's own term is NOT settled here. Read as mitigation the caster puts out ~5.5x");
			Console.WriteLine("     and a level-80 target absorbs half of it; read the other way the caster term is ~2.6x and low-level");
			Console.WriteLine("     targets take amplified damage. Separating those needs a target whose armour we know, not a second");
			Console.WriteLine("     caster - which is a different capture ask from the one this ticket was carrying.");
		}

		private static int Band(uint level)
		{
			return level >= 78 ? 4 : level >= 60 ? 3 : level >= 40 ? 2 : level >= 20 ? 1 : 0;
		}

		private static string BandName(int band)
		{
			return band switch { 4 => "78-80", 3 => "60-77", 2 => "40-59", 1 => "20-39", _ => "under 20" };
		}

		/// <summary>
		///     ⭐⭐ The ratio, per TARGET - which is what the ~4.7x actually varies with.
		/// </summary>
		/// <remarks>
		///     ⛔ <b>This refutes the ticket's central claim.</b> #924 read the ~4.7x as a per-CASTER multiplier
		///     because five spells whose bases differ 3x all landed near it. Split by target instead of aggregated per
		///     spell, the same corpus says otherwise: within ONE target the spread is 1.03-1.11, i.e. the spell's own
		///     stated roll and nothing else, while ACROSS targets in the SAME capture, from the SAME caster at the
		///     SAME level with the SAME spell, the mean ratio runs 2.53 to 8.02.
		///     <para />
		///     A per-caster multiplier cannot vary between two mobs killed a minute apart. So the "4.7x" was an
		///     average over a mixture of targets, exactly as the medians were an average over a mixture of ratios -
		///     and the question "what separates a caster from the level" was the wrong question.
		/// </remarks>
		private static void SpellDamageByTarget(IReadOnlyList<(string Capture, uint Caster, uint PlayerLevel, uint SpellId, uint Target, string TargetName, uint TargetLevel, int Damage)> hits,
			IReadOnlyDictionary<uint, string> names, IReadOnlyDictionary<uint, StatedRamp> stated)
		{
			Console.WriteLine();
			Console.WriteLine("--- the ratio PER TARGET: same caster, same spell, same session ---");
			Console.WriteLine($"  {"spell",-30} {"target",9} {"tLvl",5} {"n",4} {"mean ratio",11} {"own spread",11}  creature");

			foreach (var spell in hits.Where(h => stated.ContainsKey(h.SpellId) && h.PlayerLevel >= 80)
				         .GroupBy(h => h.SpellId)
				         .OrderByDescending(g => g.Select(h => h.Target).Distinct().Count())
				         .Take(4))
			{
				var mid = StatedMid(stated, spell.Key, 80);
				if (mid is not > 0)
					continue;

				foreach (var target in spell.GroupBy(h => h.Target).OrderBy(g => g.Average(h => h.Damage)))
				{
					var values = target.Select(h => h.Damage).Order().ToArray();
					Console.WriteLine($"  {Trim(names.GetValueOrDefault(spell.Key, $"spell {spell.Key}"), 30),-30} {target.Key:X8} {target.First().TargetLevel,5} {values.Length,4} "
					                  + $"{values.Average() / mid.Value,11:F2} {(double)values[^1] / values[0],11:F2}  {Trim(target.First().TargetName, 28)}");
				}

				Console.WriteLine();
			}
		}

		/// <summary>
		///     What each PLAYER caster was actually seen casting, with the reason a spell carries no ratio.
		/// </summary>
		/// <remarks>
		///     ⭐ The ratio table above drops a spell the moment it cannot be scored, which is exactly the population
		///     this ticket needs to look at: the question is no longer "what is the ratio" but "does the SECOND caster
		///     ever land a hit that can be ratioed at all". A table that only shows scorable rows cannot answer it,
		///     and the first cut of this reported the demonologist as casting three spells when the real question was
		///     which of its spells are flat.
		///     <para />
		///     ⭐⭐ <c>DPSHit</c> (53013) is the one to watch. Those spells are DETERMINISTIC at level 80 - the
		///     necromancer's `Curse of Blood` lands the same number on 46 of 79 hits - so ONE captured demonologist
		///     DPSHit would give that character's constant exactly, with no median and no roll to average out. That is
		///     the cheapest separation this ticket can get.
		/// </remarks>
		private static void SpellCasterCensus(IReadOnlyList<(string Capture, uint Caster, bool IsPlayer, uint PlayerLevel, uint SpellId, List<int> Hits)> rows,
			IReadOnlyDictionary<uint, string> names, IReadOnlyDictionary<uint, uint> kinds, IReadOnlyDictionary<uint, StatedRamp> stated)
		{
			Console.WriteLine();
			Console.WriteLine("--- every spell each PLAYER caster was seen casting, ratio or not ---");
			Console.WriteLine($"  {"caster",9} {"lvl",4} {"spell",-36} {"hits",5} {"kind",7} {"scorable",9}  distinct values");

			var byCaster = rows.Where(r => r.IsPlayer)
				.GroupBy(r => (r.Caster, r.PlayerLevel))
				.OrderByDescending(g => g.Key.PlayerLevel)
				.ThenBy(g => g.Key.Caster);

			foreach (var caster in byCaster)
			{
				foreach (var spell in caster.GroupBy(r => r.SpellId).OrderByDescending(g => g.Sum(r => r.Hits.Count)))
				{
					var hits = spell.SelectMany(r => r.Hits).ToList();
					var kind = kinds.GetValueOrDefault(spell.Key);
					var distinct = hits.Distinct().Order().ToArray();
					var scorable = stated.ContainsKey(spell.Key) ? "yes" : kind == 0 ? "no effect row" : kind == PercentKind ? "PERCENT" : $"kind {kind}";
					Console.WriteLine($"  {caster.Key.Caster:X8} {caster.Key.PlayerLevel,4} {Trim(names.GetValueOrDefault(spell.Key, $"spell {spell.Key}"), 36),-36} "
					                  + $"{hits.Count,5} {(kind == 0 ? "-" : kind.ToString()),7} {scorable,9}  "
					                  + (distinct.Length <= 4 ? string.Join(", ", distinct) : $"{distinct.Length} values {distinct[0]}..{distinct[^1]}"));
				}
			}
		}

		// `HitPercentage` - states a percent of the TARGET'S pool, so it can never be ratioed against a magnitude.
		private const uint PercentKind = 53007;

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
