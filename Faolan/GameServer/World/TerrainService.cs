using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Server-side terrain from the client RDB heightmap (type 1066611), for #284: ground-height snapping (loot bags /
	// corpses sit on the floor, not at a creature's death-moment Y) and terrain line-of-sight (NPCs don't aggro over a
	// hill or across a big drop). The heightmaps are ~9 MB each (1536² floats) so they are NOT imported into the DB -
	// like Faolan.Web's CharacterModelService we read them straight from the RDB at runtime, lazily per map, cached.
	// Optional: if RdbDir isn't configured (or the client RDB isn't present), Available is false and every query
	// degrades gracefully (GroundHeight -> null, line-of-sight -> true/open) so the server runs without the client
	// assets. Verified in-play: sampling City of Tortage (4020) at known ground positions matches the players' Y to <0.5u.
	public sealed class TerrainService
	{
		private readonly RdbReader _rdb;
		private readonly ILogger _logger;

		// mapId -> heightmap (null cached = no local heightmap for that map, so we don't re-probe every query).
		private readonly ConcurrentDictionary<uint, RdbHeightmap> _maps = new();
		private readonly ConcurrentDictionary<uint, byte> _missing = new();

		// mapId -> collision mesh (walls/buildings, #284 tier-3). Same lazy-load + missing-cache scheme as the heightmap.
		private readonly ConcurrentDictionary<uint, RdbCollisionMesh> _collision = new();
		private readonly ConcurrentDictionary<uint, byte> _collisionMissing = new();

		// LoS sampling: how many intermediate ground samples along the sightline, and how far a hill must rise above
		// the eye-line to actually block (a little slack so a gentle slope or a 1u bump doesn't read as a wall).
		private const int LineOfSightSamples = 24;
		private const float EyeHeight = 1.5f;       // lift both endpoints off the ground so you can see over small bumps
		private const float OcclusionMargin = 1.5f; // terrain must exceed the eye-line by this to occlude

		public bool Available => _rdb != null;

		public TerrainService(string rdbDir, ILogger logger)
		{
			_logger = logger;
			if (string.IsNullOrWhiteSpace(rdbDir) || !Directory.Exists(rdbDir) || !File.Exists(Path.Combine(rdbDir, "le.idx")))
			{
				_logger.LogInformation("Terrain: no client RDB at '{Dir}' - ground-snap + terrain line-of-sight disabled (server runs without it).", rdbDir);
				return;
			}

			try
			{
				_rdb = new RdbReader(rdbDir);
				_logger.LogInformation("Terrain: heightmaps available from RDB '{Dir}' (ground-snap + terrain line-of-sight on).", rdbDir);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Terrain: failed to open RDB '{Dir}' - ground-snap + terrain line-of-sight disabled.", rdbDir);
			}
		}

		// Pre-load a map's terrain assets into the cache. Called fire-and-forget from world entry so the first aggro /
		// loot on a map doesn't pay the ~12 MB decompress + grid build synchronously inside the game tick.
		public void Warm(uint mapId)
		{
			if (_rdb == null)
				return;

			Heightmap(mapId);
			Collision(mapId);
		}

		// The terrain ground height at world (x, z) on `mapId`, or null when unavailable (no RDB, no heightmap for that
		// map, or the point is off the grid). Callers fall back to their own default when null.
		public float? GroundHeight(uint mapId, float x, float z)
		{
			return Heightmap(mapId)?.HeightAt(x, z);
		}

		// True if nothing blocks the straight eye-line from `from` to `to` (an NPC to a player): neither the terrain
		// (a hill / ridge / the far side of a drop) NOR the collision geometry (a building wall). Both endpoints are
		// lifted to eye height so you can see over small bumps / low sills. Fails OPEN (returns true) whenever the
		// relevant asset is unavailable, so aggro still works without the client RDB - this only ever REMOVES aggros
		// that the range + tier-1 vertical gate would have wrongly allowed.
		public bool HasLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			return HasTerrainLineOfSight(mapId, from, to) && HasCollisionLineOfSight(mapId, from, to);
		}

		private bool HasTerrainLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			var hm = Heightmap(mapId);
			if (hm == null)
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

		// True if no collision-mesh wall/building intersects the eye-line. Raycasts the segment against the per-map
		// PhysX trimesh (grid-indexed). Both endpoints at eye height so a chest-high wall occludes but the floor doesn't.
		private bool HasCollisionLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			var col = Collision(mapId);
			if (col == null)
				return true;

			var eyeFrom = new Vector3(from.X, from.Y + EyeHeight, from.Z);
			var eyeTo = new Vector3(to.X, to.Y + EyeHeight, to.Z);
			return !col.SegmentBlocked(eyeFrom, eyeTo);
		}

		private RdbHeightmap Heightmap(uint mapId)
		{
			if (_rdb == null || _missing.ContainsKey(mapId))
				return null;
			if (_maps.TryGetValue(mapId, out var cached))
				return cached;

			var loaded = Load(mapId);
			if (loaded == null)
				_missing.TryAdd(mapId, 0);
			else
				_maps.TryAdd(mapId, loaded);

			return loaded;
		}

		private RdbHeightmap Load(uint mapId)
		{
			try
			{
				if (!_rdb.TryGet(RdbHeightmap.ResourceType, mapId, out var entry) || !entry.IsLocal)
					return null;

				var body = _rdb.ReadDecompressed(entry);
				return RdbHeightmap.TryParse(body, out var hm, TilemapExtent(mapId))
					? hm
					: null;
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Terrain: failed to load heightmap for map {Map}.", mapId);
				return null;
			}
		}

		private RdbCollisionMesh Collision(uint mapId)
		{
			if (_rdb == null || _collisionMissing.ContainsKey(mapId))
				return null;
			if (_collision.TryGetValue(mapId, out var cached))
				return cached;

			var loaded = LoadCollision(mapId);
			if (loaded == null)
				_collisionMissing.TryAdd(mapId, 0);
			else
				_collision.TryAdd(mapId, loaded);

			return loaded;
		}

		private RdbCollisionMesh LoadCollision(uint mapId)
		{
			try
			{
				if (!_rdb.TryGet(RdbCollisionMesh.ResourceType, mapId, out var entry) || !entry.IsLocal)
					return null;

				var body = _rdb.ReadDecompressed(entry);
				if (!RdbCollisionMesh.TryParse(body, out var mesh))
					return null;

				// First-touch cost: decompress (~12 MB) + parse + grid build. One-time per map, then cached.
				_logger.LogInformation("Terrain: loaded collision mesh for map {Map} ({Tris} triangles).", mapId, mesh.TriangleCount);
				return mesh;
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Terrain: failed to load collision mesh for map {Map}.", mapId);
				return null;
			}
		}

		// The playfield's world-unit width, from the 1070003 <tilemap Width>, used to derive the heightmap cell size
		// (extent / gridWidth). Returns 0 if unavailable - RdbHeightmap then assumes 1 unit/cell (true for Tortage).
		private float TilemapExtent(uint mapId)
		{
			try
			{
				if (!_rdb.TryGet(1070003, mapId, out var entry) || !entry.IsLocal)
					return 0f;

				var xml = System.Text.Encoding.Latin1.GetString(_rdb.ReadDecompressed(entry));
				var m = Regex.Match(xml, "<tilemap[^>]*Width=\"(\\d+)\"");
				return m.Success ? float.Parse(m.Groups[1].Value) : 0f;
			}
			catch
			{
				return 0f;
			}
		}
	}
}
