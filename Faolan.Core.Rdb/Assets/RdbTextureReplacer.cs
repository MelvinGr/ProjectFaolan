using System;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Ties the texture pipeline together (#295 Phase 2): read an FCTX texture from the RDB, upscale it, re-encode to
	// FCTX/DXT1, and hand back the raw IOz1+zlib blob ready for RdbWriter.InlineEntries. A drop-in higher-res
	// replacement for a (type,id). Bilinear upscale here is a placeholder for an ML upscaler (Real-ESRGAN etc.) — the
	// container/encode/inline path is the point; swap the resampler for quality. DXT1 only for now (opaque diffuse).
	public static class RdbTextureReplacer
	{
		// Build an upscaled FCTX replacement blob for the given texture entry, PRESERVING its format (DXT1 or DXT5),
		// or null if it isn't a decodable DXT-colour FCTX (MIXD/normal maps return null). `scale` multiplies both
		// dimensions (2 = double res). `upscale` replaces the resampler (default bilinear).
		public static byte[]? BuildUpscaled(RdbReader rdb, uint type, uint id, int scale = 2, Func<byte[], int, int, int, int, byte[]>? upscale = null)
		{
			if (scale < 1 || !rdb.TryGet(type, id, out var e) || !rdb.CanRead(e))
				return null;

			var payload = rdb.ReadDecompressed(e);
			if (!FctxConverter.IsFctx(payload) || payload.Length < 24)
				return null;

			var fourCc = Encoding.ASCII.GetString(payload, 20, 4);
			var resample = upscale ?? Bilinear;

			if (fourCc == "MIXD")
			{
				// Normal + detail sub-textures, both upscaled; seg1 is regenerated as zero-padding (BC5-path only).
				if (!FctxConverter.TryDecodeMixdNormal(payload, out var nrm, out var w, out var h) ||
				    !FctxConverter.TryDecodeTopMip(payload, out var det, out _, out _, includeSingleChannel: true))
					return null;

				var nw = w * scale;
				var nh = h * scale;
				var upN = resample(nrm, w, h, nw, nh);
				var upDrgba = resample(det, w, h, nw, nh);
				var upD = new byte[nw * nh];
				for (var i = 0; i < nw * nh; i++)
					upD[i] = upDrgba[i * 4]; // detail is single-channel (decoded to R)
				return FctxEncoder.WrapIOz1(FctxEncoder.EncodeMixd(upN, upD, nw, nh));
			}

			if (fourCc != "DXT1" && fourCc != "DXT5")
				return null; // DXT3/unknown - not re-encodable

			if (!FctxConverter.TryDecodeTopMip(payload, out var rgba, out var cw, out var ch))
				return null;

			var cnw = cw * scale;
			var cnh = ch * scale;
			var up = resample(rgba, cw, ch, cnw, cnh);
			return FctxEncoder.WrapIOz1(FctxEncoder.Encode(up, cnw, cnh, fourCc));
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
