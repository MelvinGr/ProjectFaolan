using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using Faolan.Core.Rdb.Assets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
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
		private const float EyeHeight = 1.5f; // lift both endpoints off the ground so you can see over small bumps
		private const float OcclusionMargin = 1.5f; // terrain must exceed the eye-line by this to occlude

		// LoS sampling: how many intermediate ground samples along the sightline, and how far a hill must rise above
		// the eye-line to actually block (a little slack so a gentle slope or a 1u bump doesn't read as a wall).
		private const int LineOfSightSamples = 24;
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

		// True if nothing blocks the straight eye-line from `from` to `to` (an NPC to a player): neither the terrain
		// (a hill / ridge / the far side of a drop) NOR the collision geometry (a building wall). Both endpoints are
		// lifted to eye height so you can see over small bumps / low sills. Fails OPEN (returns true) whenever the
		// relevant asset is unavailable, so aggro still works without the baked terrain - this only ever REMOVES aggros
		// that the range + tier-1 vertical gate would have wrongly allowed.
		public bool HasLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			EnsureLoaded(mapId);
			return HasTerrainLineOfSight(mapId, from, to) && HasCollisionLineOfSight(mapId, from, to);
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

			var eyeFrom = new Vector3(from.X, from.Y + EyeHeight, from.Z);
			var eyeTo = new Vector3(to.X, to.Y + EyeHeight, to.Z);
			return !col.SegmentBlocked(eyeFrom, eyeTo);
		}

		private bool HasTerrainLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			if (!_heightmaps.TryGetValue(mapId, out var hm))
				return true;

			var eyeFrom = from.Y + EyeHeight;
			var eyeTo = to.Y + EyeHeight;
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
