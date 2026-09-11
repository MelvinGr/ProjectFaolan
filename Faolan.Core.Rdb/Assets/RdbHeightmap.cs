using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The per-playfield terrain heightmap (RDB type 1066611), decoded for server-side ground height and terrain
	///     line-of-sight.
	/// </summary>
	/// <remarks>
	///     The decompressed resource is <c>[u32 levels=1][u32 W][u32 H]</c> followed by a W×H row-major float32 grid of
	///     world-Y heights, where the row index is Z and the column is X: <c>height = grid[gz * W + gx]</c>. There is
	///     one sample per tilemap cell, so the world-to-grid cell size is <c>tilemapWidth / W</c> — 1.0 for the
	///     1536-unit, 1536-sample Tortage playfields. Sampling City of Tortage (4020) at known ground positions matches
	///     the players' Y to within 0.5 units. The resource is not TEA-encrypted: it is a plain IOz1/zlib body, read
	///     through <see cref="RdbReader.ReadDecompressed" />.
	/// </remarks>
	public sealed class RdbHeightmap
	{
		public const uint ResourceType = 1066611;

		private readonly float[] _heights;

		/// <summary>
		///     World units per grid cell; world X and Z are divided by this to index the grid. The tilemap extent
		///     equals W on the full-resolution playfields, giving a cell size of 1; a downsampled map (W below the
		///     extent) gets a cell size above 1.
		/// </summary>
		public float CellSize { get; }

		/// <summary>
		///     World-space origin that the grid's (0, 0) maps to. The Tortage playfields use local [0, extent]
		///     coordinates, so the origin is 0; it stays configurable for any map that offsets its world coordinates.
		/// </summary>
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

		/// <summary>
		///     The terrain ground height at world (x, z), or null when the point is outside the grid. This is a
		///     nearest-cell sample: the grid is dense enough (one unit per cell on Tortage) that bilinear filtering
		///     buys little for loot-bag placement or line-of-sight.
		/// </summary>
		public float? HeightAt(float worldX, float worldZ)
		{
			var gx = (int)MathF.Round((worldX - OriginX) / CellSize);
			var gz = (int)MathF.Round((worldZ - OriginZ) / CellSize);
			if (gx < 0 || gx >= Width || gz < 0 || gz >= Height)
				return null;

			return _heights[gz * Width + gx];
		}

		/// <summary>
		///     The ground height at (x, z) for something known to be standing on the ground at <paramref name="nearY" />:
		///     the nearest cell, unless it is further than <paramref name="tolerance" /> from that Y, in which case the
		///     cells within <paramref name="radius" /> are searched and the one closest to it wins. Null off the grid.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <see cref="HeightAt" /> takes the single nearest cell, which is the wrong sample next to anything
		///         tall. The heightmap is one height per cell, so a rock formation, a wall or a tree trunk occupies its
		///         footprint at its own height: a creature standing at the foot of it can round to a cell that is the
		///         rock's top, 50 m up, while the ground it is actually on sits in the cell one metre away.
		///     </para>
		///     <para>
		///         Measured on Tortage beach (4010): a creature dying at Y=145.19 has a nearest cell of 199.63 — the sea
		///         stack beside it — while the beach cells 1-3 m away read 145.6-146.3. A 54 m delta like that puts the
		///         sample past the caller's snap tolerance, leaving the loot bag at the death Y roughly 0.6 m under the
		///         sand, where it can be neither seen nor clicked.
		///     </para>
		///     <para>
		///         The neighbourhood search is a fallback, not the normal path: a nearest cell already within
		///         <paramref name="tolerance" /> of the entity is returned untouched, so ordinary ground, flat or
		///         sloped, keeps its plain nearest-cell sample. Searching unconditionally would be worse than no search
		///         at all: on a slope the closest-to-Y candidate is simply whichever cell matches the entity's own
		///         height, so the snap would stop correcting anything.
		///     </para>
		///     <para>
		///         It also does not defeat the caller's own tolerance check: a creature genuinely below the surface —
		///         indoors, in a cave, under a bridge — has no nearby cell at its height either, so the best candidate
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

		/// <summary>
		///     The lower envelope of several 1066611 bodies that describe the same terrain — cell by cell, the lowest
		///     height any of them reports — returned as a body in the same format. The first body fixes the grid, and a
		///     later one sampled differently is skipped rather than fatal.
		/// </summary>
		/// <returns>
		///     False when nothing merged into the first body, in which case the caller keeps that body as it stands.
		/// </returns>
		/// <remarks>
		///     <para>
		///         1066611 is emitted per playfield and bakes that playfield's own static objects into the surface, so
		///         it is not the bare terrain. Playfields that share a tilemap (Tortage Island 4010, City of Tortage
		///         4020/4022 and the End Battle 4030 all name <c>tilemap ID="4040"</c>) are one physical terrain, and
		///         each one's grid is that terrain plus its own props. Props only ever add height, so the lowest
		///         reading across the siblings is the ground under all of them.
		///     </para>
		///     <para>
		///         Measured against positions entities are known to stand on — a logged walk path and the imported NPC
		///         spawns — taking the envelope moves the island from 53%/79% of samples within 1 m to 95%/99%, and the
		///         city-night set from 44% to 70%, while never scoring below the best single grid on any of the four
		///         sets. Each playfield's own grid is the worst one in its neighbours' areas and the best in its own,
		///         which is the fingerprint of per-playfield props rather than of a bad bake.
		///     </para>
		///     <para>
		///         The one thing it cannot preserve is a variant that genuinely digs: where a sibling lowers the
		///         terrain, the envelope keeps the dug version for every playfield. That is a deliberate trade, taken
		///         because it is measurable and the alternative is not — 4.9% of Tortage's cells read lower in 4010
		///         than in 4020.
		///     </para>
		/// </remarks>
		public static bool TryLowerEnvelope(IReadOnlyList<byte[]> bodies, [NotNullWhen(true)] out byte[]? merged)
		{
			merged = null;
			ArgumentNullException.ThrowIfNull(bodies);

			float[]? lowest = null;
			byte[]? shape = null;
			var contributors = 0;
			foreach (var body in bodies)
			{
				if (!TryParse(body, out var hm))
					continue;

				if (lowest == null)
				{
					shape = body;
					lowest = (float[])hm._heights.Clone();
					contributors = 1;
					continue;
				}

				// A sibling sampled at a different resolution is a different grid, not a variant of this one: there is
				// no cell-to-cell correspondence to take a minimum over, so it is skipped. Skipping it must not veto
				// the merge — Tortage's tilemap group is 4010/4015/4020/4022/4030/4040 and 4022 alone is a 768² grid
				// against the others' 1536², so rejecting the whole group over it would leave the island unmerged.
				if (hm._heights.Length != lowest.Length)
					continue;

				contributors++;
				for (var i = 0; i < lowest.Length; i++)
					if (hm._heights[i] < lowest[i])
						lowest[i] = hm._heights[i];
			}

			// Nothing to merge with: hand back false so the caller keeps the original body rather than rewriting it
			// into an identical one and reporting a merge that did not happen.
			if (lowest == null || shape == null || contributors < 2)
				return false;

			// Same layout back out: the 12-byte header of whichever body defined the shape, then the merged grid.
			var body2 = new byte[12 + lowest.Length * 4];
			shape.AsSpan(0, 12).CopyTo(body2);
			for (var i = 0; i < lowest.Length; i++)
				body2.WriteF32Le(12 + i * 4, lowest[i]);

			merged = body2;
			return true;
		}

		/// <summary>
		///     Parses a decompressed 1066611 resource.
		/// </summary>
		/// <param name="tilemapExtent">
		///     The playfield's world-unit width, from the 1070003 <c>&lt;tilemap Width&gt;</c>, used only to derive the
		///     cell size. Pass 0, or a value equal to W, to assume one unit per cell.
		/// </param>
		public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out RdbHeightmap? heightmap, float tilemapExtent = 0f, float originX = 0f, float originZ = 0f)
		{
			heightmap = null;
			if (body.Length < 12)
				return false;

			// [0] = levels (1), [4] = W, [8] = H, then W * H float32. The [type][id] RDB-Id header other resources
			// carry is absent here, so the floats begin at offset 12.
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
