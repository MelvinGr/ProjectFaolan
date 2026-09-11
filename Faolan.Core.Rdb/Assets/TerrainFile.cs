using System.IO;
using System.IO.Compression;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     A pre-baked per-map terrain file: the RDB heightmap (1066611) and collision mesh (1000012) bodies, gzipped,
	///     so the game server can serve ground-snap and line-of-sight without shipping the ~28 GB client RDB.
	/// </summary>
	/// <remarks>
	///     The importer extracts one file per map (the terrain-export report); TerrainService reads it and parses the
	///     two bodies with the same <see cref="RdbHeightmap" /> and <see cref="RdbCollisionMesh" /> parsers it uses
	///     against the RDB, because the file is just those two decompressed resource bodies re-gzipped plus the tilemap
	///     extent that gives the heightmap its cell size. Either body may be absent on a map that has only one.
	///     <code>
	///     [u32 magic 'FTR1'][u32 tilemapExtent][u32 gzLen][gzip heightmap body][u32 gzLen][gzip collision body]
	///     </code>
	///     The layout is big-endian, as FaolanStream writes it.
	/// </remarks>
	public static class TerrainFile
	{
		public const string Extension = ".terrain";
		private const uint Magic = 0x46545231; // 'FTR1'

		/// <summary>
		///     Reads a terrain file.
		/// </summary>
		/// <returns>
		///     The parsed data, or null when the stream is not a valid FTR1 file. The heightmap and collision bodies
		///     are the decompressed resource payloads, ready for <see cref="RdbHeightmap.TryParse" /> and
		///     <see cref="RdbCollisionMesh.TryParse" />; either may be empty.
		/// </returns>
		public static Data? Read(Stream stream)
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

		private static byte[] Pack(byte[] body)
		{
			if (body is not { Length: > 0 })
				return [];

			using var output = new MemoryStream();
			using (var gz = new GZipStream(output, CompressionLevel.Optimal, true))
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

		public sealed record Data(uint TilemapExtent, byte[] Heightmap, byte[] Collision);
	}
}
