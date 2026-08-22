using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report interest-radius`: how far from the player does retail DESPAWN an entity? (Gitea #856.)
	//
	// `WorldSimulation.PlayerInterestRange = 150 u` is invented, and every viewer-set decision in `WorldBroadcaster`
	// and `InterestManager` turns on it. The previous attempt measured the SPAWN side and got a distribution that is
	// not a radius at all (median 104 u, p99 1343 u, max 1914 u), for two structural reasons this report is built to
	// avoid rather than to repeat:
	//
	//   * ⛔ **the login burst is the map's whole population**, not what is in range, so those distances measure the
	//     playfield;
	//   * ⛔ **a teleport moves the player with no `CharDCMove`**, so the carried-forward position is stale across
	//     every zone line and door in Tortage.
	//
	// ⭐ **Measuring the DESPAWN side removes the first contaminant outright**: nothing bulk-despawns at login, so
	// there is no burst to exclude. And the second is removable by construction rather than by a boundary guess - a
	// teleport despawns EVERYTHING at once, so a despawn arriving alongside many others is a zone change and an
	// ISOLATED one is a walk-out-of-range. That is the filter, and its threshold is printed rather than tuned.
	//
	// ⚠️ What this can and cannot say. A despawn radius is a CEILING on the distance at which the player still had
	// the entity: the server despawns when the player crosses it, so the measured distance is at-or-just-past it. The
	// tightest observations therefore bound it from above, and the spread below them is how far a player travelled
	// between the crossing and the sweep that noticed - which is why the report prints the low percentiles, not the
	// mean.
	public static partial class ReportCli
	{
		// A despawn is attributed to WALKING OUT only if fewer than this many others arrive in the same window. A
		// zone change despawns the whole visible population at once, so the two are separated by count rather than by
		// guessing where a burst boundary lies.
		private const int IsolatedDespawnMax = 3;

		// Packets either side that count as "the same window". Small: a zone change's despawns are contiguous.
		private const int DespawnWindowPackets = 40;

		// A player position older than this many packets is not evidence of where they were - the stale-position
		// contaminant. Movement uploads are frequent, so this is generous and still excludes a teleport gap.
		private const int MaxStalePackets = 60;

		// ⛔ How much FURTHER from the entity the player must have got over the recent window for the despawn to be
		// attributable to interest at all.
		//
		// This is the filter the first run of this report was missing, and its absence produced a confident wrong
		// answer: the ten tightest samples all came in at ~2 u, from captures named
		// `..._all-necro-summons_dismiss-all`, `..._summon_pet` and `..._mount-horse`. A despawn has plenty of causes
		// that are not leaving range - a pet dismissed, a summon expiring, a corpse fading, a mount put away - and
		// every one fires wherever the entity happens to be, which is usually right beside the player.
		//
		// ⚠️ Filtering those by DISTANCE would be circular: it would assume the answer in order to decide what counts
		// as evidence for it. RECEDING is not - an interest despawn is CAUSED by the player crossing outward, so they
		// must have been moving away from that entity however far away the boundary sits. A dismissed pet beside a
		// standing player fails it with no radius assumed anywhere.
		private const float MinRecedingUnits = 10f;

		// How far back the receding comparison looks.
		private const int RecedeWindowPackets = 40;

		// How soon a re-spawn of the same handle means the despawn was a re-stream rather than a range crossing.
		private const int RestreamWindowPackets = 200;

		// How far apart two spawns of one handle must sit before the entity counts as pool-managed rather than placed.
		private const float PoolDriftUnits = 5f;

		// ⛔ A handle that DIED is excluded outright, and this is the contaminant that survives every filter above.
		// A corpse fades on a TIMER, and the shape of a kill is: fight it, loot it, walk on - so the fade lands while
		// the player is genuinely receding, at whatever distance they have reached by then. It passes isolation, it
		// passes staleness, and it passes receding, while measuring the corpse timer rather than any radius.
		//
		// The death record is `DeathBehaviorId` / `DeathSeq`, shared with `--report death-timing` rather than
		// restated here - three reports already filter on that pair and a fourth copy is one more place to drift.

		// Horizontal separation - the plane an interest radius is measured in.
		private static float Flat(Vector3 a, Vector3 b)
		{
			var dx = a.X - b.X;
			var dz = a.Z - b.Z;
			return MathF.Sqrt(dx * dx + dz * dz);
		}

		private static int InterestRadius(IReadOnlyList<NamedCapture> captures)
		{
			var samples = new List<(string Capture, string Name, float Distance)>();
			int quitArrivals = 0, quitParsed = 0, noPosition = 0, notIsolated = 0, stale = 0, approaching = 0, corpses = 0, restreamed = 0, staleEntity = 0, pooled = 0;

			foreach (var capture in captures)
			{
				// `At` is when the position was last stated and `Moved` whether the entity has ever moved since it
				// spawned - together they say whether the position can still be trusted (see the staleness filter).
				var known = new Dictionary<uint, (Vector3 Position, string Name, int At, bool Moved)>();
				var player = Vector3.Zero;
				var playerAt = int.MinValue;

				// The player's recent track, so "were they moving away from THIS entity" is asked per despawn rather
				// than answered once by a scalar speed.
				var track = new List<(int At, Vector3 Position)>();

				// Deaths are collected in the SAME forward walk - a corpse's fade always follows its death, so a set
				// filled as we go is complete by the time any fade is judged.
				var died = new HashSet<uint>();

				// Pass 1: where every despawn sits in the packet stream, so isolation can be judged without
				// re-walking. A despawn's own index is included, so a lone one counts 1. The same walk records where
				// every SPAWN of a handle sits, which is what the re-stream filter below needs to look forwards.
				var quitAt = new List<int>();
				var spawnAt = new Dictionary<uint, List<int>>();
				var spawnedAt = new Dictionary<uint, List<Vector3>>();
				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsDownload: true, IsIirCarrier: true })
						continue;

					if (p.SubOpcode == N3ToClientQuitCodec.Opcode)
						quitAt.Add(i);
					else if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode && p.Body.Length >= SpawnPacketBuilder.PosZOffset + 4)
					{
						var spawned = p.Body.ReadU32Be(SpawnPacketBuilder.HandleOffset);
						if (!spawnAt.TryGetValue(spawned, out var at))
							spawnAt[spawned] = at = [];

						at.Add(i);

						// Where each spawn of this handle PUT it. A placed NPC comes back to the same spot every
						// time; a pool-managed one does not, which is the whole of the test below.
						var where = new Vector3(p.Body.ReadF32Le(SpawnPacketBuilder.PosXOffset), p.Body.ReadF32Le(SpawnPacketBuilder.PosYOffset),
							p.Body.ReadF32Le(SpawnPacketBuilder.PosZOffset));
						if (!spawnedAt.TryGetValue(spawned, out var places))
							spawnedAt[spawned] = places = [];

						places.Add(where);
					}
				}

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (!p.IsIirCarrier)
						continue;

					if (p is { IsDownload: false, SubOpcode: CharDCMoveCodec.Opcode })
					{
						if (p.TryParse(CharDCMoveCodec.Parse, out var move))
						{
							player = move.Position;
							playerAt = i;
							track.Add((i, player));
						}

						continue;
					}

					if (!p.IsDownload)
						continue;

					// ⚠️ An entity MOVES. Judging a despawn against where it SPAWNED measures the player's distance
					// from a stale point, and the walkers - the very population #703 made travel their whole beat -
					// are the ones it is most wrong about. Keep each handle's position current from its own
					// downloaded moves.
					if (p.SubOpcode == CharDCMoveCodec.Opcode && p.TryParse(CharDCMoveCodec.Parse, out var npcMove)
					    && known.TryGetValue(npcMove.Handle, out var tracked))
					{
						known[npcMove.Handle] = (npcMove.Position, tracked.Name, i, true);
						continue;
					}

					// A spawn body: remember where the entity STANDS. ⚠️ The position offsets are relative to `.Body`
					// (which keeps the leading length word), never `.Record` - read off the wrong one they yield a
					// plausible, self-consistent, wrong answer.
					if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode && p.Body.Length >= SpawnPacketBuilder.PosZOffset + 4)
					{
						var handle = p.Body.ReadU32Be(SpawnPacketBuilder.HandleOffset);
						known[handle] = (new Vector3(p.Body.ReadF32Le(SpawnPacketBuilder.PosXOffset), p.Body.ReadF32Le(SpawnPacketBuilder.PosYOffset),
							p.Body.ReadF32Le(SpawnPacketBuilder.PosZOffset)), $"0x{handle:X8}", i, false);
						continue;
					}

					if (p.SubOpcode == BehaviorControlCenterCodec.Opcode)
					{
						if (p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) && bcc.Seq == DeathSeq && bcc.BehaviorId == DeathBehaviorId)
							died.Add(bcc.Actor);

						continue;
					}

					if (p.SubOpcode != N3ToClientQuitCodec.Opcode)
						continue;

					quitArrivals++;
					if (!p.TryParse(N3ToClientQuitCodec.Parse, out var quit))
						continue;

					quitParsed++;

					if (died.Contains(quit.Handle))
					{
						corpses++;
						continue;
					}

					// ⭐ ISOLATION - the whole point. A zone change despawns everything at once; a walk-out-of-range
					// arrives alone or nearly so.
					var neighbours = quitAt.Count(at => Math.Abs(at - i) <= DespawnWindowPackets);
					if (neighbours > IsolatedDespawnMax)
					{
						notIsolated++;
						continue;
					}

					if (i - playerAt > MaxStalePackets)
					{
						stale++;
						continue;
					}

					if (!known.TryGetValue(quit.Handle, out var entity))
					{
						noPosition++;
						continue;
					}

					// ⛔ AMBIENT population, not interest. The two NPC populations behave differently here: a PLACED
					// creature returns to its own spawn point every time, while an AMBIENT one is put back wherever
					// its pool decides. So a handle this capture spawns at two DIFFERENT places is pool-managed, and
					// its despawns are the pool's decisions rather than a radius the player crossed.
					//
					// `Oilskin Scorpion 0x0002DC7B` is the case in point - three spawns at 579.5, 608.1 and 610.3 on
					// one axis - and it was supplying the tightest sample in the report.
					if (spawnedAt.TryGetValue(quit.Handle, out var places) && places.Count > 1
					    && places.Any(a => Flat(a, places[0]) > PoolDriftUnits))
					{
						pooled++;
						continue;
					}

					// ⛔ The ENTITY's position can be stale too, and the report applied this test only to the player.
					// A WANDERER walks while the client is not being told - `Oilskin Scorpion 0x0002DC7B` despawns,
					// and when it comes back 85 s later it stands 29 u from where it was last seen, so the distance
					// measured at its despawn was against a point it had already left. That is the whole of its
					// 24.6 u, the tightest sample the report had.
					//
					// A creature that has NEVER moved is exempt: its spawn position is as good on the last packet as
					// on the first, and those are precisely the samples this measurement wants.
					if (entity.Moved && i - entity.At > MaxStalePackets)
					{
						staleEntity++;
						continue;
					}

					// HORIZONTAL distance: the interest test the server runs is a radius on the ground, and a tall
					// city or a cliff would otherwise add height that no radius is measuring.
					var now = Flat(entity.Position, player);

					// ⛔ A RE-STREAM, not a range crossing. An entity the server hands back almost immediately never
					// left the client's interest set in any meaningful sense: `Oilskin Scorpion 0x0002DC7B` is
					// despawned and re-spawned AT THE SAME POSITION 0.4 s later, and no player crosses a radius twice
					// in 0.4 s. Its 24.6 u was the tightest sample in the whole report and it is not a radius at all.
					//
					// The window is in PACKETS for the same reason the others are - it is the stream's own clock -
					// and it is deliberately short, so a genuine despawn the player walks back into is still counted.
					if (spawnAt.TryGetValue(quit.Handle, out var spawns) && spawns.Any(at => at > i && at - i <= RestreamWindowPackets))
					{
						restreamed++;
						continue;
					}

					// ⛔ RECEDING, or this is a lifecycle despawn wearing an interest despawn's clothes.
					var earlier = track.FindLast(t => i - t.At >= RecedeWindowPackets);
					if (earlier.Position == Vector3.Zero || now - Flat(entity.Position, earlier.Position) < MinRecedingUnits)
					{
						approaching++;
						continue;
					}

					samples.Add((capture.Name, entity.Name, now));
				}
			}

			Console.WriteLine("=== how far away does retail DESPAWN an entity? (#856) ===");
			Console.WriteLine();
			// ⚠️ Counted at ARRIVAL, before every filter, because "no despawn carried this opcode" and "every one was
			// filtered out" would otherwise print the same empty answer - and an empty answer is what this report
			// exists to give.
			Console.WriteLine($"  control - n3ToClientQuit: {quitArrivals,6} arrived, {quitParsed,6} parsed");
			Console.WriteLine($"    dropped: {notIsolated,6} in a burst of >{IsolatedDespawnMax} (a zone change, not a walk)");
			Console.WriteLine($"             {stale,6} with no player move within {MaxStalePackets} packets (stale position)");
			Console.WriteLine($"             {noPosition,6} for a handle whose spawn body this capture never carried");
			Console.WriteLine($"             {corpses,6} for a handle that DIED (a corpse fade is a timer, not a radius)");
			Console.WriteLine($"             {approaching,6} where the player was NOT moving away (a dismissed pet, a summon, a mount)");
			Console.WriteLine($"             {restreamed,6} re-streamed - the same handle spawned again within {RestreamWindowPackets} packets");
			Console.WriteLine($"             {pooled,6} pool-managed - the handle was spawned at more than one PLACE (ambient, not placed)");
			Console.WriteLine($"             {staleEntity,6} whose own position was stale - a WANDERER last stated over {MaxStalePackets} packets ago");
			Console.WriteLine($"  usable:    {samples.Count,6}");
			Console.WriteLine();

			if (samples.Count == 0)
			{
				Console.WriteLine("  ⚠️ Nothing survived the filters, so the corpus cannot answer this either way.");
				Console.WriteLine("     That is an absence of evidence - it says nothing about 150 u being right or wrong.");
				return 1;
			}

			var sorted = samples.Select(s => s.Distance).OrderBy(d => d).ToList();
			string P(double q)
			{
				return $"{sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))]:F0} u";
			}

			Console.WriteLine($"  min {sorted[0]:F0} u   p10 {P(0.10)}   median {P(0.50)}   p90 {P(0.90)}   max {sorted[^1]:F0} u");
			Console.WriteLine();
			Console.WriteLine("  the ten TIGHTEST - a despawn cannot happen INSIDE the radius, so these bound it from above:");
			foreach (var s in samples.OrderBy(s => s.Distance).Take(10))
				Console.WriteLine($"    {s.Distance,7:F1} u   {s.Name}   {s.Capture}");

			Console.WriteLine();
			Console.WriteLine("  ⚠️ Read as a CEILING, not an average. The server despawns when the player crosses the radius, so");
			Console.WriteLine("     every sample sits at-or-just-past it and the SMALLEST ones are the informative end.");
			Console.WriteLine();

			// ⭐ The verdict, and it is a NEGATIVE one that must not be dressed up as a number. A hard radius makes a
			// sharp prediction: a floor with a tight cluster just above it, the width of one server sweep. What is
			// here instead is a smear across nearly two orders of magnitude, so whatever the sample still contains,
			// it is not one radius - and quoting its median as one would be exactly the invented figure #856 exists
			// to complain about.
			var spread = sorted[^1] / MathF.Max(1f, sorted[0]);
			Console.WriteLine($"  ⛔ This does NOT exhibit a radius. A hard cutoff predicts a floor with a tight cluster just above it;");
			Console.WriteLine($"     these span {sorted[0]:F0}..{sorted[^1]:F0} u - a factor of {spread:F0}. So the sample still holds despawn causes");
			Console.WriteLine("     these filters do not name, and the honest reading is that the corpus cannot state the radius.");
			Console.WriteLine("     150 u is neither confirmed nor refuted by it; it is merely near the median, which is not evidence.");
			Console.WriteLine();
			Console.WriteLine("  ⭐ The biggest NAMED contaminant is the ambient pool, and naming it halved the smear: the two NPC");
			Console.WriteLine("     populations behave differently here - a placed creature returns to its own spawn point, an ambient");
			Console.WriteLine("     one is put back wherever its pool decides - so a handle seen spawning at two PLACES is the pool's");
			Console.WriteLine("     business and not a radius the player crossed. Removing those (and the re-streams beside them) took");
			Console.WriteLine("     the spread from 25..850 u to what is printed above. What is left is not yet one radius.");
			Console.WriteLine();
			Console.WriteLine("  ⭐ What WOULD settle it, and what this report is here to measure the moment it exists: one capture of a");
			Console.WriteLine("     player walking slowly AWAY from a known stationary NPC on open ground, no teleports, then back. The");
			Console.WriteLine("     despawn distance is the radius and the re-spawn distance on the way back is its hysteresis.");
			return 0;
		}
	}
}
