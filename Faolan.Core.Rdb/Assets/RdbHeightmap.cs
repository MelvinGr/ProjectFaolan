using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	// The per-playfield terrain heightmap (RDB type 1066611), decoded for server-side ground-height + terrain
	// line-of-sight (#284). The decompressed resource is `[u32 levels=1][u32 W][u32 H]` then a W×H row-major float32
	// grid of world-Y heights (row index = Z, column = X: height = grid[gz*W + gx]). One sample per tilemap cell, so
	// the world→grid cell size = tilemapWidth / W (1.0 for the 1536-unit / 1536-sample Tortage playfields). Verified
	// byte-exact against live play: sampling City of Tortage (4020) at known ground positions matches the players'
	// Y to <0.5u. Not TEA-encrypted - a plain IOz1/zlib resource (RdbReader.ReadDecompressed).
	public sealed class RdbHeightmap
	{
		public const uint ResourceType = 1066611;

		private readonly float[] _heights;

		// World units per grid cell. worldX/Z are divided by this to index the grid. tilemapExtent == W for the
		// full-res playfields (cell = 1); a downsampled map (W < extent) gets cell > 1.
		public float CellSize { get; }

		// World-space origin the grid's (0,0) maps to. The Tortage playfields use local [0, extent] coords (origin 0),
		// validated in-play; kept configurable for any map that offsets its world coordinates.
		public float OriginX { get; }
		public float OriginZ { get; }
		public int Height { get; }

		public int Width { get; }

		private RdbHeightmap(int width, int height, float[] heights, float cellSize, float originX, float originZ)
		{
			Width = width;
			Height = height;
			_heights = heights;
			CellSize = cellSize;
			OriginX = originX;
			OriginZ = originZ;
		}

		// The terrain ground height at world (x, z), or null if the point is outside the grid. Nearest-cell sample
		// (the grid is dense enough - 1 unit/cell on Tortage - that bilinear buys little for a loot-bag / LoS use).
		public float? HeightAt(float worldX, float worldZ)
		{
			var gx = (int)MathF.Round((worldX - OriginX) / CellSize);
			var gz = (int)MathF.Round((worldZ - OriginZ) / CellSize);
			if (gx < 0 || gx >= Width || gz < 0 || gz >= Height)
				return null;

			return _heights[gz * Width + gx];
		}

		/// <summary>
		///     The ground height at (x, z) for something KNOWN to be standing on the ground at <paramref name="nearY" />.
		///     The nearest cell, unless it is further than <paramref name="tolerance" /> from that Y - in which case the
		///     cells within <paramref name="radius" /> are searched and the one closest to it wins. Null off the grid.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <see cref="HeightAt" /> takes the single NEAREST cell, and that is the wrong sample next to anything
		///         tall. The heightmap is one height per cell, so a rock formation, a wall or a tree trunk occupies its
		///         footprint at its OWN height: a creature standing at the foot of it can round to a cell that is the
		///         rock's top, 50 m up, while the ground it is actually on sits in the cell one metre away.
		///     </para>
		///     <para>
		///         Measured on Tortage beach (4010): Scavenger Tuut died at Y=145.19 against a nearest cell of
		///         <b>199.63</b> - the sea stack he was standing beside - while the beach cells 1-3 m away read
		///         145.6-146.3. The 54 m delta put the sample past the caller's snap tolerance, so his loot bag kept the
		///         death Y and spawned ~0.6 m UNDER the sand, where it cannot be seen or clicked.
		///     </para>
		///     <para>
		///         The neighbourhood search is a FALLBACK, not the normal path: a nearest cell that is already within
		///         <paramref name="tolerance" /> of the entity is returned untouched, so ordinary ground - flat or
		///         sloped - samples exactly as it always did. Searching unconditionally would be worse than the bug: on
		///         a slope the closest-to-Y candidate is simply whichever cell matches the entity's own height, so the
		///         snap would stop correcting anything.
		///     </para>
		///     <para>
		///         It also does NOT defeat the caller's own tolerance check - a creature genuinely below the surface
		///         (indoors, in a cave, under a bridge) has no nearby cell at its height either, so the best candidate
		///         is still far away and the caller still declines.
		///     </para>
		/// </remarks>
		public float? HeightNear(float worldX, float worldZ, float nearY, float radius, float tolerance)
		{
			if (HeightAt(worldX, worldZ) is not { } nearest)
				return null;

			var cells = (int)MathF.Ceiling(radius / CellSize);
			if (cells <= 0 || MathF.Abs(nearest - nearY) <= tolerance)
				return nearest;

			var gx = (int)MathF.Round((worldX - OriginX) / CellSize);
			var gz = (int)MathF.Round((worldZ - OriginZ) / CellSize);
			var best = nearest;
			for (var dz = -cells; dz <= cells; dz++)
			for (var dx = -cells; dx <= cells; dx++)
			{
				var x = gx + dx;
				var z = gz + dz;
				if (x < 0 || x >= Width || z < 0 || z >= Height)
					continue;

				var candidate = _heights[z * Width + x];
				if (MathF.Abs(candidate - nearY) < MathF.Abs(best - nearY))
					best = candidate;
			}

			return best;
		}

		// Parse a decompressed 1066611 resource. `tilemapExtent` is the playfield's world-unit width (from the 1070003
		// <tilemap Width>) used only to derive the cell size; pass 0 (or == W) to assume 1 unit/cell.
		public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out RdbHeightmap? heightmap, float tilemapExtent = 0f, float originX = 0f, float originZ = 0f)
		{
			heightmap = null;
			if (body.Length < 12)
				return false;

			// [0]=levels (1), [4]=W, [8]=H, then W*H float32. The [type][id] RDB-Id header other resources carry is
			// absent here - the floats begin at offset 12 (validated in-play).
			var width = (int)body.ReadU32Le(4);
			var height = (int)body.ReadU32Le(8);
			if (width <= 0 || height <= 0 || (long)width * height > 64_000_000)
				return false;

			var floatBytes = (long)width * height * 4;
			if (12 + floatBytes > body.Length)
				return false;

			var heights = new float[width * height];
			var grid = body.Slice(12, (int)floatBytes);
			for (var i = 0; i < heights.Length; i++)
				heights[i] = grid.ReadF32Le(i * 4);

			var cellSize = tilemapExtent > 0f
				? tilemapExtent / width
				: 1f;
			heightmap = new RdbHeightmap(width, height, heights, cellSize, originX, originZ);
			return true;
		}
	}
}
