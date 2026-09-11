using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using Faolan.Core.Rdb.Assets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Which half of the line-of-sight test rejected a ray, or <see cref="None" /> when it is clear. The two halves
	///     fail for entirely different reasons, so a diagnostic that cannot tell them apart cannot point at a cause.
	/// </summary>
	public enum LineOfSightBlocker
	{
		None,
		Terrain,
		Collision
	}

	/// <summary>
	///     Server-side terrain: ground-height snapping, so loot bags and corpses sit on the floor rather than at a
	///     creature's death-moment Y, and line-of-sight, so NPCs do not aggro over a hill, across a drop or through a
	///     building wall.
	/// </summary>
	/// <remarks>
	///     The data is the per-playfield RDB heightmap (type 1066611) and collision mesh (type 1000012), but the game
	///     server does not read the client RDB: the importer bakes a small gzipped file per map
	///     (<c>--report terrain-export</c>, <c>TerrainFile</c> format) and this service consumes those, lazily per map
	///     and cached.
	///     <para>
	///         It is optional. When the terrain directory is not configured or not present, <see cref="Available" /> is
	///         false and every query degrades gracefully - ground height answers null and line-of-sight answers open -
	///         so the server runs without the baked files. Sampling City of Tortage (4020) at known ground positions
	///         matches the players' Y to within 0.5 units.
	///     </para>
	/// </remarks>
	public sealed class TerrainService
	{
		public const float EyeHeight = 1.5f; // lift both endpoints off the ground so you can see over small bumps

		/// <summary>
		///     How far around a point <see cref="GroundHeightNear" /> may look for the surface the entity was standing
		///     on.
		/// </summary>
		/// <remarks>
		///     Two metres is the width of the thing that steals the sample - a rock, wall or trunk footprint is a cell
		///     or two - and stays well inside the distance over which the beach itself moves: the Tortage sand runs
		///     about 0.1 m per metre, so a 2 m reach costs at most 0.2 m of slope error against the 50 m it recovers.
		/// </remarks>
		private const float GroundSampleRadius = 2f;

		/// <summary>How far terrain must exceed the eye-line before it occludes.</summary>
		/// <remarks>
		///     The slack keeps a gentle slope or a one-unit bump from reading as a wall.
		/// </remarks>
		public const float OcclusionMargin = 1.5f;

		/// <summary>How many intermediate ground samples are taken along the sightline.</summary>
		public const int LineOfSightSamples = 24;

		/// <summary>
		///     Half the width the collision half's three parallel rays span: one eye to eye, and one a shoulder to
		///     either side, with the sightline called occluded only when all three are blocked.
		/// </summary>
		/// <remarks>
		///     A single ray is defeated by anything thinner than a body: on City of Tortage (4020) a fifth of the
		///     collision actors are posts, barrels and pillars, and one of those between an NPC and a player is not
		///     cover but a thing you see past. Half a unit is a human half-shoulder, so the two outer rays bracket the
		///     silhouette a mob would be looking at, while a wall, hut or rock face is wider than the unit the three
		///     rays span and still blocks all of them.
		/// </remarks>
		public const float ShoulderHalfWidth = 0.5f;
		private readonly ConcurrentDictionary<uint, RdbCollisionMesh> _collision = [];

		private readonly ConcurrentDictionary<uint, RdbHeightmap> _heightmaps = [];

		// Maps whose terrain file has already been attempted, present or not, so a query never re-reads it.
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

		/// <summary>
		///     The terrain ground height at world (x, z) on <paramref name="mapId" />, or null when it is unavailable:
		///     no terrain loaded, no heightmap for that map, or a point off the grid. Callers fall back to their own
		///     default.
		/// </summary>
		public float? GroundHeight(uint mapId, float x, float z)
		{
			EnsureLoaded(mapId);
			return _heightmaps.TryGetValue(mapId, out var hm)
				? hm.HeightAt(x, z)
				: null;
		}

		/// <summary>
		///     The ground height under something that was standing at <paramref name="nearY" />: the nearest cell,
		///     unless a taller neighbour explains it better (<c>RdbHeightmap.HeightNear</c>).
		/// </summary>
		/// <remarks>
		///     For a drop or placement beside a rock, a wall or a tree, whose footprint owns the nearest cell at its own
		///     height; see that method for the measured Tortage case.
		/// </remarks>
		public float? GroundHeightNear(uint mapId, float x, float z, float nearY, float tolerance, float radius = GroundSampleRadius)
		{
			EnsureLoaded(mapId);
			return _heightmaps.TryGetValue(mapId, out var hm)
				? hm.HeightNear(x, z, nearY, radius, tolerance)
				: null;
		}

		/// <summary>
		///     Whether nothing blocks the straight eye-line from <paramref name="from" /> to <paramref name="to" />,
		///     typically an NPC to a player: neither the terrain - a hill, a ridge, the far side of a drop - nor the
		///     collision geometry.
		/// </summary>
		/// <remarks>
		///     Both endpoints are lifted to eye height so a viewer can see over small bumps and low sills. It fails
		///     open whenever the relevant asset is unavailable, so aggro still works without the baked terrain: this
		///     only ever removes aggros the range and vertical gates would wrongly have allowed.
		///     <para>
		///         Two things it deliberately does not block on: a post, barrel or pillar narrower than a body (see
		///         <see cref="HasCollisionLineOfSight" />), and the roof over an endpoint the heightmap does not
		///         describe, such as a cave or a cellar (see <see cref="HasTerrainLineOfSight" />).
		///         <c>--report terrain-los</c> measures what it culls over real spawn positions.
		///     </para>
		/// </remarks>
		public bool HasLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			return Trace(mapId, from, to) == LineOfSightBlocker.None;
		}

		/// <summary>
		///     The same test as <see cref="HasLineOfSight" />, but it reports which gate rejected the ray.
		/// </summary>
		/// <remarks>
		///     Aggro that collapses to melee range looks identical whichever half is doing it, and the two have
		///     entirely different causes - a heightmap that reads high, against a collision mesh that contains the
		///     ground it is meant to sit on - so "is line-of-sight culling this" is unanswerable without attribution.
		///     <c>--report terrain-los</c> tabulates it over real spawn positions.
		/// </remarks>
		public LineOfSightBlocker Trace(uint mapId, Vector3 from, Vector3 to)
		{
			EnsureLoaded(mapId);
			if (!HasTerrainLineOfSight(mapId, from, to))
				return LineOfSightBlocker.Terrain;

			return HasCollisionLineOfSight(mapId, from, to)
				? LineOfSightBlocker.None
				: LineOfSightBlocker.Collision;
		}

		/// <summary>
		///     Pre-load a map's terrain into the cache. Called fire-and-forget from world entry, so the first aggro or
		///     loot drop on a map does not pay the file read, parse and grid build inside the game tick.
		/// </summary>
		public void Warm(uint mapId)
		{
			EnsureLoaded(mapId);
		}

		/// <summary>
		///     Load a map's baked terrain file exactly once, present or not, parsing the heightmap and collision bodies
		///     with the same parsers used against the RDB.
		/// </summary>
		private void EnsureLoaded(uint mapId)
		{
			if (_terrainDir == null || !_loaded.TryAdd(mapId, 0))
				return;

			var clock = System.Diagnostics.Stopwatch.StartNew();
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

				// The first-touch cost: file read, gunzip, parse and grid build. Paid once per map, then cached.
				_logger.LogInformation("Terrain: loaded map {Map} (heightmap {Heightmap}, collision {Tris} triangles) in {Ms} ms.", mapId, hasHeightmap
					? "yes"
					: "no", tris, clock.ElapsedMilliseconds);
			}
			catch (Exception ex)
			{
				// Logged at Error, deliberately: a map with no bake at all is a normal, already-handled case - the
				// File.Exists check above simply returns - so everything reaching here is a truncated or corrupt file.
				// The failure is invisible in its effects, because the map then has no collision and
				// HasCollisionLineOfSight answers "clear" for a map it knows nothing about, so line-of-sight passes
				// through walls and NPCs shoot through them while the server looks healthy.
				_logger.LogError(ex, "Terrain: baked terrain for map {Map} failed to load, so collision and line-of-sight are OFF for it. The file is present but unreadable - re-bake it.",
					mapId);
			}
		}

		private bool HasCollisionLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			if (!_collision.TryGetValue(mapId, out var col))
				return true;

			var eyeFrom = new Vector3(from.X, from.Y + EyeHeight, from.Z); // `from with { ... }` would parse as a query
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
				return false; // straight up or down: there is no "beside", so the centre ray is the whole answer

			// Perpendicular to the sightline in the ground plane, applied to both ends so the three rays run parallel.
			var side = new Vector3(-dz / horizontal, 0, dx / horizontal) * ShoulderHalfWidth;
			return !col.SegmentBlocked(eyeFrom + side, eyeTo + side) || !col.SegmentBlocked(eyeFrom - side, eyeTo - side);
		}

		private bool HasTerrainLineOfSight(uint mapId, Vector3 from, Vector3 to)
		{
			if (!_heightmaps.TryGetValue(mapId, out var hm))
				return true;

			var eyeFrom = from.Y + EyeHeight;
			var eyeTo = to.Y + EyeHeight;

			// An endpoint whose eye is under the surface is somewhere the heightmap does not describe - a cave, a
			// cellar, a floor beneath a roof the bake kept - and from there every sample along the ray reads as rock
			// overhead, so the terrain half would occlude at every range including point-blank, collapsing those NPCs
			// to melee-only aggro (7 of the 122 City of Tortage spawns sit 1.5 to 60 units under the grid). The grid
			// has nothing to say about such a ray, so the terrain half stands aside and the collision mesh, which does
			// model the cave wall and the building, is the gate. Sampled with the same neighbourhood search the loot
			// drop uses, so a creature standing beside a rock - whose nearest cell is the rock's top - still reads as
			// on the ground next to it rather than as buried.
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
					continue; // an off-grid sample: never block on missing data

				var eyeLine = eyeFrom + (eyeTo - eyeFrom) * t;
				if (g > eyeLine + OcclusionMargin)
					return false; // terrain rises above the sightline here, so the ray is occluded
			}

			return true;
		}
	}
}
