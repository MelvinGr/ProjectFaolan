using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// --report terrain-los [--report-arg <mapId>[:<maxRange>]]
		//
		// How much of an NPC's nominal aggro range the LINE-OF-SIGHT gate actually leaves it (#577: "agro distance is
		// way too close, practically have to be on top of npc to agro").
		//
		// `WorldState.NearestPlayerInRange` culls any candidate past melee range that fails
		// `TerrainService.HasLineOfSight`, so if that test says "blocked" too often, aggro collapses to exactly
		// `NpcMeleeRange` - which is the reported symptom, and is indistinguishable in-client from the range constant
		// being too small. This measures it OFFLINE, over the real baked terrain and the real spawn positions, instead
		// of A/B-ing a server with and without `TerrainDir`.
		//
		// For each NPC spawn on the map it fires rays to eight compass points at a range of distances, each at the
		// terrain's own ground height (i.e. where a player standing there would be), and tabulates the outcome by
		// distance band. It uses `TerrainService.Trace`, so the numbers come from the SAME code the game tick runs -
		// a re-implementation here would measure a second opinion, not the gate.
		//
		// The attribution is the point: terrain and collision fail for entirely different reasons (a heightmap that
		// reads high, versus a collision mesh that contains the ground it is meant to sit on), and "line-of-sight is
		// culling aggro" is not actionable without knowing which.
		// TerrainService's own eye height, repeated here only to LABEL the datum check's output. It is not used in any
		//calculation - the rays go through TerrainService.Trace - so a drift makes a sentence read oddly, not a number wrong.
		private const float EyeHeightForReading = 1.5f;

		private static async Task TerrainLos(string? arg)
		{
			var spec = (arg ?? "").Split(':', 2, StringSplitOptions.TrimEntries);
			var mapId = spec.Length > 0 && uint.TryParse(spec[0], out var m) ? m : 4010u;
			var maxRange = spec.Length == 2 && float.TryParse(spec[1], out var r) ? r : 20f;

			var terrainDir = _options.TerrainOut ?? CliConfig.Resolve(_options.ConfigOverrides)["TerrainDir"] ?? "Terrain";
			var terrain = new TerrainService(terrainDir, NullLogger.Instance);
			if (!terrain.Available)
			{
				Console.WriteLine($"No baked terrain at '{terrainDir}'. Pass --terrain-out <dir> or set TerrainDir.");
				return;
			}

			await using var db = OpenDb();
			var spawns = await db.NpcSpawns.AsNoTracking().Where(s => s.MapId == mapId)
				.Select(s => new { s.PositionX, s.PositionY, s.PositionZ }).ToListAsync();
			if (spawns.Count == 0)
			{
				Console.WriteLine($"No NPC spawns on map {mapId}.");
				return;
			}

			// Warm + prove the map actually has a bake; without one every ray is trivially clear and the report would
			// read as "the gate is fine" for a map it knows nothing about.
			terrain.Warm(mapId);
			if (terrain.GroundHeight(mapId, spawns[0].PositionX, spawns[0].PositionZ) is null)
				Console.WriteLine($"⚠️ map {mapId} has no heightmap sample at the first spawn - terrain rays will not block.");

			// The DATUM CHECK first, because it decides how to read everything below. The ray starts at the spawn's
			// stored Y + eye height; if that Y sits BELOW the heightmap's own surface, the eye is underground and the
			// very first sample occludes - at any distance, including point-blank. A ray gate cannot be tuned out of
			// that, so a large negative delta here means the two ends are in different vertical datums and the
			// heightmap half of the gate is measuring nothing real.
			var deltas = spawns
				.Select(s => (Ground: terrain.GroundHeight(mapId, s.PositionX, s.PositionZ), s.PositionY))
				.Where(p => p.Ground.HasValue)
				.Select(p => p.PositionY - p.Ground!.Value)
				.OrderBy(d => d)
				.ToList();

			if (deltas.Count > 0)
			{
				Console.WriteLine($"spawn Y minus terrain ground Y, over {deltas.Count} spawns:");
				Console.WriteLine($"    min {deltas[0]:0.##}   p25 {deltas[deltas.Count / 4]:0.##}   median {deltas[deltas.Count / 2]:0.##}"
					+ $"   p75 {deltas[deltas.Count * 3 / 4]:0.##}   max {deltas[^1]:0.##}");
				Console.WriteLine($"    below ground: {deltas.Count(d => d < 0)} of {deltas.Count}"
					+ $"   more than the {EyeHeightForReading}u eye height below: {deltas.Count(d => d < -EyeHeightForReading)}");
				Console.WriteLine();
			}

			// ON-DATUM spawns only: the ones whose stored Y is within an eye height of the heightmap's surface, i.e.
			// the ones for which the ray test is even meaningful. A spawn sitting below the surface (a cave, an
			// interior the heightmap does not model, or a bad row) has its eye underground and blocks EVERY ray at
			// EVERY distance - so mixing them in puts a constant floor under the blocked rate that looks like the gate
			// mis-firing at point-blank range when it is really the input.
			var onDatum = spawns
				.Where(s => terrain.GroundHeight(mapId, s.PositionX, s.PositionZ) is { } g
					&& MathF.Abs(s.PositionY - g) <= EyeHeightForReading)
				.ToList();

			float[] distances = [2f, 4f, 6f, 8f, 10f, 12f, 15f, maxRange];
			var directions = Enumerable.Range(0, 8)
				.Select(i => (float)(i * Math.PI / 4))
				.Select(a => new Vector2(MathF.Cos(a), MathF.Sin(a)))
				.ToArray();

			Console.WriteLine($"map {mapId}: {spawns.Count} NPC spawns x 8 directions, ground-to-ground rays");
			Console.WriteLine($"terrain dir: {terrainDir}");
			Console.WriteLine();

			Table("ALL spawns", spawns.Select(s => new Vector3(s.PositionX, s.PositionY, s.PositionZ)).ToList());
			Console.WriteLine();
			Table($"ON-DATUM spawns only ({onDatum.Count} of {spawns.Count})",
				onDatum.Select(s => new Vector3(s.PositionX, s.PositionY, s.PositionZ)).ToList());

			Console.WriteLine();
			Console.WriteLine("A clear rate that falls off a cliff past melee range IS the reported bug: aggro is then");
			Console.WriteLine("bounded by whichever column is climbing, not by WorldState.NpcAggroRange.");
			return;

			void Table(string title, IReadOnlyList<Vector3> from)
			{
			Console.WriteLine(title);
			Console.WriteLine($"{"range",6} {"rays",8} {"clear",9} {"terrain",9} {"collision",10} {"offgrid",8}");

			foreach (var distance in distances)
			{
				int clear = 0, byTerrain = 0, byCollision = 0, offGrid = 0;
				foreach (var s in from)
				{
					// The NPC end is the spawn's own stored Y; the player end is the terrain's ground height at the
					// target point, which is where a player standing there actually is (the server ground-snaps).
					foreach (var d in directions)
					{
						var x = s.X + d.X * distance;
						var z = s.Z + d.Y * distance;
						if (terrain.GroundHeight(mapId, x, z) is not { } groundY)
						{
							offGrid++;
							continue;
						}

						switch (terrain.Trace(mapId, s, new Vector3(x, groundY, z)))
						{
							case LineOfSightBlocker.None:
								clear++;
								break;
							case LineOfSightBlocker.Terrain:
								byTerrain++;
								break;
							case LineOfSightBlocker.Collision:
								byCollision++;
								break;
						}
					}
				}

				var tested = clear + byTerrain + byCollision;
				Console.WriteLine($"{distance,6:0.#} {tested,8} {Pct(clear, tested),9} {Pct(byTerrain, tested),9} {Pct(byCollision, tested),10} {offGrid,8}");
			}
			}

			static string Pct(int n, int total)
			{
				return total == 0
					? "-"
					: $"{100.0 * n / total:0.0}%";
			}
		}
	}
}
