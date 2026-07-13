using System;
using System.Buffers.Binary;

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

		public int Width { get; }
		public int Height { get; }

		// World units per grid cell. worldX/Z are divided by this to index the grid. tilemapExtent == W for the
		// full-res playfields (cell = 1); a downsampled map (W < extent) gets cell > 1.
		public float CellSize { get; }

		// World-space origin the grid's (0,0) maps to. The Tortage playfields use local [0, extent] coords (origin 0),
		// validated in-play; kept configurable for any map that offsets its world coordinates.
		public float OriginX { get; }
		public float OriginZ { get; }

		private RdbHeightmap(int width, int height, float[] heights, float cellSize, float originX, float originZ)
		{
			Width = width;
			Height = height;
			_heights = heights;
			CellSize = cellSize;
			OriginX = originX;
			OriginZ = originZ;
		}

		// Parse a decompressed 1066611 resource. `tilemapExtent` is the playfield's world-unit width (from the 1070003
		// <tilemap Width>) used only to derive the cell size; pass 0 (or == W) to assume 1 unit/cell.
		public static bool TryParse(ReadOnlySpan<byte> body, out RdbHeightmap heightmap, float tilemapExtent = 0f, float originX = 0f, float originZ = 0f)
		{
			heightmap = null;
			if (body.Length < 12)
				return false;

			// [0]=levels (1), [4]=W, [8]=H, then W*H float32. The [type][id] RDB-Id header other resources carry is
			// absent here - the floats begin at offset 12 (validated in-play).
			var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
			var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
			if (width <= 0 || height <= 0 || (long)width * height > 64_000_000)
				return false;

			var floatBytes = (long)width * height * 4;
			if (12 + floatBytes > body.Length)
				return false;

			var heights = new float[width * height];
			var grid = body.Slice(12, (int)floatBytes);
			for (var i = 0; i < heights.Length; i++)
				heights[i] = BinaryPrimitives.ReadSingleLittleEndian(grid[(i * 4)..]);

			var cellSize = tilemapExtent > 0f
				? tilemapExtent / width
				: 1f;
			heightmap = new RdbHeightmap(width, height, heights, cellSize, originX, originZ);
			return true;
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
	}
}
