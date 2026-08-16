using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report ranged-standoff`: at what DISTANCE does a retail archer actually shoot? (#838.)
	//
	// Our NPC chase closes to `WorldState.NpcContactRange` (1.5u) and swings inside `NpcMeleeRange` (3u) whatever the
	// creature is holding, so a bow carrier runs up and shoots the player in the face. What it should do instead is a
	// number nobody has: the client's tuning table (`--report rdb-tweaks`, 295 entries) names `AIDefaults_
	// MaximumChaseRadius` and `AIDefaults_ChaseDeaggroDelayTime` but NO ranged stand-off, and our item data carries no
	// weapon range. So it has to be measured off retail's own combat.
	//
	// Method: pair every `Combat:Ranged` / `Combat:Melee` attack with the positions of the attacker and the target it
	// names, both tracked live from the spawn record and every subsequent `CharDCMove`, and print the distance
	// distribution. Melee is the CONTROL: whatever the measurement says about archers only means something if the same
	// pipeline puts melee mobs at contact range.
	public static partial class ReportCli
	{
		private static void RangedStandoff(IReadOnlyList<NamedCapture> captures)
		{
			var samples = new List<(string Capture, string Name, bool Ranged, float Distance)>();
			var noPosition = 0;
			// The counts that make the NEGATIVE result legible: a mob's attack record names no target, so there is
			// nothing to measure a distance TO. Printed rather than dropped, because "0 samples" on its own reads as a
			// broken report instead of as the finding it is.
			int npcAttacks = 0, withTargets = 0;

			foreach (var c in captures)
			{
				// Last known position per handle, and who is an NPC (a spawn with no player_stats), per capture.
				var position = new Dictionary<uint, Vector3>();
				var npcNames = new Dictionary<uint, string>();

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					switch (p.SubOpcode)
					{
						// A spawn seeds the position and says whether the handle is an NPC at all.
						case SimpleCharFullUpdateCodec.Opcode:
						{
							if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
								continue;

							position[spawn.Handle] = spawn.Position;
							if (spawn.Character?.player_stats == null)
								npcNames[spawn.Handle] = spawn.Character?.Name ?? "?";
							continue;
						}

						// ...and every move updates it. This is the only reason the distance is knowable at all: both
						// parties are streamed continuously, so "where were they when the arrow left" is answerable.
						case CharDCMoveCodec.Opcode:
						{
							if (p.TryParse(CharDCMoveCodec.Parse, out var move))
								position[move.Handle] = move.Position;
							continue;
						}

						case BehaviorControlCenterCodec.Opcode:
						{
							if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
								continue;

							var ranged = bcc.BehaviorId == (uint)BehaviorId.CombatRanged;
							if (!ranged && bcc.BehaviorId != (uint)BehaviorId.CombatMelee)
								continue;
							if (!npcNames.TryGetValue(bcc.Actor, out var name)) // players swing too; this is about mobs
								continue;

							npcAttacks++;
							if (!TryReadAttackTargets(bcc, out var targets) || targets.Count == 0)
								continue;

							withTargets++;
							if (!position.TryGetValue(bcc.Actor, out var from))
							{
								noPosition++;
								continue;
							}

							foreach (var t in targets)
							{
								if (!position.TryGetValue(t, out var to))
								{
									noPosition++;
									continue;
								}

								// Horizontal, because that is the plane the chase steps in.
								samples.Add((c.Name, name, ranged, MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z))));
							}

							continue;
						}
					}
				}
			}

			Console.WriteLine("ranged-standoff: the distance a retail mob is at when it attacks (#838)");
			Console.WriteLine("Melee is the CONTROL - the same pipeline must put melee mobs at contact range for the archer figure to mean anything.\n");
			Console.WriteLine($"{npcAttacks} attacks by an NPC; {withTargets} of them name a target at all.");
			if (withTargets == 0)
				Console.WriteLine("=> a mob's attack record carries an EMPTY target list, so retail's own stand-off distance cannot be measured from the corpus (#838).\n");
			Console.WriteLine($"{samples.Count} attack/target pairs with both positions known; {noPosition} skipped (a party never seen moving).\n");

			Report("RANGED (bow/crossbow/thrown)", samples.Where(s => s.Ranged).ToList());
			Report("MELEE (control)", samples.Where(s => !s.Ranged).ToList());

			var archers = samples
				.Where(s => s.Ranged)
				.GroupBy(s => s.Name)
				.OrderByDescending(g => g.Count())
				.Take(8)
				.ToList();
			if (archers.Count > 0)
			{
				Console.WriteLine("per archer:");
				foreach (var g in archers)
				{
					var d = g.Select(s => s.Distance).Order().ToList();
					Console.WriteLine($"  {g.Key,-28} shots={d.Count,4}  median={Pick(d, 0.5):6.1f}  p90={Pick(d, 0.9):6.1f}  max={d[^1]:6.1f}");
				}
			}

			return;

			static void Report(string label, List<(string Capture, string Name, bool Ranged, float Distance)> rows)
			{
				if (rows.Count == 0)
				{
					Console.WriteLine($"{label}: no samples.\n");
					return;
				}

				var d = rows.Select(r => r.Distance).Order().ToList();
				Console.WriteLine($"{label}: {d.Count} attacks from {rows.Select(r => r.Name).Distinct().Count()} creature(s)");
				Console.WriteLine($"  min={d[0]:0.0}  p10={Pick(d, 0.1):0.0}  median={Pick(d, 0.5):0.0}  p90={Pick(d, 0.9):0.0}  max={d[^1]:0.0}");

				// A histogram, because a median alone cannot show whether archers hold ONE distance or shoot at
				// whatever range they happen to be - and those imply different AI.
				var buckets = new[] { 2f, 4f, 6f, 10f, 15f, 20f, 25f, 30f, 40f };
				foreach (var (lo, hi) in buckets.Select((b, i) => (i == 0 ? 0f : buckets[i - 1], b)).Append((buckets[^1], float.MaxValue)))
				{
					var n = d.Count(v => v >= lo && v < hi);
					if (n == 0)
						continue;

					var bar = new string('#', Math.Max(1, n * 40 / d.Count));
					Console.WriteLine($"    {lo,5:0.#} - {(hi == float.MaxValue ? "inf" : hi.ToString("0.#")),-5} {n,5}  {bar}");
				}

				Console.WriteLine();
			}

			static float Pick(List<float> sorted, double q)
			{
				return sorted[Math.Clamp((int)(sorted.Count * q), 0, sorted.Count - 1)];
			}
		}

		// The target handles an attack names: [u32 targetCount][targets x (class, handle)]. The same head
		// `ReportCli.BccAttackTail` reads - kept to that one shape deliberately, so a change to the grammar breaks
		// both rather than silently making one of them print nonsense.
		private static bool TryReadAttackTargets(BehaviorControlCenterIIR bcc, out List<uint> targets)
		{
			targets = [];
			var tail = bcc.Tail;
			if (tail.Length < 4)
				return false;

			var count = (int)tail.ReadU32Be(0);
			if (count <= 0 || 4 + count * 8 > tail.Length)
				return false;

			for (var i = 0; i < count; i++)
				targets.Add(tail.ReadU32Be(4 + i * 8 + 4));

			return true;
		}
	}
}
