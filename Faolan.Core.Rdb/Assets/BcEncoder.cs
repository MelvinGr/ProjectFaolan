using System;

namespace Faolan.Core.Rdb.Assets
{
	// Encodes RGBA32 → BCn block-compressed data — the inverse of BcDecoder, for re-authoring FCTX textures
	// (#295 Phase 2: replace/upscale a texture and write it back into the RDB). Endpoint fit per 4×4 block is a
	// simple min/max bounding box (colour) / min-max (alpha) — not the theoretically-optimal fit, but a valid block
	// a GPU decodes correctly, which is what matters for an in-engine texture swap.
	//   BC1 (DXT1) — opaque colour.   BC3 (DXT5) — colour + 8-bit alpha.   BC4 — single channel (for MIXD detail).
	public static class BcEncoder
	{
		// RGBA32 (row-major, top-down) → BC1 blocks (8 bytes / 4×4 tile). Opaque (BC1's 1-bit alpha = fully opaque).
		public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height)
		{
			var (bw, bh) = Blocks(width, height);
			var outp = new byte[bw * bh * 8];
			var o = 0;
			Span<byte> rgb = stackalloc byte[16 * 3];
			for (var by = 0; by < bh; by++)
			for (var bx = 0; bx < bw; bx++)
			{
				GatherRgbA(rgba, width, height, bx, by, rgb, default);
				EncodeColorBlock(rgb, outp.AsSpan(o, 8));
				o += 8;
			}

			return outp;
		}

		// RGBA32 → BC3 blocks (16 bytes / 4×4 tile: 8-byte alpha block + 8-byte colour block).
		public static byte[] EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height)
		{
			var (bw, bh) = Blocks(width, height);
			var outp = new byte[bw * bh * 16];
			var o = 0;
			Span<byte> rgb = stackalloc byte[16 * 3];
			Span<byte> a = stackalloc byte[16];
			for (var by = 0; by < bh; by++)
			for (var bx = 0; bx < bw; bx++)
			{
				GatherRgbA(rgba, width, height, bx, by, rgb, a);
				EncodeInterpBlock(a, outp.AsSpan(o, 8));
				EncodeColorBlock(rgb, outp.AsSpan(o + 8, 8));
				o += 16;
			}

			return outp;
		}

		// Single-channel (one byte per texel, row-major) → BC4 blocks (8 bytes / 4×4 tile, same layout as the BC3
		// alpha block). For the MIXD detail/normal channels.
		public static byte[] EncodeBc4(ReadOnlySpan<byte> channel, int width, int height)
		{
			var (bw, bh) = Blocks(width, height);
			var outp = new byte[bw * bh * 8];
			var o = 0;
			Span<byte> v = stackalloc byte[16];
			for (var by = 0; by < bh; by++)
			for (var bx = 0; bx < bw; bx++)
			{
				for (var py = 0; py < 4; py++)
				for (var px = 0; px < 4; px++)
				{
					var x = Math.Min(bx * 4 + px, width - 1);
					var y = Math.Min(by * 4 + py, height - 1);
					v[py * 4 + px] = channel[y * width + x];
				}

				EncodeInterpBlock(v, outp.AsSpan(o, 8));
				o += 8;
			}

			return outp;
		}

		// RGBA32 normal map (R=X, G=Y) → BC5/ATI2 blocks (16 bytes / 4×4 tile: X interp block then Y interp block).
		// The inverse of BcDecoder.DecodeBc5Normal (which reconstructs Z). For the MIXD normal sub-texture.
		public static byte[] EncodeBc5(ReadOnlySpan<byte> rgba, int width, int height)
		{
			var (bw, bh) = Blocks(width, height);
			var outp = new byte[bw * bh * 16];
			var o = 0;
			Span<byte> xs = stackalloc byte[16];
			Span<byte> ys = stackalloc byte[16];
			for (var by = 0; by < bh; by++)
			for (var bx = 0; bx < bw; bx++)
			{
				for (var py = 0; py < 4; py++)
				for (var px = 0; px < 4; px++)
				{
					var x = Math.Min(bx * 4 + px, width - 1);
					var y = Math.Min(by * 4 + py, height - 1);
					var si = (y * width + x) * 4;
					xs[py * 4 + px] = rgba[si]; // R = normal X
					ys[py * 4 + px] = rgba[si + 1]; // G = normal Y
				}

				EncodeInterpBlock(xs, outp.AsSpan(o, 8));
				EncodeInterpBlock(ys, outp.AsSpan(o + 8, 8));
				o += 16;
			}

			return outp;
		}

		private static (int, int) Blocks(int w, int h)
		{
			return (Math.Max(1, (w + 3) / 4), Math.Max(1, (h + 3) / 4));
		}

		// 16 RGB texels → an 8-byte BC1 colour block. Always 4-colour mode (c0 >= c1) - valid for BC1 (opaque) and
		// required for the BC3 colour block.
		private static void EncodeColorBlock(ReadOnlySpan<byte> block, Span<byte> dst)
		{
			int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
			for (var i = 0; i < 16; i++)
			{
				int r = block[i * 3], g = block[i * 3 + 1], b = block[i * 3 + 2];
				if (r < rMin)
					rMin = r;
				if (g < gMin)
					gMin = g;
				if (b < bMin)
					bMin = b;
				if (r > rMax)
					rMax = r;
				if (g > gMax)
					gMax = g;
				if (b > bMax)
					bMax = b;
			}

			var c0 = To565(rMax, gMax, bMax);
			var c1 = To565(rMin, gMin, bMin);

			if (c0 == c1)
			{
				dst[0] = (byte)(c0 & 0xff);
				dst[1] = (byte)(c0 >> 8);
				dst[2] = (byte)(c1 & 0xff);
				dst[3] = (byte)(c1 >> 8);
				dst[4] = dst[5] = dst[6] = dst[7] = 0; // all texels = c0
				return;
			}

			if (c0 < c1)
				(c0, c1) = (c1, c0);

			Span<int> palR = stackalloc int[4];
			Span<int> palG = stackalloc int[4];
			Span<int> palB = stackalloc int[4];
			From565(c0, out palR[0], out palG[0], out palB[0]);
			From565(c1, out palR[1], out palG[1], out palB[1]);
			palR[2] = (2 * palR[0] + palR[1]) / 3;
			palG[2] = (2 * palG[0] + palG[1]) / 3;
			palB[2] = (2 * palB[0] + palB[1]) / 3;
			palR[3] = (palR[0] + 2 * palR[1]) / 3;
			palG[3] = (palG[0] + 2 * palG[1]) / 3;
			palB[3] = (palB[0] + 2 * palB[1]) / 3;

			dst[0] = (byte)(c0 & 0xff);
			dst[1] = (byte)(c0 >> 8);
			dst[2] = (byte)(c1 & 0xff);
			dst[3] = (byte)(c1 >> 8);

			uint indices = 0;
			for (var i = 0; i < 16; i++)
			{
				int r = block[i * 3], g = block[i * 3 + 1], b = block[i * 3 + 2];
				var best = 0;
				var bestD = int.MaxValue;
				for (var p = 0; p < 4; p++)
				{
					var dr = r - palR[p];
					var dg = g - palG[p];
					var db = b - palB[p];
					var d = dr * dr + dg * dg + db * db;
					if (d < bestD)
					{
						bestD = d;
						best = p;
					}
				}

				indices |= (uint)best << (i * 2);
			}

			dst[4] = (byte)(indices & 0xff);
			dst[5] = (byte)((indices >> 8) & 0xff);
			dst[6] = (byte)((indices >> 16) & 0xff);
			dst[7] = (byte)((indices >> 24) & 0xff);
		}

		// 16 single-byte values → an 8-byte interpolated block (BC3 alpha / BC4). Endpoints v0=max, v1=min so the
		// 8-value mode (v0 > v1) is used; a flat block (v0 == v1) maps every texel to index 0 == v0.
		private static void EncodeInterpBlock(ReadOnlySpan<byte> v, Span<byte> dst)
		{
			int vMin = 255, vMax = 0;
			for (var i = 0; i < 16; i++)
			{
				if (v[i] < vMin)
					vMin = v[i];
				if (v[i] > vMax)
					vMax = v[i];
			}

			dst[0] = (byte)vMax; // v0
			dst[1] = (byte)vMin; // v1

			Span<int> pal = stackalloc int[8];
			pal[0] = vMax;
			pal[1] = vMin;
			if (vMax > vMin)
			{
				for (var i = 1; i <= 6; i++)
					pal[i + 1] = ((7 - i) * vMax + i * vMin) / 7;
			}
			else
			{
				for (var i = 2; i < 8; i++)
					pal[i] = vMax; // flat: index 0 = v0 = the flat value, so all-zero indices are exact
			}

			long bits = 0;
			for (var i = 0; i < 16; i++)
			{
				var best = 0;
				var bestD = int.MaxValue;
				for (var p = 0; p < 8; p++)
				{
					var d = Math.Abs(v[i] - pal[p]);
					if (d < bestD)
					{
						bestD = d;
						best = p;
					}
				}

				bits |= (long)best << (i * 3);
			}

			for (var i = 0; i < 6; i++)
				dst[2 + i] = (byte)((bits >> (8 * i)) & 0xff);
		}

		private static void From565(ushort c, out int r, out int g, out int b)
		{
			var r5 = (c >> 11) & 0x1f;
			var g6 = (c >> 5) & 0x3f;
			var b5 = c & 0x1f;
			r = (r5 << 3) | (r5 >> 2);
			g = (g6 << 2) | (g6 >> 4);
			b = (b5 << 3) | (b5 >> 2);
		}

		// Gather a 4×4 tile's RGB (into rgb[16*3]) and, if `alpha` is non-empty, its alpha (into alpha[16]). Edge
		// tiles clamp to the last row/column for non-multiple-of-4 sizes.
		private static void GatherRgbA(ReadOnlySpan<byte> rgba, int w, int h, int bx, int by, Span<byte> rgb, Span<byte> alpha)
		{
			for (var py = 0; py < 4; py++)
			for (var px = 0; px < 4; px++)
			{
				var x = Math.Min(bx * 4 + px, w - 1);
				var y = Math.Min(by * 4 + py, h - 1);
				var si = (y * w + x) * 4;
				var ti = py * 4 + px;
				rgb[ti * 3] = rgba[si];
				rgb[ti * 3 + 1] = rgba[si + 1];
				rgb[ti * 3 + 2] = rgba[si + 2];
				if (!alpha.IsEmpty)
					alpha[ti] = rgba[si + 3];
			}
		}

		private static ushort To565(int r, int g, int b)
		{
			return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
		}
	}
}
