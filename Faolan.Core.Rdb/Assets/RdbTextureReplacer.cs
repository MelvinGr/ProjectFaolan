using System;

namespace Faolan.Core.Rdb.Assets
{
	// Ties the texture pipeline together (#295 Phase 2): read an FCTX texture from the RDB, upscale it, re-encode to
	// FCTX/DXT1, and hand back the raw IOz1+zlib blob ready for RdbWriter.InlineEntries. A drop-in higher-res
	// replacement for a (type,id). Bilinear upscale here is a placeholder for an ML upscaler (Real-ESRGAN etc.) — the
	// container/encode/inline path is the point; swap the resampler for quality. DXT1 only for now (opaque diffuse).
	public static class RdbTextureReplacer
	{
		// Build an upscaled DXT1 FCTX replacement blob for the given texture entry, or null if it isn't a decodable
		// DXT-colour FCTX. `scale` multiplies both dimensions (2 = double res). `upscale` replaces the resampler.
		public static byte[]? BuildUpscaledDxt1(RdbReader rdb, uint type, uint id, int scale = 2, Func<byte[], int, int, int, int, byte[]>? upscale = null)
		{
			if (scale < 1 || !rdb.TryGet(type, id, out var e) || !rdb.CanRead(e))
				return null;

			var payload = rdb.ReadDecompressed(e);
			if (!FctxConverter.TryDecodeTopMip(payload, out var rgba, out var w, out var h))
				return null; // not a decodable DXT colour FCTX (MIXD/normal maps return false)

			var nw = w * scale;
			var nh = h * scale;
			var up = (upscale ?? Bilinear)(rgba, w, h, nw, nh);
			return FctxEncoder.WrapIOz1(FctxEncoder.EncodeDxt1(up, nw, nh));
		}

		// Bilinear resample RGBA32 (row-major, top-down) from (sw,sh) to (dw,dh).
		public static byte[] Bilinear(byte[] src, int sw, int sh, int dw, int dh)
		{
			var dst = new byte[dw * dh * 4];
			for (var y = 0; y < dh; y++)
			{
				var fy = (y + 0.5) * sh / dh - 0.5;
				var y0 = (int)Math.Floor(fy);
				var wy = fy - y0;
				var y0c = Math.Clamp(y0, 0, sh - 1);
				var y1c = Math.Clamp(y0 + 1, 0, sh - 1);
				for (var x = 0; x < dw; x++)
				{
					var fx = (x + 0.5) * sw / dw - 0.5;
					var x0 = (int)Math.Floor(fx);
					var wx = fx - x0;
					var x0c = Math.Clamp(x0, 0, sw - 1);
					var x1c = Math.Clamp(x0 + 1, 0, sw - 1);
					var di = (y * dw + x) * 4;
					for (var c = 0; c < 4; c++)
					{
						var a = src[(y0c * sw + x0c) * 4 + c];
						var b = src[(y0c * sw + x1c) * 4 + c];
						var cc = src[(y1c * sw + x0c) * 4 + c];
						var d = src[(y1c * sw + x1c) * 4 + c];
						var top = a + (b - a) * wx;
						var bot = cc + (d - cc) * wx;
						dst[di + c] = (byte)Math.Clamp(top + (bot - top) * wy, 0, 255);
					}
				}
			}

			return dst;
		}
	}
}
