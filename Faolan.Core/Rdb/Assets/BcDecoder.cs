using System;

namespace Faolan.Core.Rdb.Assets
{
	// Decodes BC1 (DXT1) / BC3 (DXT5) block-compressed texture data to RGBA32. Used to turn an AoC FCTX diffuse
	// (DXT1) into a PNG that glTF can reference (#275 Phase 2). Only the top mip is needed for the model preview.
	public static class BcDecoder
	{
		// Decode BC4 / ATI1 (AoC "MIXD") - a single-channel block format (one 8-byte interpolated-value block per 4x4
		// tile, identical to the BC3 alpha block) - to RGBA32 grayscale (R=G=B=value, A=255). Used to inspect/apply the
		// body normal/detail map (#275): the channel carries surface detail (nipples, navel, abs) the diffuse omits.
		public static byte[] DecodeBc4(ReadOnlySpan<byte> blocks, int width, int height)
		{
			var rgba = new byte[width * height * 4];
			var blocksW = Math.Max(1, (width + 3) / 4);
			var blocksH = Math.Max(1, (height + 3) / 4);

			var p = 0;
			for (var by = 0; by < blocksH; by++)
			for (var bx = 0; bx < blocksW; bx++)
			{
				if (p + 8 > blocks.Length)
					return rgba;

				Span<byte> vals = stackalloc byte[16];
				DecodeAlphaBlock(blocks.Slice(p, 8), vals);
				for (var i = 0; i < 16; i++)
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if (x >= width || y >= height)
						continue;
					var o = (y * width + x) * 4;
					rgba[o + 0] = rgba[o + 1] = rgba[o + 2] = vals[i];
					rgba[o + 3] = 255;
				}

				p += 8;
			}

			return rgba;
		}

		// Decode the top-level mip of a BCn payload (block data only, no DDS/FCTX header) to RGBA32 (row-major, top-down).
		public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height, string fourCc)
		{
			var bc3 = fourCc == "DXT3" || fourCc == "DXT5";
			var blockBytes = bc3 ? 16 : 8;
			var rgba = new byte[width * height * 4];
			var blocksW = Math.Max(1, (width + 3) / 4);
			var blocksH = Math.Max(1, (height + 3) / 4);

			var p = 0;
			for (var by = 0; by < blocksH; by++)
			for (var bx = 0; bx < blocksW; bx++)
			{
				if (p + blockBytes > blocks.Length)
					return rgba;

				Span<byte> alpha = stackalloc byte[16];
				if (bc3)
				{
					DecodeAlphaBlock(blocks.Slice(p, 8), alpha);
					DecodeColorBlock(blocks.Slice(p + 8, 8), rgba, width, height, bx * 4, by * 4, alpha, hasAlphaBlock: true);
				}
				else
				{
					DecodeColorBlock(blocks.Slice(p, 8), rgba, width, height, bx * 4, by * 4, alpha, hasAlphaBlock: false);
				}

				p += blockBytes;
			}

			return rgba;
		}

		private static void DecodeColorBlock(ReadOnlySpan<byte> b, byte[] rgba, int width, int height, int px, int py,
			ReadOnlySpan<byte> alpha, bool hasAlphaBlock)
		{
			int c0 = b[0] | (b[1] << 8);
			int c1 = b[2] | (b[3] << 8);
			Span<byte> r = stackalloc byte[4];
			Span<byte> g = stackalloc byte[4];
			Span<byte> bl = stackalloc byte[4];

			(r[0], g[0], bl[0]) = Rgb565(c0);
			(r[1], g[1], bl[1]) = Rgb565(c1);

			// BC1 punch-through alpha only when there's no separate alpha block (BC3 always uses the 4-colour mode).
			var fourColor = hasAlphaBlock || c0 > c1;
			if (fourColor)
			{
				r[2] = (byte)((2 * r[0] + r[1]) / 3); g[2] = (byte)((2 * g[0] + g[1]) / 3); bl[2] = (byte)((2 * bl[0] + bl[1]) / 3);
				r[3] = (byte)((r[0] + 2 * r[1]) / 3); g[3] = (byte)((g[0] + 2 * g[1]) / 3); bl[3] = (byte)((bl[0] + 2 * bl[1]) / 3);
			}
			else
			{
				r[2] = (byte)((r[0] + r[1]) / 2); g[2] = (byte)((g[0] + g[1]) / 2); bl[2] = (byte)((bl[0] + bl[1]) / 2);
				r[3] = g[3] = bl[3] = 0;
			}

			var bits = b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24);
			for (var i = 0; i < 16; i++)
			{
				var x = px + (i & 3);
				var y = py + (i >> 2);
				if (x >= width || y >= height)
					continue;

				var sel = (bits >> (i * 2)) & 3;
				var o = (y * width + x) * 4;
				rgba[o + 0] = r[sel];
				rgba[o + 1] = g[sel];
				rgba[o + 2] = bl[sel];
				rgba[o + 3] = hasAlphaBlock ? alpha[i] : (byte)(!fourColor && sel == 3 ? 0 : 255);
			}
		}

		private static void DecodeAlphaBlock(ReadOnlySpan<byte> b, Span<byte> outAlpha)
		{
			Span<int> a = stackalloc int[8];
			a[0] = b[0];
			a[1] = b[1];
			if (a[0] > a[1])
				for (var i = 1; i <= 6; i++)
					a[i + 1] = ((7 - i) * a[0] + i * a[1]) / 7;
			else
			{
				for (var i = 1; i <= 4; i++)
					a[i + 1] = ((5 - i) * a[0] + i * a[1]) / 5;
				a[6] = 0;
				a[7] = 255;
			}

			long bits = 0;
			for (var i = 0; i < 6; i++)
				bits |= (long)b[2 + i] << (8 * i);

			for (var i = 0; i < 16; i++)
				outAlpha[i] = (byte)a[(int)((bits >> (i * 3)) & 7)];
		}

		private static (byte r, byte g, byte b) Rgb565(int c)
		{
			var r5 = (c >> 11) & 0x1F;
			var g6 = (c >> 5) & 0x3F;
			var b5 = c & 0x1F;
			return ((byte)((r5 * 255 + 15) / 31), (byte)((g6 * 255 + 31) / 63), (byte)((b5 * 255 + 15) / 31));
		}
	}
}
