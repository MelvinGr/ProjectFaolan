using System;

namespace Faolan.Core.Rdb.Assets
{
	// Encodes RGBA32 → BC1 (DXT1) block-compressed data — the inverse of BcDecoder, for re-authoring FCTX textures
	// (#295 Phase 2: replace/upscale a texture and write it back into the RDB). Opaque-only (BC1's 1-bit alpha is
	// written as fully-opaque); diffuse/colour atlases are the target. A simple bounding-box endpoint fit per 4×4
	// block (min/max corner in RGB565, 4-colour mode) — not the theoretically-optimal fit, but a valid DXT1 block a
	// GPU decodes correctly, which is what matters for an in-engine texture swap.
	public static class BcEncoder
	{
		// RGBA32 (row-major, top-down) → BC1 blocks (8 bytes per 4×4 tile, left-to-right, top-to-bottom).
		public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height)
		{
			var blocksW = Math.Max(1, (width + 3) / 4);
			var blocksH = Math.Max(1, (height + 3) / 4);
			var outp = new byte[blocksW * blocksH * 8];
			var o = 0;
			Span<byte> block = stackalloc byte[16 * 3]; // 16 texels × RGB

			for (var by = 0; by < blocksH; by++)
			for (var bx = 0; bx < blocksW; bx++)
			{
				// Gather the 4×4 texel RGB (clamp at edges for non-multiple-of-4 sizes).
				for (var py = 0; py < 4; py++)
				for (var px = 0; px < 4; px++)
				{
					var x = Math.Min(bx * 4 + px, width - 1);
					var y = Math.Min(by * 4 + py, height - 1);
					var si = (y * width + x) * 4;
					var di = (py * 4 + px) * 3;
					block[di] = rgba[si];
					block[di + 1] = rgba[si + 1];
					block[di + 2] = rgba[si + 2];
				}

				EncodeBlock(block, outp.AsSpan(o, 8));
				o += 8;
			}

			return outp;
		}

		private static void EncodeBlock(ReadOnlySpan<byte> block, Span<byte> dst)
		{
			// Bounding box of the block's colours = the two endpoints.
			int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
			for (var i = 0; i < 16; i++)
			{
				int r = block[i * 3], g = block[i * 3 + 1], b = block[i * 3 + 2];
				if (r < rMin) rMin = r;
				if (g < gMin) gMin = g;
				if (b < bMin) bMin = b;
				if (r > rMax) rMax = r;
				if (g > gMax) gMax = g;
				if (b > bMax) bMax = b;
			}

			var c0 = To565(rMax, gMax, bMax);
			var c1 = To565(rMin, gMin, bMin);

			// 4-colour mode needs c0 > c1; if equal (flat block) nudge so the decoder doesn't pick 3-colour+black.
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

			// Reconstruct the 4-entry palette exactly as the decoder will, and pick the nearest for each texel.
			Span<int> palR = stackalloc int[4];
			Span<int> palG = stackalloc int[4];
			Span<int> palB = stackalloc int[4];
			From565(c0, out palR[0], out palG[0], out palB[0]);
			From565(c1, out palR[1], out palG[1], out palB[1]);
			palR[2] = (2 * palR[0] + palR[1]) / 3; palG[2] = (2 * palG[0] + palG[1]) / 3; palB[2] = (2 * palB[0] + palB[1]) / 3;
			palR[3] = (palR[0] + 2 * palR[1]) / 3; palG[3] = (palG[0] + 2 * palG[1]) / 3; palB[3] = (palB[0] + 2 * palB[1]) / 3;

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

		private static ushort To565(int r, int g, int b)
		{
			return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
		}

		private static void From565(ushort c, out int r, out int g, out int b)
		{
			// Match the decoder's 5/6/5 → 8-bit expansion (bit-replication).
			var r5 = (c >> 11) & 0x1f;
			var g6 = (c >> 5) & 0x3f;
			var b5 = c & 0x1f;
			r = (r5 << 3) | (r5 >> 2);
			g = (g6 << 2) | (g6 >> 4);
			b = (b5 << 3) | (b5 >> 2);
		}
	}
}
