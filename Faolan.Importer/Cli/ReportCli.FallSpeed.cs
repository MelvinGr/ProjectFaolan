using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
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
	// ⚠️ What the report does NOT do is refit `FallDamage`. It prints the paired observations and the correlation,
	// because the refit needs the health POOL per event too, and #515's own history is two attempts that produced a
	// number by arithmetic rather than by measurement (`StatValue` is not the pre-hit pool - see StatDamageIIR).
	// Shipping the pairing first is what lets the fit be checked rather than believed.
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

		private static void FallSpeed(IReadOnlyList<NamedCapture> captures)
		{
			var falls = new List<(string Capture, uint Handle, double Damage, float ImpactY, float DropY, int Samples, double Gap)>();
			var noMove = 0;

			foreach (var capture in captures)
			{
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
					falls.Add((capture.Name, dmg.Handle, -dmg.Delta / 100.0, landing.Vel.Y, drop, window.Count,
						capture.Packets[i].Ticks - capture.Packets[landing.Index].Ticks));
				}
			}

			Console.WriteLine($"{falls.Count} fall(s) paired with a landing CharDCMove; {noMove} with no usable movement track.");
			if (falls.Count == 0)
			{
				Console.WriteLine("  (no environmental health damage in this corpus - check the captures dir.)");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"  {"damage",9} {"impactY",9} {"sampledDrop",12} {"samples",8} {"lag ms",7}  capture");
			foreach (var f in falls.OrderBy(f => f.ImpactY))
			{
				Console.WriteLine($"  {f.Damage,9:F1} {f.ImpactY,9:F3} {f.DropY,12:F2} {f.Samples,8} {f.Gap / TimeSpan.TicksPerMillisecond,7:F0}  {Trim(f.Capture, 46)}");
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
			Console.WriteLine();
			Console.WriteLine("⚠️ This does NOT refit FallDamage. The refit needs the health POOL per event, which must come from");
			Console.WriteLine("   the separate StatUpdateIIR stream - `StatDamageIIR.StatValue` is the damage again on the");
			Console.WriteLine("   environmental form, and reading a pool off it makes every fall cost exactly 50% by construction.");
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
