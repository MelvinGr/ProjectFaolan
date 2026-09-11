using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report melee-crowd`: when several retail mobs are on one player at once, how are THEY ARRANGED?
	//
	// Our combat chase walks every attacker to `NpcContactRange` of the target's exact position with no per-attacker
	// offset, so N attackers all solve for the same 1.5u sphere and settle inside one body's worth of space - five
	// Tortage Pirates measured 0.03u-0.46u apart in-client. The fix needs a slot geometry, and what that should look
	// like is a retail question rather than a design one: do captured mobs ring their target, favour an arc, or
	// genuinely overlap?
	//
	// Method deliberately does not go through the attack records. Its sibling `ranged-standoff` established that a
	// mob's `Combat:*` BCC carries an empty target list, so "who is attacking whom" is not recoverable that way. It
	// does not need to be: the question is about POSITIONS, and both parties are streamed continuously (spawn +
	// every `CharDCMove`). So this samples the geometry directly - every moment at which two or more NPCs are within
	// `CrowdRadius` of a player - and asks how far apart they are.
	//
	// Proximity is not proof of aggro. An NPC near a player may be a bystander, which INFLATES separations (a
	// passer-by is further away than a committed attacker). That biases the measurement toward "retail spreads them
	// out", so the number to trust is the low end: if retail mobs genuinely stacked, the minimum separations would
	// be near zero however many bystanders are mixed in. Reported as a distribution for that reason, not a mean.
	public static partial class ReportCli
	{
		// How close an NPC has to be to a player to count as part of the crowd. Wider than our own melee range (3u)
		// so the sample is not defined by our own constant.
		private const float CrowdRadius = 6f;

		// How recently (in packets) an NPC must have moved to count as walking rather than standing about.
		private const int MoverWindow = 400;

		private static void MeleeCrowd(IReadOnlyList<NamedCapture> captures)
		{
			var pairs = new List<float>();
			var moverPairs = new List<float>();
			// How far a walking crowd member stands from the player it is crowding - measured directly, so the ring
			// radius the separations imply can be checked against it rather than inferred and believed.
			var moverRadius = new List<float>();
			var crowds = new List<(string Capture, int Size, float MinGap)>();
			var moverCrowds = new List<(int Size, float MinGap)>();
			var stacked = new List<(string Capture, string A, string B, float Gap)>();
			var exactlyZero = 0;
			var samples = 0;

			foreach (var c in captures)
			{
				var position = new Dictionary<uint, Vector3>();
				var npcNames = new Dictionary<uint, string>();
				var players = new HashSet<uint>();
				// Packet index of each handle's last CharDCMove - the "is it actually walking" filter below.
				var lastMoved = new Dictionary<uint, int>();
				var index = 0;
				// One sample per distinct crowd COMPOSITION per capture: a stationary pile-on streams the same
				// arrangement every tick, and counting it once per packet would let one moment dominate the histogram.
				var seen = new HashSet<string>();

				foreach (var p in c.Packets)
				{
					index++;
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					switch (p.SubOpcode)
					{
						case SimpleCharFullUpdateCodec.Opcode:
						{
							if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
								continue;

							position[spawn.Handle] = spawn.Position;
							if (spawn.Character?.player_stats == null)
								npcNames[spawn.Handle] = spawn.Character?.Name ?? "?";
							else
								players.Add(spawn.Handle);
							continue;
						}

						// `CharDCMove` is the player's movement and only the player's - measured, not assumed:
						// over the whole corpus it carries 30,769 records and not one of them names an NPC handle.
						// So it fixes the crowd's CENTRE and nothing else.
						case CharDCMoveCodec.Opcode:
						{
							if (!p.TryParse(CharDCMoveCodec.Parse, out var move))
								continue;

							position[move.Handle] = move.Position;
							lastMoved[move.Handle] = index;
							break;
						}

						// ...and an NPC moves by MoveTask. Without this the only NPC position available is the one its
						// spawn record streamed in with - where it was standing when it came into view, not where it
						// fought - which would answer a different question while looking like it answered this one.
						case MoveTaskCodec.Opcode:
						{
							if (!p.TryParse(MoveTaskCodec.Parse, out var task))
								continue;

							if (!TryTaskDestination(task, out var to))
								continue;

							position[task.Handle] = to;
							lastMoved[task.Handle] = index;
							break;
						}

						default:
							continue;
					}

					// A position changed - re-evaluate every player's neighbourhood.
					foreach (var player in players)
					{
						if (!position.TryGetValue(player, out var centre))
							continue;

						var near = npcNames.Keys
							.Where(h => position.TryGetValue(h, out var q) && Horizontal(centre, q) <= CrowdRadius)
							.OrderBy(h => h)
							.ToList();
						if (near.Count < 2)
							continue;

						var key = string.Join(",", near.Select(h => $"{h:X}:{position[h].X:0.0}:{position[h].Z:0.0}"));
						if (!seen.Add(key))
							continue;

						samples++;
						var gaps = new List<float>();
						var moverGaps = new List<float>();
						// MOVERS: an NPC that has emitted a CharDCMove in the recent past is walking, which is what a
						// chasing attacker does and a standing townsperson does not. That is the cut that turns a
						// proximity sample into a CHASE sample - and it matters, because the corpus's cities put a dozen
						// stationary citizens inside 6u of the player with no fight in sight.
						var movers = near.Where(h => lastMoved.TryGetValue(h, out var at) && index - at <= MoverWindow).ToList();

						for (var i = 0; i < near.Count; i++)
						{
							for (var j = i + 1; j < near.Count; j++)
							{
								var gap = Horizontal(position[near[i]], position[near[j]]);
								gaps.Add(gap);
								pairs.Add(gap);

								// An exact zero is an artefact, not a measurement: two independently-moving entities
								// do not land on bit-identical X and Z. It means one entity streamed twice (a respawn
								// under a new handle) or a scripted co-location - the encounter lure and its boss sit on
								// one point by design. Counted apart rather than folded into "retail overlaps".
								if (gap == 0f)
									exactlyZero++;
								else if (gap < 0.5f)
									stacked.Add((c.Name, npcNames[near[i]], npcNames[near[j]], gap));
							}
						}

						for (var i = 0; i < movers.Count; i++)
						{
							for (var j = i + 1; j < movers.Count; j++)
							{
								var gap = Horizontal(position[movers[i]], position[movers[j]]);
								if (gap > 0f)
									moverGaps.Add(gap);
							}
						}

						if (movers.Count >= 3)
							moverRadius.AddRange(movers.Select(h => Horizontal(centre, position[h])));

						crowds.Add((c.Name, near.Count, gaps.Min()));
						if (moverGaps.Count > 0)
						{
							moverPairs.AddRange(moverGaps);
							moverCrowds.Add((movers.Count, moverGaps.Min()));
						}
					}
				}
			}

			Console.WriteLine($"melee-crowd: how retail arranges several mobs around one player (#908). Crowd = 2+ NPCs within {CrowdRadius:0}u of a player.\n");
			Console.WriteLine($"{samples} distinct crowd arrangements across {crowds.Select(c => c.Capture).Distinct().Count()} captures; {pairs.Count} NPC-NPC pairs.\n");

			if (pairs.Count == 0)
			{
				Console.WriteLine("=> no crowd ever forms in the corpus, so retail's arrangement cannot be measured from it.");
				return;
			}

			var sorted = pairs.Order().ToList();
			Console.WriteLine("pairwise separation between two NPCs crowding the same player:");
			Console.WriteLine($"  min={sorted[0]:0.00}  p05={Pick(sorted, 0.05):0.00}  p10={Pick(sorted, 0.10):0.00}  median={Pick(sorted, 0.5):0.00}  p90={Pick(sorted, 0.9):0.00}  max={sorted[^1]:0.00}");
			Console.WriteLine();

			// The number that decides the ticket: how often does retail put two mobs inside a body's width of each
			// other? Our own five-pirate pile-on was 0.03u-0.46u, i.e. ENTIRELY inside this bucket.
			foreach (var bound in new[] { 0.5f, 1f, 1.5f, 2f, 3f })
				Console.WriteLine($"  pairs closer than {bound:0.0}u: {pairs.Count(g => g < bound),6} of {pairs.Count} ({100.0 * pairs.Count(g => g < bound) / pairs.Count:0.0}%)");

			Console.WriteLine($"  ({exactlyZero} pairs sat at EXACTLY 0.00u and are excluded from the overlap counts above - see the remark: a bit-identical position is a duplicate stream or a scripted co-location, not two mobs standing on each other.)");

			Console.WriteLine();
			Console.WriteLine("crowd size -> the CLOSEST pair in that arrangement (the separation a slot geometry has to reproduce):");
			foreach (var g in crowds.GroupBy(c => c.Size).OrderBy(g => g.Key))
			{
				var mins = g.Select(c => c.MinGap).Order().ToList();
				Console.WriteLine($"  {g.Key} mobs: {mins.Count,5} arrangements   min={mins[0]:0.00}  median={Pick(mins, 0.5):0.00}  p90={Pick(mins, 0.9):0.00}");
			}

			// The cut that answers the ticket. A CHASE is mobs that are moving; a city square is mobs that are not.
			if (moverPairs.Count > 0)
			{
				var m = moverPairs.Order().ToList();
				Console.WriteLine();
				Console.WriteLine($"WALKING mobs only ({m.Count} pairs) - the chase population, standing crowds excluded:");
				Console.WriteLine($"  min={m[0]:0.00}  p05={Pick(m, 0.05):0.00}  p10={Pick(m, 0.10):0.00}  median={Pick(m, 0.5):0.00}  p90={Pick(m, 0.9):0.00}");
				foreach (var bound in new[] { 0.5f, 1f, 1.5f })
					Console.WriteLine($"  pairs closer than {bound:0.0}u: {moverPairs.Count(g => g < bound),5} of {m.Count} ({100.0 * moverPairs.Count(g => g < bound) / m.Count:0.0}%)");

				// Is it a RING? On a circle of radius R with N evenly-spaced occupants, neighbours sit
				// 2*R*sin(pi/N) apart - so dividing the observed separation by that factor recovers the radius each
				// crowd size implies. If the arrangement is a ring, every size implies the same radius; if mobs simply
				// pack in tighter as more arrive, the implied radius falls away with N. That is the discrimination,
				// and it is the reason to print a derived column at all.
				Console.WriteLine("  by crowd size, the CLOSEST walking pair -> the ring radius it implies:");
				foreach (var g in moverCrowds.Where(c => c.Size >= 2).GroupBy(c => c.Size).OrderBy(g => g.Key).Take(8))
				{
					var mins = g.Select(c => c.MinGap).Order().ToList();
					var median = Pick(mins, 0.5);
					var implied = median / (2f * MathF.Sin(MathF.PI / g.Key));
					Console.WriteLine($"    {g.Key} mobs: {mins.Count,5} arrangements   min={mins[0]:0.00}  median={median:0.00}  p90={Pick(mins, 0.9):0.00}   implied R={implied:0.00}");
				}

				if (moverRadius.Count > 0)
				{
					// ...and the direct measurement of the same thing, which is what makes the implied figure a finding
					// rather than an arithmetic artefact: they have to agree.
					var r = moverRadius.Order().ToList();
					Console.WriteLine();
					Console.WriteLine($"  MEASURED distance from a walking crowd member to the player ({r.Count} samples, crowds of 3+):");
					Console.WriteLine($"    min={r[0]:0.00}  p10={Pick(r, 0.10):0.00}  median={Pick(r, 0.5):0.00}  p90={Pick(r, 0.9):0.00}  max={r[^1]:0.00}");
				}
			}

			if (stacked.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"⚠️ {stacked.Count} pair(s) closer than 0.5u - retail DOES let mobs overlap this far, at least sometimes:");
				foreach (var s in stacked.OrderBy(s => s.Gap).Take(10))
					Console.WriteLine($"  {s.Gap:0.00}u  {s.A} / {s.B}   [{s.Capture}]");
			}

			return;

			static float Horizontal(Vector3 a, Vector3 b)
			{
				return MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
			}

			// Where a move task PUTS the entity: the last waypoint of the deepest node that has any, else the record
			// header's own point. The header is the segment's from (that is what `BroadcastNpcMove` writes), so taking
			// it when a path exists would track the mob one step behind its own walk.
			static bool TryTaskDestination(MoveTaskIIR task, out Vector3 to)
			{
				to = default;
				var node = task.Task;
				var waypoints = Deepest(node);
				if (waypoints is { Count: >= 3 })
				{
					to = new Vector3(
						BitConverter.UInt32BitsToSingle(waypoints[^3]),
						BitConverter.UInt32BitsToSingle(waypoints[^2]),
						BitConverter.UInt32BitsToSingle(waypoints[^1]));
					return true;
				}

				to = new Vector3(
					BitConverter.UInt32BitsToSingle(task.FromX),
					BitConverter.UInt32BitsToSingle(task.FromY),
					BitConverter.UInt32BitsToSingle(task.FromZ));
				return true;
			}

			static List<uint>? Deepest(MoveTaskNode node)
			{
				foreach (var child in node.Children)
				{
					var found = Deepest(child);
					if (found is { Count: >= 3 })
						return found;
				}

				return node.Waypoints.Count >= 3 ? node.Waypoints : null;
			}

			static float Pick(List<float> sorted, double q)
			{
				return sorted[Math.Clamp((int)(sorted.Count * q), 0, sorted.Count - 1)];
			}
		}
	}
}
