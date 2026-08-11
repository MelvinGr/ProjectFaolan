using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report fall-speed`: pair every captured FALL with the impact SPEED the client reported for it (#515).
	//
	// #504 shipped `damage = maxHealth * 0.038 * (fallDistance - 3.08)`, and #515 says the limit is the
	// MEASUREMENT rather than the model: the drop is reconstructed from position samples ~200 ms apart, so it
	// misses a fraction of a unit at each end - which dominates a 4-unit fall whose free distance is 3.08.
	//
	// ⭐ The client already reports the impact speed and it needs no reconstruction at all. During flight the
	// vector `CharDCMoveCodec` calls `Velocity` carries the live velocity, and the LANDING record's `Velocity.Y`
	// is the impact speed - one number, sampled by the client at the moment of impact rather than interpolated
	// between two of our samples.
	//
	// ⭐⭐ And this dissolves the trade-off #515 calls "the actual decision on this ticket". The worry is that
	// driving damage off a client-reported speed means TRUSTING it for a server-side number. It does not, because
	// impact speed and fall height are the same quantity under `v^2 = 2*g*h`: a law in `v` is the same law in `h`
	// through a fixed transform the server can apply to its OWN tracked height. So the client's speed is the
	// CALIBRATION ORACLE - the precise measurement we fit the constants against - and never a runtime input. The
	// server keeps computing from state it already holds, and gains only the accuracy.
	//
	// ⚠️ The health POOL is the third column, and it is the one two earlier passes died on. It comes from the
	// separate `StatUpdateIIR` (0x96B8DC59) stream, as the HIGH-WATER MARK of `HealthCurrent` for that handle up to
	// the fall - and from nowhere else. Two things it is NOT:
	//   * `StatDamageIIR.StatValue` - the damage again on the environmental form, so a pool read off it makes every
	//     fall cost exactly 50% BY CONSTRUCTION, which is what the second attempt produced and refused to ship;
	//   * the `HealthMax` the same stat block states - the wire max is a BASE the client tops up (#450), and it
	//     reads 2,371 for every fall in the corpus, the same value for two different characters, against which the
	//     deepest falls come out at 225% of the pool.
	//
	// ⚠️ Two more traps, both of which produce a plausible wrong number rather than an obvious failure:
	//   * BOTH streams carry every visible entity, not just the player - so the pool must be keyed by the damage's
	//     own target handle, or a fall gets paired with some NPC's health;
	//   * the two sides are on DIFFERENT SCALES. `CharacterStatBlock`'s indexer already returns the player-facing
	//     value, while `StatDamageIIR.Delta` is the x100 wire number. Mixed, a fall costs 4,000% of the pool.
	public static partial class ReportCli
	{
		// The environmental form: stat 0x1B (Health), school 12, and no caster at all. Same test
		// `StatDamages.Environmental` builds, stated once here.
		private const uint HealthStat = 0x1B;

		private static bool IsFall(StatDamageIIR d)
		{
			return d.StatId == HealthStat && (uint)d.School == 12 && d.Caster == 0 && d.Delta < 0;
		}

		// How far back to look for the fall's apex. Long enough for a deep drop, short enough that the previous
		// unrelated movement does not raise the maximum.
		private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(3);

		// ⚠️ How long after the landing record the damage may arrive and still be THAT landing's. The environmental
		// school is not fall-only - lava is in it too (#504) - and an environmental hit with no landing near it is
		// something else entirely. Without this bound the pairing walks back to whatever the last downward movement
		// was: the corpus contains a hit 94 SECONDS after one, which would otherwise be fitted as a fall. The good
		// pairings sit at 86..855 ms, so the bound is comfortably outside the real population rather than tuned to it.
		private static readonly TimeSpan LandingLag = TimeSpan.FromSeconds(1);

		// Gravity is measured as `v^2 / 2h` against the SAMPLED drop, which is the noisy quantity this whole ticket
		// exists to stop using - so it is measured only on the falls where that noise is smallest. A drop of 10+
		// units is many times the ~0.5 unit the 200 ms sampling can lose at each end.
		private const float GravitySampleDepth = 10f;

		// How long after the damage the fall DEBUFF may arrive. Wider than LandingLag because the two are different
		// events: the damage is the landing, the debuff is retail deciding what the landing did.
		//
		// ⚠️ The column is REPORTED and deliberately NOT aggregated into bands, because attribution is not sound at
		// this corpus's fall spacing and the failure is silent: at 5 s the corpus yields 10 Bruised Body falls and
		// ZERO Broken Bones (though #504 observed Broken Bones), and at 15 s it yields 5 Broken Bones spanning
		// 0.47%..84.28% of the pool - i.e. the wider window simply captures the NEXT fall's debuff. Both look like
		// measurements. Bounding it properly needs "the first fall-debuff before the same handle's next fall", which
		// is a change to how falls are collected rather than a wider constant.
		private static readonly TimeSpan DebuffLag = TimeSpan.FromSeconds(5);

		private static void FallSpeed(IReadOnlyList<NamedCapture> captures)
		{
			var falls = new List<(string Capture, uint Handle, double Damage, float ImpactY, float DropY, int Samples, double Gap, uint Pool, uint Debuff)>();
			var noMove = 0;
			var noPool = 0;
			var unpaired = 0;
			var debuffApplies = 0;

			foreach (var capture in captures)
			{
				// The pool track, per handle, in packet order - read from the StatUpdate stream and NOT from the
				// damage record (see the class remarks).
				//
				// ⚠️ The pool is the HIGH-WATER MARK of `HealthCurrent`, NOT the `HealthMax` the same stream states.
				// The wire max is a BASE the client tops up (#450), so it is not the number a fall is a fraction of:
				// reading it gives 2,371 for every fall in the corpus - the same value for two different characters -
				// against which the deepest falls come out at 225% and 161% of the pool, i.e. more damage than the
				// character could possibly have. A player is at full health at some point in a session, so the
				// highest current the stream ever states for them IS the pool, measured rather than derived.
				var pools = new Dictionary<uint, List<(int Index, uint Current, uint WireMax)>>();
				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true, SubOpcode: StatUpdateCodec.Opcode } || !p.TryParse(StatUpdateCodec.Parse, out var update))
						continue;

					// The indexer returns 0 for a stat the block does not carry, and most updates carry only some of
					// them - so an absent stat must be skipped rather than recorded as a zero.
					var current = update.Stats[CharacterStatId.HealthCurrent];
					if (current == 0)
						continue;

					if (!pools.TryGetValue(update.EntityHandle, out var list))
						pools[update.EntityHandle] = list = [];

					list.Add((i, current, update.Stats[CharacterStatId.HealthMax]));
				}

				// The two fall DEBUFFS as they land, per handle. A fall's tier is not derivable from the damage - it
				// is what retail actually applied - so it is read off the wire like everything else here.
				var debuffs = new Dictionary<uint, List<(int Index, uint SpellBase)>>();
				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode } || !p.TryParse(MagicBuffCodec.Parse, out var buff))
						continue;

					if (buff.SpellBaseId != FallDamage.BruisedBodySpellId && buff.SpellBaseId != FallDamage.BrokenBonesSpellId)
						continue;

					if (!debuffs.TryGetValue(buff.ApplyTo, out var list))
						debuffs[buff.ApplyTo] = list = [];

					list.Add((i, buff.SpellBaseId));
					debuffApplies++;
				}

				// The player's own movement stream, in order: (index, position, velocity). Keyed by handle, because a
				// capture carries every visible entity's moves and a fall belongs to exactly one of them.
				var moves = new Dictionary<uint, List<(int Index, System.Numerics.Vector3 Pos, System.Numerics.Vector3 Vel)>>();

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true, SubOpcode: CharDCMoveCodec.Opcode } || !p.TryParse(CharDCMoveCodec.Parse, out var move))
						continue;

					if (!moves.TryGetValue(move.Handle, out var list))
						moves[move.Handle] = list = [];

					list.Add((i, move.Position, move.Velocity));
				}

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true, SubOpcode: StatDamageCodec.Opcode } || !p.TryParse(StatDamageCodec.Parse, out var dmg) || !IsFall(dmg))
						continue;

					if (!moves.TryGetValue(dmg.Handle, out var track))
					{
						noMove++;
						continue;
					}

					// The LANDING record: the last move before the damage whose vertical velocity is downward. A
					// record at rest carries ~0 there, so the test is the sign rather than "the nearest record".
					var before = track.Where(m => m.Index < i).ToList();
					var landing = before.LastOrDefault(m => m.Vel.Y < -0.01f);
					if (landing.Index == 0 && landing.Vel.Y >= -0.01f)
					{
						noMove++;
						continue;
					}

					// ⚠️ ...and it has to be THIS damage's landing. See LandingLag: the environmental school carries
					// lava as well as falls, so an environmental hit with no landing within the bound is not a fall
					// and must not be fitted as one.
					if (capture.Packets[i].Ticks - capture.Packets[landing.Index].Ticks > LandingLag.Ticks)
					{
						unpaired++;
						continue;
					}

					// The DROP, reconstructed the way #504 did it - from POSITION samples, not from the velocity sign.
					// ⚠️ A velocity-sign run does not work: the client sends exactly ONE record with a downward
					// Velocity.Y per fall (measured - `airborne` was 1 on all 39), so a run bounded by the sign is a
					// single sample and its drop is 0 by construction. The window is the same one a fall occupies.
					var windowStart = capture.Packets[landing.Index].Ticks - DropWindow.Ticks;
					var window = before.Where(m => capture.Packets[m.Index].Ticks >= windowStart).ToList();
					var drop = window.Count > 0 ? window.Max(m => m.Pos.Y) - landing.Pos.Y : 0f;

					// ⚠️ `Delta` is in HUNDREDTHS of a health point - the ×100 wire scale #515's history already names
					// as one of its three traps. Divided here so the column is health, and so a reader does not take a
					// 534,437 against a 6,341 pool as a decode failure.
					// The pool AT THE MOMENT OF THE FALL: the highest health this handle has been seen at, up to and
					// including the fall itself. Bounded at the fall rather than taken over the whole capture because
					// a character LEVELS during a session and the pool grows - taking a later, bigger pool would
					// understate every earlier fall. (That growth is also the likeliest reason the 25-fall handle's
					// rank correlation is 0.87 rather than 1.00.)
					//
					// The fall's own damage is added back: a fall observed only at reduced health would otherwise be
					// measured against a high-water mark it had already spent.
					var pool = pools.TryGetValue(dmg.Handle, out var track2)
						? track2.Where(m => m.Index <= i).Select(m => m.Current).DefaultIfEmpty(0u).Max()
						: 0u;
					if (pool == 0)
					{
						noPool++;
						continue;
					}

					// The debuff this landing applied: a Bruised Body / Broken Bones apply on the same handle inside
					// the landing window. Bounded the same way the landing is, and for the same reason - the two
					// spells are fall-only, but a later fall's debuff must not be attributed to this one.
					var debuff = debuffs.TryGetValue(dmg.Handle, out var buffTrack)
						? buffTrack.FirstOrDefault(t => t.Index >= i && capture.Packets[t.Index].Ticks - capture.Packets[i].Ticks <= DebuffLag.Ticks).SpellBase
						: 0u;

					falls.Add((capture.Name, dmg.Handle, -dmg.Delta / 100.0, landing.Vel.Y, drop, window.Count,
						capture.Packets[i].Ticks - capture.Packets[landing.Index].Ticks, pool, debuff));
				}
			}

			Console.WriteLine($"{falls.Count} fall(s) paired with a landing CharDCMove and a health pool; " +
				$"{noMove} with no usable movement track, {unpaired} whose nearest landing is more than " +
				$"{LandingLag.TotalSeconds:F0}s away (not a fall - the environmental school carries lava too), " +
				$"{noPool} with no health stated for them. {debuffApplies} fall-debuff apply/end record(s) in the corpus.");
			if (falls.Count == 0)
			{
				Console.WriteLine("  (no environmental health damage in this corpus - check the captures dir.)");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"  {"damage",9} {"pool",7} {"frac",7} {"impactY",9} {"debuff",12} {"sampledDrop",12} {"lag ms",7}  capture");
			foreach (var f in falls.OrderBy(f => f.ImpactY))
			{
				Console.WriteLine($"  {f.Damage,9:F1} {f.Pool,7} {f.Damage / f.Pool,7:P1} {f.ImpactY,9:F3} {DebuffName(f.Debuff),12} {f.DropY,12:F2}" +
					$" {f.Gap / TimeSpan.TicksPerMillisecond,7:F0}  {Trim(f.Capture, 40)}");
			}

			// The claim to test: does the client's speed order the events BETTER than our reconstructed drop? Damage
			// is pool-relative, so raw damage cannot be compared across characters - but the RANK correlation can,
			// within the events of one handle, where the pool is fixed.
			Console.WriteLine();
			Console.WriteLine("⭐ per-entity rank agreement (the pool is fixed within one handle, so damage is comparable there):");
			Console.WriteLine($"  {"handle",10} {"falls",5}  {"speed vs damage",16}  {"drop vs damage",15}");

			foreach (var g in falls.GroupBy(f => f.Handle).Where(g => g.Count() > 2).OrderByDescending(g => g.Count()))
			{
				var rows = g.ToList();
				Console.WriteLine($"  0x{g.Key:X8} {rows.Count,5}  {Spearman([.. rows.Select(r => (double)-r.ImpactY)], [.. rows.Select(r => r.Damage)]),16:F3}"
				                  + $"  {Spearman([.. rows.Select(r => (double)r.DropY)], [.. rows.Select(r => r.Damage)]),15:F3}");
			}

			Console.WriteLine();
			Console.WriteLine("  A speed column at or near 1.000 where the drop column is not says the client's number is the");
			Console.WriteLine("  better predictor - which is #515's premise, now measured rather than asserted.");

			Fit(falls);
		}

		private static string DebuffName(uint spellBase)
		{
			return spellBase switch
			{
				FallDamage.BruisedBodySpellId => "Bruised",
				FallDamage.BrokenBonesSpellId => "BrokenBones",
				_ => "-"
			};
		}

		// The refit, in the only two steps that are separable: measure GRAVITY from the falls, then fit the damage
		// law in HEIGHT - because height is what the server computes from its own tracked state, and what
		// `FallDamage`'s constants are expressed in.
		//
		// ⭐ Height is not taken from the position samples here. The whole point of #515 is that those are ~200 ms
		// apart and lose a fraction of a unit at each end; the client's impact SPEED does not, and `v^2 = 2gh`
		// converts one into the other exactly. So each fall's height is `v^2 / 2g` - the client's own precision,
		// expressed in the server's own units.
		private static void Fit(IReadOnlyList<(string Capture, uint Handle, double Damage, float ImpactY, float DropY, int Samples, double Gap, uint Pool, uint Debuff)> falls)
		{
			// GRAVITY, from the deep falls only. A sampled drop of 7+ units carries ~13% error (#504); a 4-unit one
			// is dominated by the sampling gap, and including those biases g by more than the deep falls can fix.
			var deep = falls.Where(f => f.DropY >= GravitySampleDepth).Select(f => (double)(f.ImpactY * f.ImpactY) / (2 * f.DropY)).Order().ToList();
			Console.WriteLine();
			if (deep.Count < 3)
			{
				Console.WriteLine($"⚠️ only {deep.Count} fall(s) of {GravitySampleDepth:F0}+ sampled units - too few to measure gravity, so no fit is printed.");
				return;
			}

			var gravity = deep[deep.Count / 2];
			Console.WriteLine($"⭐ gravity from the {deep.Count} falls of {GravitySampleDepth:F0}+ units (v^2 / 2h, median): {gravity:F2} u/s^2   " +
				$"[p10 {deep[deep.Count / 10]:F2}, p90 {deep[deep.Count * 9 / 10]:F2}]");

			// The damage law, fitted in height. `frac = slope * (h - intercept)`, by ordinary least squares on
			// (h, frac) - the same shape #504 fitted, on a height that no longer has the sampling error in it.
			var points = falls.Select(f => (H: f.ImpactY * f.ImpactY / (2 * gravity), Frac: f.Damage / f.Pool)).ToList();
			var n = points.Count;
			var meanH = points.Average(p => p.H);
            var meanF = points.Average(p => p.Frac);
			var sxx = points.Sum(p => (p.H - meanH) * (p.H - meanH));
			var sxy = points.Sum(p => (p.H - meanH) * (p.Frac - meanF));
			if (sxx <= 0)
			{
				Console.WriteLine("⚠️ every fall lands at the same height - nothing to fit.");
				return;
			}

			var slope = sxy / sxx;
			var free = meanH - meanF / slope; // the x-intercept: the height below which a fall costs nothing
			Console.WriteLine($"⭐ damage law over {n} falls: frac = {slope:F5} * (h - {free:F2})" +
				$"    [shipped: {0.038:F5} * (h - {3.08:F2})]");

			// How well it reproduces each fall, which is the number #504 reports as "median 13% over the deep ones".
			var errors = points.Select(p => (Predicted: Math.Max(0, slope * (p.H - free)), p.Frac))
				.Where(e => e.Frac > 0)
				.Select(e => Math.Abs(e.Predicted - e.Frac) / e.Frac)
				.Order()
				.ToList();
			Console.WriteLine($"   |error| over all {errors.Count}: p50 {errors[errors.Count / 2]:P0}, " +
				$"p90 {errors[errors.Count * 9 / 10]:P0}, max {errors[^1]:P0}");

			// ⚠️ The intercept is what the shipped constant is PINNED by rather than fitted to: #504 sets it from the
			// smallest DAMAGING fall observed, because a least-squares intercept predicts zero for that fall. Print
			// both so the choice stays visible instead of being silently overwritten by the regression.
			var smallest = points.Where(p => p.Frac > 0).MinBy(p => p.H);
			Console.WriteLine($"   smallest damaging fall: h {smallest.H:F2} for {smallest.Frac:P1} of pool " +
				$"- an intercept above that predicts ZERO for it, which is why the shipped one is pinned, not fitted.");

			FitInSpeed(falls);
		}

		// ⭐ The SAME law, fitted in v^2 - and this is the one that ships (#515).
		//
		// The height fit above has to divide by a measured gravity, and gravity can only be measured as `v^2 / 2h`
		// against the SAMPLED drop - the very quantity this ticket exists to stop using. Over the deep falls it runs
		// p10 7.6 / median 19.3 / p90 36.2, so the conversion was the loosest link in the chain and refitting through
		// it would have traded a known error for an unknown one. That is why the constants sat unchanged.
		//
		// The blocker dissolves by never converting. `frac = a * (h - h0)` and `frac = A * (v^2 - V0)` are the SAME
		// model under `v^2 = 2gh` - gravity is absorbed into the fitted slope (`A = a / 2g`, `V0 = 2g * h0`) - so
		// fitting in v^2 needs no gravity at all, and the server applies it to the impact speed the client reports on
		// the landing move rather than to a height it reconstructed. ⚠️ That is not a new trust assumption: the
		// server's sampled drop already comes from the client's own position stream, so the velocity in the same
		// packet is the same source, one field over and without the 200 ms sampling loss.
		private static void FitInSpeed(IReadOnlyList<(string Capture, uint Handle, double Damage, float ImpactY, float DropY, int Samples, double Gap, uint Pool, uint Debuff)> falls)
		{
			var points = falls.Select(f => (V2: (double)f.ImpactY * f.ImpactY, Frac: f.Damage / f.Pool)).ToList();
			var meanV2 = points.Average(p => p.V2);
			var meanF = points.Average(p => p.Frac);
			var sxx = points.Sum(p => (p.V2 - meanV2) * (p.V2 - meanV2));
			if (sxx <= 0)
				return;

			var slope = points.Sum(p => (p.V2 - meanV2) * (p.Frac - meanF)) / sxx;
			var free = meanV2 - meanF / slope;
			Console.WriteLine();
			Console.WriteLine($"⭐ the same law in SPEED, which needs no gravity: frac = {slope:F8} * (v^2 - {free:F1})" +
				$"   i.e. free below {Math.Sqrt(free):F2} u/s   [shipped: {FallDamage.DamagePerSpeedSquared:F8} * (v^2 - {FallDamage.FreeFallSpeed * FallDamage.FreeFallSpeed:F1})]");

			// The three candidate predictors, scored the same way, so "the speed is better" is a measurement rather
			// than a preference. Pearson rather than Spearman here: the fit is linear in each x, and what is being
			// compared is how much of the VARIANCE each one explains, not merely the ordering.
			Console.WriteLine($"   correlation with the damage fraction:  v^2 {Pearson([.. points.Select(p => p.V2)], [.. points.Select(p => p.Frac)]):F3}" +
				$"   v {Pearson([.. falls.Select(f => (double)-f.ImpactY)], [.. points.Select(p => p.Frac)]):F3}" +
				$"   sampled drop {Pearson([.. falls.Select(f => (double)f.DropY)], [.. points.Select(p => p.Frac)]):F3}");

			Errors("   |error| in v^2", points.Select(p => (Math.Max(0, slope * (p.V2 - free)), p.Frac)));
			Errors("   |error| as SHIPPED (speed)",
				points.Select(p => ((double)FallDamage.Resolve((float)Math.Sqrt(p.V2), 10000) / 10000, p.Frac)));
			Errors("   |error| on the sampled drop, best case (refit)", RefitOnDrop(falls));
		}

		// The sampled-drop law refitted on this same corpus, so the comparison is against its BEST case rather than
		// against constants fitted on an older pass. Anything worse than this is the measurement, not the model.
		private static IEnumerable<(double Predicted, double Actual)> RefitOnDrop(
			IReadOnlyList<(string Capture, uint Handle, double Damage, float ImpactY, float DropY, int Samples, double Gap, uint Pool, uint Debuff)> falls)
		{
			var pts = falls.Select(f => (H: (double)f.DropY, Frac: f.Damage / f.Pool)).ToList();
			var mh = pts.Average(p => p.H);
			var mf = pts.Average(p => p.Frac);
			var sxx = pts.Sum(p => (p.H - mh) * (p.H - mh));
			var slope = sxx > 0 ? pts.Sum(p => (p.H - mh) * (p.Frac - mf)) / sxx : 0;
			var free = slope != 0 ? mh - mf / slope : 0;
			return pts.Select(p => (Math.Max(0, slope * (p.H - free)), p.Frac));
		}

		private static void Errors(string label, IEnumerable<(double Predicted, double Actual)> rows)
		{
			var errors = rows.Where(e => e.Actual > 0).Select(e => Math.Abs(e.Predicted - e.Actual) / e.Actual).Order().ToList();
			if (errors.Count == 0)
				return;

			Console.WriteLine($"{label}: p50 {errors[errors.Count / 2]:P0}, p90 {errors[errors.Count * 9 / 10]:P0}, max {errors[^1]:P0}");
		}

		private static double Pearson(double[] a, double[] b)
		{
			var ma = a.Average();
			var mb = b.Average();
			var sa = Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)));
			var sb = Math.Sqrt(b.Sum(x => (x - mb) * (x - mb)));
			return sa <= 0 || sb <= 0 ? double.NaN : a.Zip(b, (x, y) => (x - ma) * (y - mb)).Sum() / (sa * sb);
		}

		// Spearman rank correlation - the right measure here because the law is monotone but not linear (damage goes
		// as the drop, the drop as the square of the speed), so Pearson would understate a perfect ordering.
		private static double Spearman(double[] a, double[] b)
		{
			if (a.Length != b.Length || a.Length < 2)
				return double.NaN;

			var ra = Ranks(a);
			var rb = Ranks(b);
			var n = a.Length;
			var d2 = ra.Zip(rb, (x, y) => (x - y) * (x - y)).Sum();
			return 1 - 6 * d2 / (n * ((double)n * n - 1));
		}

		private static double[] Ranks(double[] values)
		{
			var order = values.Select((v, i) => (v, i)).OrderBy(x => x.v).ToList();
			var ranks = new double[values.Length];
			for (var i = 0; i < order.Count;)
			{
				var j = i;
				while (j < order.Count && Math.Abs(order[j].v - order[i].v) < 1e-9)
					j++;

				// Ties share the average rank, or a run of identical values would fabricate an ordering.
				var mean = (i + j - 1) / 2.0;
				for (var k = i; k < j; k++)
					ranks[order[k].i] = mean;

				i = j;
			}

			return ranks;
		}
	}
}
