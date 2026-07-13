using System;
using System.Collections.Generic;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The RDB terrain heightmap (type 1066611) parser + world->grid sampling (#284). Format decoded + validated
	// byte-exact against the live client RDB: `[u32 levels=1][u32 W][u32 H]` + a W×H row-major float32 grid (row = Z),
	// sampled as grid[round(z/cell)*W + round(x/cell)]. Real-data check (not hermetic, done manually): City of Tortage
	// (4020) at known player ground positions matches the players' Y to <0.5u. These tests lock the parse + transform.
	public class RdbHeightmapTests
	{
		private static byte[] Build(int w, int h, Func<int, int, float> height, uint levels = 1)
		{
			var bytes = new List<byte>();
			bytes.AddRange(BitConverter.GetBytes(levels));
			bytes.AddRange(BitConverter.GetBytes((uint)w));
			bytes.AddRange(BitConverter.GetBytes((uint)h));
			for (var gz = 0; gz < h; gz++)
			for (var gx = 0; gx < w; gx++)
				bytes.AddRange(BitConverter.GetBytes(height(gx, gz)));
			return bytes.ToArray();
		}

		[Fact]
		public void Parses_And_Samples_RowMajor_By_Z()
		{
			// grid[gz*W + gx] = gz*10 + gx, so the row index is Z and the column is X.
			var body = Build(4, 3, (gx, gz) => gz * 10 + gx);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));
			Assert.Equal(4, hm.Width);
			Assert.Equal(3, hm.Height);
			Assert.Equal(1f, hm.CellSize); // no tilemap extent passed -> 1 unit/cell

			Assert.Equal(0f, hm.HeightAt(0, 0));     // gx0,gz0
			Assert.Equal(3f, hm.HeightAt(3, 0));     // gx3,gz0
			Assert.Equal(12f, hm.HeightAt(2, 1));    // gz1*10 + gx2
			Assert.Equal(22f, hm.HeightAt(2.4f, 1.5f)); // rounds to gx2,gz2 -> 22 (nearest-cell)
		}

		[Fact]
		public void OffGrid_Returns_Null()
		{
			var body = Build(4, 3, (_, _) => 5f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));
			Assert.Null(hm.HeightAt(-1, 0));
			Assert.Null(hm.HeightAt(0, 3));   // gz3 is out of a height-3 grid
			Assert.Null(hm.HeightAt(4, 0));   // gx4 is out of a width-4 grid
		}

		[Fact]
		public void CellSize_From_TilemapExtent_Scales_The_Transform()
		{
			// A downsampled map: 4 samples across an 8-unit playfield -> 2 units/cell.
			var body = Build(4, 1, (gx, _) => gx * 100f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm, tilemapExtent: 8f));
			Assert.Equal(2f, hm.CellSize);
			Assert.Equal(0f, hm.HeightAt(0, 0));     // world 0 -> cell 0
			Assert.Equal(100f, hm.HeightAt(2, 0));   // world 2 -> cell 1
			Assert.Equal(300f, hm.HeightAt(6, 0));   // world 6 -> cell 3
		}

		[Fact]
		public void Rejects_Truncated_Or_Bogus()
		{
			Assert.False(RdbHeightmap.TryParse(new byte[8], out _));            // < 12-byte header
			Assert.False(RdbHeightmap.TryParse(Build(4, 4, (_, _) => 0f)[..20], out _)); // header ok, grid truncated
			var bogus = new List<byte>();
			bogus.AddRange(BitConverter.GetBytes(1u));
			bogus.AddRange(BitConverter.GetBytes(0u)); // W = 0
			bogus.AddRange(BitConverter.GetBytes(4u));
			Assert.False(RdbHeightmap.TryParse(bogus.ToArray(), out _));
		}
	}
}
