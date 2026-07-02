using Faolan.Core.Rdb.Assets;

namespace Faolan.TextureUpscaler
{
	// Upscaling for NORMAL maps (#295): bilinear resample + renormalise. A super-resolution model must NOT touch a
	// normal map (it hallucinates detail and breaks the unit-length vectors), so normals go through this instead.
	public static class NormalUpscaler
	{
		public static byte[] BilinearResample(byte[] rgba, int sw, int sh, int dw, int dh)
		{
			return RdbTextureReplacer.Bilinear(rgba, sw, sh, dw, dh);
		}

		// Bilinear resample, then clamp each texel's tangent-space XY (R,G mapped -1..1) back onto/inside the unit
		// circle so Z stays real on decode. The `upscale` delegate shape the replacer expects.
		public static byte[] Renormalizing(byte[] rgba, int sw, int sh, int dw, int dh)
		{
			var dst = RdbTextureReplacer.Bilinear(rgba, sw, sh, dw, dh);
			for (var i = 0; i < dw * dh; i++)
			{
				var nx = dst[i * 4] / 127.5f - 1f;
				var ny = dst[i * 4 + 1] / 127.5f - 1f;
				var len = MathF.Sqrt(nx * nx + ny * ny);
				if (len > 1f)
				{
					nx /= len;
					ny /= len;
					dst[i * 4] = (byte)Math.Clamp((nx + 1f) * 127.5f, 0, 255);
					dst[i * 4 + 1] = (byte)Math.Clamp((ny + 1f) * 127.5f, 0, 255);
				}
			}

			return dst;
		}
	}
}
