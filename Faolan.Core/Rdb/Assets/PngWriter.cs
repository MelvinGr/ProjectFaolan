using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Faolan.Core.Rdb.Assets
{
	// Minimal RGBA8 -> PNG encoder (#275 Phase 2): enough to wrap decoded BCn texture data as a PNG that glTF can
	// embed/reference. Writes a single IDAT of zlib-deflated, filter-0 scanlines. No third-party image dependency.
	public static class PngWriter
	{
		private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

		public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
		{
			using var ms = new MemoryStream();
			ms.Write(Signature);

			Span<byte> ihdr = stackalloc byte[13];
			BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
			BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
			ihdr[8] = 8; // bit depth
			ihdr[9] = 6; // colour type RGBA
			ihdr[10] = 0; // deflate
			ihdr[11] = 0; // filter
			ihdr[12] = 0; // no interlace
			WriteChunk(ms, "IHDR", ihdr);

			// Filter byte 0 + RGBA row, all rows, then zlib-compress.
			var raw = new byte[height * (1 + width * 4)];
			for (var y = 0; y < height; y++)
			{
				var dst = y * (1 + width * 4);
				raw[dst] = 0;
				rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(dst + 1));
			}

			using (var comp = new MemoryStream())
			{
				using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
					z.Write(raw);
				WriteChunk(ms, "IDAT", comp.ToArray());
			}

			WriteChunk(ms, "IEND", []);
			return ms.ToArray();
		}

		private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
		{
			Span<byte> len = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
			s.Write(len);

			Span<byte> typeBytes = stackalloc byte[4];
			for (var i = 0; i < 4; i++)
				typeBytes[i] = (byte)type[i];
			s.Write(typeBytes);
			s.Write(data);

			var crc = Crc32(typeBytes, data);
			Span<byte> crcBytes = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
			s.Write(crcBytes);
		}

		private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			var crc = 0xFFFFFFFFu;
			foreach (var x in a)
				crc = Step(crc, x);
			foreach (var x in b)
				crc = Step(crc, x);
			return crc ^ 0xFFFFFFFFu;
		}

		private static uint Step(uint crc, byte x)
		{
			crc ^= x;
			for (var k = 0; k < 8; k++)
				crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
			return crc;
		}
	}
}
