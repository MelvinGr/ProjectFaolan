using System;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Ties the texture pipeline together: reads an FCTX texture from the RDB, upscales it, re-encodes it to FCTX
	///     and hands back the raw IOz1+zlib blob ready for <see cref="RdbWriter" /> to inline, giving a drop-in
	///     higher-resolution replacement for a (type, id).
	/// </summary>
	/// <remarks>
	///     The bilinear resampler is a default, not a quality target: the container, encode and inline path is what
	///     this class owns, and a caller passes its own upscaler in.
	///     <para>
	///         Nothing in the solution calls this, so it is unexercised and should be re-verified against a real
	///         texture before being relied on. The parts it drives — <see cref="FctxConverter" />,
	///         <see cref="FctxEncoder" /> and <see cref="RdbWriter" /> — are used and covered elsewhere.
	///         Open work tracked in Gitea #295.
	///     </para>
	/// </remarks>
	public static class RdbTextureReplacer
	{
		/// <summary>
		///     Bilinearly resamples RGBA32 (row-major, top-down) from (sw, sh) to (dw, dh).
		/// </summary>
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

		/// <summary>
		///     Builds an upscaled FCTX replacement blob for the given texture entry, preserving its format.
		/// </summary>
		/// <param name="scale">Multiplies both dimensions; 2 doubles the resolution.</param>
		/// <param name="upscale">Replaces the resampler; <see cref="Bilinear" /> by default.</param>
		/// <returns>Null when the entry is not a re-encodable FCTX.</returns>
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
				// Normal and detail sub-textures, both upscaled; seg1 is regenerated as zero padding, which only the
				// BC5 path reads.
				if (!FctxConverter.TryDecodeMixdNormal(payload, out var nrm, out var w, out var h) || !FctxConverter.TryDecodeTopMip(payload, out var det, out _, out _, true))
					return null;

				var nw = w * scale;
				var nh = h * scale;
				var upN = resample(nrm, w, h, nw, nh);
				var upDrgba = resample(det, w, h, nw, nh);
				var upD = new byte[nw * nh];
				for (var i = 0; i < nw * nh; i++)
					upD[i] = upDrgba[i * 4]; // The detail map is single-channel, decoded into R.
				return FctxEncoder.WrapIOz1(FctxEncoder.EncodeMixd(upN, upD, nw, nh));
			}

			if (fourCc != "DXT1" && fourCc != "DXT5")
				return null; // DXT3 and unknown fourccs are not re-encodable.

			if (!FctxConverter.TryDecodeTopMip(payload, out var rgba, out var cw, out var ch))
				return null;

			var cnw = cw * scale;
			var cnh = ch * scale;
			var up = resample(rgba, cw, ch, cnw, cnh);
			return FctxEncoder.WrapIOz1(FctxEncoder.Encode(up, cnw, cnh, fourCc));
		}
	}
}
