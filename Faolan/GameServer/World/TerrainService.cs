using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using Faolan.Core.Rdb.Assets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Which half of the line-of-sight test rejected a ray, or None when it is clear. The two halves fail for entirely
	// different reasons, so a diagnostic that cannot tell them apart cannot point at a cause (#577).
	public enum LineOfSightBlocker
	{
		None,
		Terrain,
		Collision
	}

	// Server-side terrain for #284: ground-height snapping (loot bags / corpses sit on the floor, not at a creature's
	// death-moment Y) and line-of-sight (NPCs don't aggro over a hill / across a drop / through a building wall). The
	// data is the per-playfield RDB heightmap (type 1066611) + collision mesh (type 1000012), but the game server does
	// NOT read the ~28 GB client RDB - the importer bakes a small gzipped file per map (`--report terrain-export`,
	// TerrainFile format) and this service consumes those. Lazily per map, cached. Optional: if the terrain dir isn't
	// configured / present, Available is false and every query degrades gracefully (GroundHeight -> null, line-of-sight
	// -> true/open) so the server runs without the baked files. Verified in-play: sampling City of Tortage (4020) at
	// known ground positions matches the players' Y to <0.5u.
	public sealed class TerrainService
	{
		public const float EyeHeight = 1.5f; // lift both endpoints off the ground so you can see over small bumps

		// How far around a point GroundHeightNear may look for the surface the entity was standing on. 2 m is the width
		// of the thing that steals the sample (a rock/wall/trunk footprint is a cell or two) and stays well inside the
		// distance over which the beach itself moves - the Tortage sand runs ~0.1 m per metre, so a 2 m reach costs at
		// most ~0.2 m of slope error, against the 50 m it recovers.
		private const float GroundSampleRadius = 2f;
		public const float OcclusionMargin = 1.5f; // terrain must exceed the eye-line by this to occlude

		// LoS sampling: how many intermediate ground samples along the sightline, and how far a hill must rise above
		// the eye-line to actually block (a little slack so a gentle slope or a 1u bump doesn't read as a wall).
		public const int LineOfSightSamples = 24;

		// The collision half casts THREE parallel rays - eye to eye, and one a shoulder to either side - and only
		// calls the sightline occluded when all three are. A single ray is defeated by anything thinner than a body:
		// on City of Tortage (4020) a fifth of the collision actors are posts, barrels and pillars, and one of those
		// between an NPC and a player is not cover, it is a thing you see past. Half a unit is a human half-shoulder,
		// so the two outer rays bracket the silhouette a mob would actually be looking at; a wall, a hut or a rock
		// face is wider than the 1 u the three rays span and still blocks all of them (#577).
		public const float ShoulderHalfWidth = 0.5f;
		private readonly ConcurrentDictionary<uint, RdbCollisionMesh> _collision = [];

		private readonly ConcurrentDictionary<uint, RdbHeightmap> _heightmaps = [];

		// Maps whose terrain file we've already attempted to load (present or not), so a query never re-reads it.
		private readonly ConcurrentDictionary<uint, byte> _loaded = [];
		private readonly ILogger _logger;
		private readonly string? _terrainDir;

		public bool Available { get; }

		public TerrainService(string? terrainDir, ILogger logger)
		{
			_logger = logger;
			if (string.IsNullOrWhiteSpace(terrainDir) || !Directory.Exists(terrainDir))
			{
				_logger.LogInformation(
					"Terrain: no baked terrain dir at '{Dir}' - ground-snap + line-of-sight disabled. Generate with: dotnet run --project Faolan.Importer -- --report terrain-export.", terrainDir);
				return;
			}

			_terrainDir = terrainDir;
			Available = true;
			_logger.LogInformation("Terrain: baked terrain files from '{Dir}' (ground-snap + line-of-sight on).", terrainDir);
		}

		// The terrain ground height at world (x, z) on `mapId`, or null when unavailable (no terrain, no heightmap for
		// that map, or the point is off the grid). Callers fall back to their own default when null.
		public float? GroundHeight(uint mapId, float x, float z)
		{
			EnsureLoaded(mapId);
			return _heightmaps.TryGetValue(mapId, out var hm)
				? hm.HeightAt(x, z)
				: null;
		}

		// The ground height under something that was STANDING at `nearY` - the nearest cell unless a taller neighbour
		// explains it better (RdbHeightmap.HeightNear). For a drop/placement beside a rock, a wall or a tree, whose
		// footprint owns the nearest cell at its own height; see that method for the measured Tortage case.
		public float? GroundHeightNear(uint mapId, float x, float z, float nearY, float tolerance, float radius = GroundSampleRadius)
		{
			EnsureLoaded(mapId);
			return _heightmaps.TryGetValue(mapId, out var hm)
				? hm.HeightNear(x, z, nearY, radius, tolerance)
				: null;
		}

		// True if nothing blocks the straight eye-line from `from` to `to` (an NPC to a player): neither the terrain
		// (a hill / ridge / the far side of a drop) NOR the collision geometry (a building wall). Both endpoints are
		// lifted to eye height so you can see over small bumps / low sills. Fails OPEN (returns true) whenever the
		// relevant asset is unavailable, so aggro still works without the baked terrain - this only ever REMOVES aggros
		// that the range + tier-1 vertical gate would have wrongly allowed. Two things it deliberately does NOT block
		// on (#577): a post/barrel/pillar narrower than a body (the collision half casts three rays, see
		// HasCollisionLineOfSight) and the roof over an endpoint the heightmap does not describe (a cave or a cellar;
		// see HasTerrainLineOfSight). `--report terrain-los` measures what it culls over real spawn positions.
		public bool HasLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			return Trace(mapId, from, to) == LineOfSightBlocker.None;
		}

		// The same test as <see cref="HasLineOfSight" />, but it says WHICH gate rejected the ray. Aggro that collapses
		// to melee range looks identical whichever half is doing it, and the two have completely different causes - a
		// heightmap that reads high, versus a collision mesh that contains the ground it is supposed to sit on - so
		// "is line-of-sight culling this" is not an answerable question without attribution. `--report terrain-los`
		// tabulates it over real spawn positions (#577).
		public LineOfSightBlocker Trace(uint mapId, Vector3 from, Vector3 to)
		{
			EnsureLoaded(mapId);
			if (!HasTerrainLineOfSight(mapId, from, to))
				return LineOfSightBlocker.Terrain;

			return HasCollisionLineOfSight(mapId, from, to)
				? LineOfSightBlocker.None
				: LineOfSightBlocker.Collision;
		}

		// Pre-load a map's terrain into the cache. Called fire-and-forget from world entry so the first aggro / loot on
		// a map doesn't pay the file read + parse + grid build synchronously inside the game tick.
		public void Warm(uint mapId)
		{
			EnsureLoaded(mapId);
		}

		// Load a map's baked terrain file exactly once (present or not). Parses the heightmap + collision bodies with
		// the same RdbHeightmap / RdbCollisionMesh parsers used against the RDB.
		private void EnsureLoaded(uint mapId)
		{
			if (_terrainDir == null || !_loaded.TryAdd(mapId, 0))
				return;

			try
			{
				var path = Path.Combine(_terrainDir, $"{mapId}{TerrainFile.Extension}");
				if (!File.Exists(path))
					return;

				TerrainFile.Data? data;
				using (var fs = File.OpenRead(path))
					data = TerrainFile.Read(fs);
				if (data == null)
					return;

				RdbHeightmap? hm = null;
				var hasHeightmap = data.Heightmap.Length > 0 && RdbHeightmap.TryParse(data.Heightmap, out hm, data.TilemapExtent);
				if (hasHeightmap)
					_heightmaps[mapId] = hm!;

				var tris = 0;
				if (data.Collision.Length > 0 && RdbCollisionMesh.TryParse(data.Collision, out var col))
				{
					_collision[mapId] = col;
					tris = col.TriangleCount;
				}

				// First-touch cost: file read + gunzip + parse + grid build. One-time per map, then cached.
				_logger.LogInformation("Terrain: loaded map {Map} (heightmap {Heightmap}, collision {Tris} triangles).", mapId, hasHeightmap
					? "yes"
					: "no", tris);
			}
			catch (Exception ex)
			{
				// LOUD, and at Error: a map with no bake at all is a normal, already-handled case (the File.Exists
				// check above just returns), so everything reaching here is a truncated or corrupt file. The failure is
				// invisible in its effects - the map simply has no collision, and HasCollisionLineOfSight answers
				// "clear" for a map it knows nothing about - so line-of-sight silently passes through walls and NPCs
				// shoot through them. At Debug this looked like a working server.
				_logger.LogError(ex, "Terrain: baked terrain for map {Map} failed to load, so collision and line-of-sight are OFF for it. The file is present but unreadable - re-bake it.",
					mapId);
			}
		}

		private bool HasCollisionLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			if (!_collision.TryGetValue(mapId, out var col))
				return true;

			var eyeFrom = new Vector3(from.X, from.Y + EyeHeight, from.Z); // (`from with {…}` parses as a query)
			var eyeTo = to with
			{
				Y = to.Y + EyeHeight
			};

			// The centre ray decides the common case at the cost of one cast; the two shoulder rays are only paid
			// when it hit something, to ask whether that something is wide enough to hide a body.
			if (!col.SegmentBlocked(eyeFrom, eyeTo))
				return true;

			var dx = to.X - from.X;
			var dz = to.Z - from.Z;
			var horizontal = MathF.Sqrt(dx * dx + dz * dz);
			if (horizontal < 1e-3f)
				return false; // straight up/down: there is no "beside", the centre ray is the whole answer

			// Perpendicular to the sightline in the ground plane, applied to BOTH ends so the three rays run parallel.
			var side = new Vector3(-dz / horizontal, 0, dx / horizontal) * ShoulderHalfWidth;
			return !col.SegmentBlocked(eyeFrom + side, eyeTo + side) || !col.SegmentBlocked(eyeFrom - side, eyeTo - side);
		}

		private bool HasTerrainLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			if (!_heightmaps.TryGetValue(mapId, out var hm))
				return true;

			var eyeFrom = from.Y + EyeHeight;
			var eyeTo = to.Y + EyeHeight;

			// An endpoint whose eye is UNDER the surface is somewhere the heightmap does not describe - a cave, a
			// cellar, a floor beneath a roof the bake kept - and from there every sample along the ray reads as rock
			// overhead, so the terrain half occludes at every range including point-blank. That is the melee-only
			// aggro of #577 for exactly those NPCs (7 of the 122 City of Tortage spawns sit 1.5-60 u under the grid).
			// The grid has nothing to say about such a ray, so the terrain half stands aside and the collision mesh,
			// which does model the cave wall and the building, is the gate. Sampled with the same neighbourhood
			// search the loot drop uses, so a creature standing BESIDE a rock (whose nearest cell is the rock's top)
			// is still read as on the ground next to it, not as buried.
			if (hm.HeightNear(from.X, from.Z, from.Y, GroundSampleRadius, EyeHeight) is { } groundFrom && groundFrom > eyeFrom)
				return true;
			if (hm.HeightNear(to.X, to.Z, to.Y, GroundSampleRadius, EyeHeight) is { } groundTo && groundTo > eyeTo)
				return true;

			for (var i = 1; i < LineOfSightSamples; i++)
			{
				var t = (float)i / LineOfSightSamples;
				var x = from.X + (to.X - from.X) * t;
				var z = from.Z + (to.Z - from.Z) * t;
				var ground = hm.HeightAt(x, z);
				if (ground is not { } g)
					continue; // off-grid sample - don't block on missing data

				var eyeLine = eyeFrom + (eyeTo - eyeFrom) * t;
				if (g > eyeLine + OcclusionMargin)
					return false; // terrain rises above the sightline here -> occluded
			}

			return true;
		}
	}
}
