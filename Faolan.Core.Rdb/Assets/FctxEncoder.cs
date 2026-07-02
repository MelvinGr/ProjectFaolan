using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Writes an AoC FCTX texture container (#295 Phase 2) — the inverse of FctxConverter's read path. Header
	// (24 B): "FCTX", u32 ver=2, u32 unk=1, u16 width, u16 height, u8 mipCount ×2, u16 0, fourcc; then the DXT
	// mip chain SMALLEST-first (the order the client reads). Currently emits DXT1 (opaque diffuse); DXT5/MIXD are
	// follow-ups. The decompressed RDB entry for a texture IS the FCTX bytes (no separate RDB-Id header), so the
	// output of Encode is ready to be IOz1+zlib-wrapped and inlined via RdbWriter.
	public static class FctxEncoder
	{
		// Encode RGBA32 (row-major, top-down) as a full-mip FCTX DXT1 blob.
		public static byte[] EncodeDxt1(byte[] rgba, int width, int height)
		{
			return Encode(rgba, width, height, "DXT1");
		}

		// Encode RGBA32 as a full-mip FCTX blob in `fourCc` (DXT1 = opaque colour, DXT5 = colour + alpha). Mip
		// levels: full res down to 1×1, each dimension = max(1, dim >> level), box-downsampled from the previous.
		public static byte[] Encode(byte[] rgba, int width, int height, string fourCc)
		{
			if (fourCc != "DXT1" && fourCc != "DXT5")
				throw new NotSupportedException($"FCTX encode fourcc '{fourCc}' not supported (DXT1/DXT5 only)");

			var mipCount = MipCount(width, height);
			using var ms = new MemoryStream();

			// Header.
			ms.Write(Encoding.ASCII.GetBytes("FCTX"));
			WriteU32(ms, 2); // version
			WriteU32(ms, 1); // unknown (matches observed FCTX entries)
			WriteU16(ms, (ushort)width);
			WriteU16(ms, (ushort)height);
			ms.WriteByte((byte)mipCount);
			ms.WriteByte((byte)mipCount);
			WriteU16(ms, 0);
			ms.Write(Encoding.ASCII.GetBytes(fourCc));

			// Build every mip's RGBA (level 0 = full res), then write blocks SMALLEST-first.
			var levels = new byte[mipCount][];
			var dims = new (int W, int H)[mipCount];
			levels[0] = rgba;
			dims[0] = (width, height);
			for (var n = 1; n < mipCount; n++)
			{
				var pw = dims[n - 1].W;
				var ph = dims[n - 1].H;
				var nw = Math.Max(1, width >> n);
				var nh = Math.Max(1, height >> n);
				levels[n] = BoxDownsample(levels[n - 1], pw, ph, nw, nh);
				dims[n] = (nw, nh);
			}

			for (var n = mipCount - 1; n >= 0; n--)
				ms.Write(fourCc == "DXT5"
					? BcEncoder.EncodeBc3(levels[n], dims[n].W, dims[n].H)
					: BcEncoder.EncodeBc1(levels[n], dims[n].W, dims[n].H));

			return ms.ToArray();
		}

		// IOz1+zlib-wrap FCTX bytes into the raw on-disk blob a local RDB entry stores (what RdbWriter inlines).
		public static byte[] WrapIOz1(byte[] content)
		{
			using var ms = new MemoryStream();
			ms.Write(Encoding.ASCII.GetBytes("IOz1"));
			WriteU32(ms, (uint)content.Length);
			using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
				zlib.Write(content, 0, content.Length);
			return ms.ToArray();
		}

		private static int MipCount(int w, int h)
		{
			var n = 1;
			while ((w >> n) > 0 || (h >> n) > 0)
				n++;
			return n;
		}

		// Simple 2×2 box downsample (clamped for odd dimensions). Good enough for mip generation; the top mip is
		// what's usually sampled and is authored at full quality by the caller (the upscaled image).
		private static byte[] BoxDownsample(byte[] src, int sw, int sh, int dw, int dh)
		{
			var dst = new byte[dw * dh * 4];
			for (var y = 0; y < dh; y++)
			for (var x = 0; x < dw; x++)
			{
				var x0 = Math.Min(x * 2, sw - 1);
				var x1 = Math.Min(x * 2 + 1, sw - 1);
				var y0 = Math.Min(y * 2, sh - 1);
				var y1 = Math.Min(y * 2 + 1, sh - 1);
				for (var c = 0; c < 4; c++)
				{
					var sum = src[(y0 * sw + x0) * 4 + c] + src[(y0 * sw + x1) * 4 + c] +
					          src[(y1 * sw + x0) * 4 + c] + src[(y1 * sw + x1) * 4 + c];
					dst[(y * dw + x) * 4 + c] = (byte)(sum / 4);
				}
			}

			return dst;
		}

		private static void WriteU32(Stream s, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(b, v);
			s.Write(b);
		}

		private static void WriteU16(Stream s, ushort v)
		{
			Span<byte> b = stackalloc byte[2];
			BinaryPrimitives.WriteUInt16LittleEndian(b, v);
			s.Write(b);
		}
	}
}
