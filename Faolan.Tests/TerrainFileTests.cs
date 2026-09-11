using System.IO;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// Round-trip for the baked per-map terrain file: the importer writes one FTR1 file per map (heightmap +
	// collision RDB bodies, gzipped) and the game server's TerrainService reads it - the game server never touches the
	// client RDB. Proves Write/Read are a faithful inverse (including the both-empty and one-empty cases) so a
	// regenerated file always decodes back to the exact resource bodies the parsers expect.
	public class TerrainFileTests
	{
		[Fact]
		public void Read_RejectsGarbage()
		{
			using var junk = new MemoryStream([0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);
			Assert.Null(TerrainFile.Read(junk));
		}

		[Fact]
		public void Read_RejectsTooShort()
		{
			using var tiny = new MemoryStream([0x46, 0x54]);
			Assert.Null(TerrainFile.Read(tiny));
		}

		[Fact]
		public void Write_Read_CollisionOnly()
		{
			var collision = new byte[] { 9, 8, 7, 6 };

			RoundTrip(0u, [], collision, out var data);

			Assert.NotNull(data);
			Assert.Empty(data.Heightmap);
			Assert.Equal(collision, data.Collision);
		}

		[Fact]
		public void Write_Read_HeightmapOnly()
		{
			var heightmap = new byte[] { 1, 2, 3, 4, 5 };

			RoundTrip(64u, heightmap, [], out var data);

			Assert.NotNull(data);
			Assert.Equal(64u, data.TilemapExtent);
			Assert.Equal(heightmap, data.Heightmap);
			Assert.Empty(data.Collision);
		}

		[Fact]
		public void Write_Read_RoundTripsBothBodies()
		{
			var heightmap = new byte[512];
			var collision = new byte[777];
			for (var i = 0; i < heightmap.Length; i++)
				heightmap[i] = (byte)(i * 7);
			for (var i = 0; i < collision.Length; i++)
				collision[i] = (byte)(i * 13 + 1);

			RoundTrip(0x0202u, heightmap, collision, out var data);

			Assert.NotNull(data);
			Assert.Equal(0x0202u, data.TilemapExtent);
			Assert.Equal(heightmap, data.Heightmap);
			Assert.Equal(collision, data.Collision);
		}

		private static byte[] RoundTrip(uint extent, byte[] heightmap, byte[] collision, out TerrainFile.Data data)
		{
			using var ms = new MemoryStream();
			TerrainFile.Write(ms, extent, heightmap, collision);
			var bytes = ms.ToArray();

			using var read = new MemoryStream(bytes);
			data = TerrainFile.Read(read)!;
			return bytes;
		}
	}
}
