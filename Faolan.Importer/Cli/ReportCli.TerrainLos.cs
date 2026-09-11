using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// --report terrain-los [--report-arg <mapId>[:<maxRange>[:profile]]]
		//
		// How much of an NPC's nominal aggro range the line-of-sight gate actually leaves it - the gate is what makes
		// an NPC feel like it only aggroes when the player is on top of it.
		//
		// `WorldState.NearestPlayerInRange` culls any candidate past melee range that fails
		// `TerrainService.HasLineOfSight`, so if that test says "blocked" too often, aggro collapses to exactly
		// `NpcMeleeRange` - which is the reported symptom, and is indistinguishable in-client from the range constant
		// being too small. This measures it OFFLINE, over the real baked terrain and the real spawn positions, instead
		// of A/B-ing a server with and without `Content:TerrainDir`.
		//
		// For each NPC spawn on the map it fires rays to eight compass points at a range of distances, each at the
		// terrain's own ground height (i.e. where a player standing there would be), and tabulates the outcome by
		// distance band. It uses `TerrainService.Trace`, so the numbers come from the same code the game tick runs -
		// a re-implementation here would measure a second opinion, not the gate.
		//
		// The attribution is the point: terrain and collision fail for entirely different reasons (a heightmap that
		// reads high, versus a collision mesh that contains the ground it is meant to sit on), and "line-of-sight is
		// culling aggro" is not actionable without knowing which. The tier-1 vertical band is tabulated as its own
		// column, ahead of both, because NearestPlayerInRange applies it first: a ray it culls never reaches the
		// line-of-sight test, so charging it to "terrain" measured a cull the ray gate never made.
		//
		// `:profile` additionally dumps the terrain-blocked rays at maxRange - where along the ray the first occluding
		// sample sits, the target's height relative to the NPC, whether the collision mesh would have caught the same
		// ray, and the ground/eye-line profile of the ones the vertical band would not have culled. That is how the
		// heightmap half was cleared of a sampling defect: every blocked in-band ray is a real dune/rock hump
		// above both eye-lines, or a terrace ledge with the target on the next terrace down.
		// TerrainService's own eye height, used only to LABEL the datum check's output and to pick the on-datum set.
		private const float EyeHeightForReading = TerrainService.EyeHeight;

		private static async Task TerrainLos(string? arg)
		{
			var spec = (arg ?? "").Split(':', 3, StringSplitOptions.TrimEntries);
			var mapId = spec.Length > 0 && uint.TryParse(spec[0], out var m) ? m : 4010u;
			var maxRange = spec.Length >= 2 && float.TryParse(spec[1], out var r) ? r : 20f;
			var profile = spec.Length == 3 && spec[2] == "profile";

			var terrainDir = _options.TerrainOut ?? _options.Configuration[Keys.ContentTerrainDir] ?? "Terrain";
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

			// The DATUM check first, because it decides how to read everything below. The ray starts at the spawn's
			// stored Y + eye height; if that Y sits below the heightmap's own surface, the eye is underground and the
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

			// on-DATUM spawns only: the ones whose stored Y is within an eye height of the heightmap's surface, i.e.
			// the ones for which the ray test is even meaningful. A spawn sitting below the surface (a cave, an
			// interior the heightmap does not model, or a bad row) has its eye underground and blocks every ray at
			// every distance - so mixing them in puts a constant floor under the blocked rate that looks like the gate
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

			if (profile)
				ProfileTerrainBlocks(terrain, mapId, onDatum.Select(s => new Vector3(s.PositionX, s.PositionY, s.PositionZ)).ToList(), directions, maxRange);

			Console.WriteLine();
			Console.WriteLine("A clear rate that falls off a cliff past melee range IS the reported bug: aggro is then");
			Console.WriteLine("bounded by whichever column is climbing, not by WorldState.NpcAggroRange.");
			return;

			void Table(string title, IReadOnlyList<Vector3> from)
			{
			Console.WriteLine(title);
			Console.WriteLine($"{"range",6} {"rays",8} {"clear",9} {"terrain",9} {"collision",10} {"vertical",9} {"offgrid",8}");

			foreach (var distance in distances)
			{
				int clear = 0, byTerrain = 0, byCollision = 0, byVertical = 0, offGrid = 0;
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

						// The gates in the order NearestPlayerInRange applies them: the tier-1 vertical band first, so a
						// target on a terrace 7 u down is culled before line-of-sight is ever asked. Counting it under
						// "terrain" would charge the ray gate for a cull it never made - on City of Tortage that was
						// 89 of the 142 terrain-blocked rays at 20 u.
						if (MathF.Abs(groundY - s.Y) > WorldState.NpcAggroVerticalRange)
						{
							byVertical++;
							continue;
						}

						switch (terrain.Trace(mapId, s, new Vector3(x, groundY, z)))
						{
							case LineOfSightBlocker.None:
							{
								clear++;
								break;
							}

							case LineOfSightBlocker.Terrain:
							{
								byTerrain++;
								break;
							}

							case LineOfSightBlocker.Collision:
							{
								byCollision++;
								break;
							}
						}
					}
				}

				var tested = clear + byTerrain + byCollision + byVertical;
				Console.WriteLine($"{distance,6:0.#} {tested,8} {Pct(clear, tested),9} {Pct(byTerrain, tested),9} {Pct(byCollision, tested),10} {Pct(byVertical, tested),9} {offGrid,8}");
			}
			}

			static string Pct(int n, int total)
			{
				return total == 0
					? "-"
					: $"{100.0 * n / total:0.0}%";
			}
		}

		// `:profile` - for every TERRAIN-blocked ray at maxRange from an on-datum spawn, where along the sightline the
		// first occluding sample sits and how the target's ground relates to the NPC's, then the ground profile of the
		// rays whose target the vertical gate would not already have culled. Re-samples the grid at the same points
		// TerrainService does (its constants are read, not repeated) so a profile line and the gate's verdict agree.
		private static void ProfileTerrainBlocks(TerrainService terrain, uint mapId, IReadOnlyList<Vector3> spawns, Vector2[] directions, float range)
		{
			const int samples = TerrainService.LineOfSightSamples;
			const float eye = TerrainService.EyeHeight;
			const float margin = TerrainService.OcclusionMargin;
			const float vertical = WorldState.NpcAggroVerticalRange;

			var blocked = new List<(Vector3 From, Vector3 To, int FirstIndex, float MaxExcess, float Dy, float[] Ground, float[] EyeLine)>();
			foreach (var s in spawns)
			foreach (var d in directions)
			{
				var to = new Vector3(s.X + d.X * range, 0, s.Z + d.Y * range);
				if (terrain.GroundHeight(mapId, to.X, to.Z) is not { } g)
					continue;

				to.Y = g;
				if (terrain.Trace(mapId, s, to) != LineOfSightBlocker.Terrain)
					continue;

				var ground = new float[samples + 1];
				var eyeLine = new float[samples + 1];
				var first = -1;
				var maxExcess = float.MinValue;
				for (var i = 0; i <= samples; i++)
				{
					var t = (float)i / samples;
					var x = s.X + (to.X - s.X) * t;
					var z = s.Z + (to.Z - s.Z) * t;
					ground[i] = terrain.GroundHeight(mapId, x, z) ?? float.NaN;
					eyeLine[i] = s.Y + eye + (to.Y - s.Y) * t;
					var excess = ground[i] - eyeLine[i];
					if (i == 0 || i == samples)
						continue;

					if (excess > margin && first < 0)
						first = i;
					if (excess > maxExcess)
						maxExcess = excess;
				}

				blocked.Add((s, to, first, maxExcess, to.Y - s.Y, ground, eyeLine));
			}

			Console.WriteLine();
			Console.WriteLine($"TERRAIN-BLOCKED rays at {range:0.#}u from on-datum spawns: {blocked.Count}");
			if (blocked.Count == 0)
				return;

			var nearNpc = blocked.Count(b => b.FirstIndex <= samples / 8);
			var nearTarget = blocked.Count(b => b.FirstIndex >= samples * 7 / 8);
			Console.WriteLine($"    first occluding sample: in the first eighth of the ray {nearNpc}, the last eighth {nearTarget}, in between {blocked.Count - nearNpc - nearTarget}");
			var culledByVertical = blocked.Count(b => MathF.Abs(b.Dy) > vertical);
			Console.WriteLine($"    target ground vs NPC: |dy| > NpcAggroVerticalRange ({vertical}u) for {culledByVertical} - the vertical gate culls those before line-of-sight is asked");
			var dys = blocked.Select(b => b.Dy).OrderBy(v => v).ToList();
			Console.WriteLine($"    dy: min {dys[0]:0.#}  p25 {dys[dys.Count / 4]:0.#}  median {dys[dys.Count / 2]:0.#}  p75 {dys[dys.Count * 3 / 4]:0.#}  max {dys[^1]:0.#}");
			var excesses = blocked.Select(b => b.MaxExcess).OrderBy(v => v).ToList();
			Console.WriteLine($"    max excess over the eye-line: min {excesses[0]:0.#}  median {excesses[excesses.Count / 2]:0.#}  max {excesses[^1]:0.#}  (margin {margin})");

			// Is the hump a PROP (a rock, a hut) that the collision mesh also carries, or bare ground? Trace attributes
			// to terrain first, so this is the only way to see whether the collision half would have caught it too.
			var terrainDir = _options.TerrainOut ?? _options.Configuration[Keys.ContentTerrainDir] ?? "Terrain";
			using var fs = System.IO.File.OpenRead(System.IO.Path.Combine(terrainDir, $"{mapId}{Faolan.Core.Rdb.Assets.TerrainFile.Extension}"));
			var data = Faolan.Core.Rdb.Assets.TerrainFile.Read(fs);
			if (data != null && Faolan.Core.Rdb.Assets.RdbCollisionMesh.TryParse(data.Collision, out var col))
			{
				var alsoCollision = blocked.Count(b => col.SegmentBlocked(b.From with { Y = b.From.Y + eye }, b.To with { Y = b.To.Y + eye }));
				Console.WriteLine($"    also blocked by the collision mesh (eye-to-eye): {alsoCollision} of {blocked.Count}");
			}

			var inBand = blocked.Where(b => MathF.Abs(b.Dy) <= vertical).OrderBy(b => b.MaxExcess).ToList();
			Console.WriteLine();
			Console.WriteLine($"rays whose target is inside the vertical band ({inBand.Count}), lowest excess first - ground / eye-line per sample:");
			foreach (var b in inBand.Take(12))
			{
				Console.WriteLine($"  from ({b.From.X:0.0},{b.From.Y:0.0},{b.From.Z:0.0}) to ({b.To.X:0.0},{b.To.Y:0.0},{b.To.Z:0.0})  dy {b.Dy:0.0}  first@{b.FirstIndex}  max excess {b.MaxExcess:0.0}");
				Console.WriteLine("    ground:  " + string.Join(" ", b.Ground.Select(v => $"{v,6:0.0}")));
				Console.WriteLine("    eyeline: " + string.Join(" ", b.EyeLine.Select(v => $"{v,6:0.0}")));
			}
		}
	}
}
