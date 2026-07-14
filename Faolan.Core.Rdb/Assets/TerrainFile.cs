using System.IO;
using System.IO.Compression;

namespace Faolan.Core.Rdb.Assets
{
	// A pre-baked per-map terrain file (#284): the RDB heightmap (1066611) + collision mesh (1000012) bodies, gzipped,
	// so the game server can serve ground-snap + line-of-sight WITHOUT shipping the ~28 GB client RDB. The importer
	// extracts one file per map (ReportCli terrain-export); TerrainService reads it and parses the two bodies with the
	// SAME RdbHeightmap / RdbCollisionMesh parsers it uses against the RDB - the file is just the two decompressed
	// resource bodies (heightmap/collision) re-gzipped, plus the tilemap extent for the heightmap cell size. Either
	// body may be absent (a map with only one). Layout (FaolanStream, big-endian):
	//   [u32 magic 'FTR1'][u32 tilemapExtent][u32 gzLen][gzip heightmap body][u32 gzLen][gzip collision body]
	public static class TerrainFile
	{
		public const string Extension = ".terrain";
		private const uint Magic = 0x46545231; // 'FTR1'

		public sealed record Data(uint TilemapExtent, byte[] Heightmap, byte[] Collision);

		public static void Write(Stream stream, uint tilemapExtent, byte[] heightmapBody, byte[] collisionBody)
		{
			using var fs = new FaolanStream();
			fs.WriteUInt32(Magic);
			fs.WriteUInt32(tilemapExtent);
			fs.WriteArrayUInt32Length(Pack(heightmapBody));
			fs.WriteArrayUInt32Length(Pack(collisionBody));
			fs.Position = 0;
			fs.CopyTo(stream);
		}

		// Reads a terrain file. Returns null if the stream isn't a valid FTR1 file. The Heightmap/Collision bodies are
		// the DECOMPRESSED resource payloads (ready for RdbHeightmap.TryParse / RdbCollisionMesh.TryParse); either may
		// be empty.
		public static Data Read(Stream stream)
		{
			using var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			using var fs = new FaolanStream(buffer.ToArray());
			if (fs.Length < 8 || fs.ReadUInt32() != Magic)
				return null;

			var extent = fs.ReadUInt32();
			var heightmap = Unpack(fs.ReadArrayUInt32Length());
			var collision = Unpack(fs.ReadArrayUInt32Length());
			return new Data(extent, heightmap, collision);
		}

		private static byte[] Pack(byte[] body)
		{
			if (body is not { Length: > 0 })
				return [];

			using var output = new MemoryStream();
			using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
				gz.Write(body);
			return output.ToArray();
		}

		private static byte[] Unpack(byte[] gz)
		{
			if (gz.Length == 0)
				return [];

			using var input = new MemoryStream(gz);
			using var gzs = new GZipStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			gzs.CopyTo(output);
			return output.ToArray();
		}
	}
}
