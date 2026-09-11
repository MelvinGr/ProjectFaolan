using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Pads a body diffuse atlas: fills the flat tan background gutter between the skin islands with the nearest
	///     skin colour and then smooths that fill, so the character mesh's UV-island edge triangles sample skin instead
	///     of the flat gutter. Only gutter pixels are changed — island pixels are preserved exactly — and results are
	///     cached per source atlas.
	/// </summary>
	/// <remarks>
	///     The raw RDB atlas lacks the edge padding a game engine applies at texture-build time, so without this the
	///     viewer shows lighter seam and blotch artifacts along the body midline, spine, waist and island boundaries.
	///     <para>
	///         Which pixels are gutter comes from the mesh wherever the caller can supply it: a texel no UV triangle
	///         covers is never drawn on the character, so filling it cannot lose anything, and a texel one does cover
	///         is skin by definition. That is a measurement, where the colour and flatness test is a guess with a known
	///         failure — the tan background is close enough to tan skin that a smooth patch of upper chest passes both
	///         guards and gets flattened, leaving a residual blotch. The heuristic remains the path for a caller with
	///         no mesh.
	///     </para>
	/// </remarks>
	public static class BodyAtlasDilator
	{
		/// <summary>
		///     How close to the sampled background colour a pixel must be, per channel, to count as gutter.
		/// </summary>
		private const int BackgroundTolerance = 14;

		private static readonly ConcurrentDictionary<long, byte[]> Cache = new ConcurrentDictionary<long, byte[]>();

		/// <summary>
		///     The blur radii applied after the nearest-fill. Smoothing the gutter at growing scales lets even the wide
		///     central gutter — the mirrored atlas's left/right divide — fill without a visible meeting seam. Islands
		///     are re-pinned after every pass.
		/// </summary>
		private static readonly int[] BlurRadii = [4, 8, 16, 32, 64, 96];

		/// <summary>
		///     Returns an RGBA copy of the atlas with its background gutter dilated into skin. Cached by the source
		///     content (the input is the untinted, decal-free base atlas, so the same source hits the cache across
		///     characters).
		/// </summary>
		/// <param name="covered">
		///     The mesh's own UV coverage from <see cref="BodyAtlasUvCoverage.TryRasterize" />, true where a triangle
		///     samples the texel. When given it replaces the colour heuristic outright and the gutter is exactly the
		///     uncovered set; null falls back to the heuristic.
		/// </param>
		public static byte[] Dilate(byte[] rgba, int width, int height, bool[]? covered = null)
		{
			if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
				return rgba;

			if (covered != null && covered.Length != width * height)
				covered = null; // a mask for a different atlas is not a mask; fall back rather than mis-index

			// The mask is part of the cache identity: two characters sharing one atlas but not one mesh do not share
			// a result. Keyed on the mask's own signature so a same-shaped mask from a different mesh still misses.
			return Cache.GetOrAdd(Signature(rgba, width, height) ^ MaskSignature(covered), _ => Compute(rgba, width, height, covered));
		}

		/// <summary>
		///     A separable box blur on the RGB channels, leaving alpha untouched, with edges clamped via a shrinking
		///     window count.
		/// </summary>
		private static byte[] BoxBlur(byte[] rgba, int w, int h, int radius)
		{
			var tmp = (byte[])rgba.Clone();
			var outp = (byte[])rgba.Clone();

			for (var y = 0; y < h; y++)
			{
				var row = y * w;
				for (var c = 0; c < 3; c++)
				{
					int sum = 0, cnt = 0;
					for (var x = 0; x <= radius && x < w; x++)
					{
						sum += rgba[(row + x) * 4 + c];
						cnt++;
					}

					for (var x = 0; x < w; x++)
					{
						tmp[(row + x) * 4 + c] = (byte)(sum / cnt);
						var add = x + radius + 1;
						var rem = x - radius;
						if (add < w)
						{
							sum += rgba[(row + add) * 4 + c];
							cnt++;
						}

						if (rem >= 0)
						{
							sum -= rgba[(row + rem) * 4 + c];
							cnt--;
						}
					}
				}
			}

			for (var x = 0; x < w; x++)
			{
				for (var c = 0; c < 3; c++)
				{
					int sum = 0, cnt = 0;
					for (var y = 0; y <= radius && y < h; y++)
					{
						sum += tmp[(y * w + x) * 4 + c];
						cnt++;
					}

					for (var y = 0; y < h; y++)
					{
						outp[(y * w + x) * 4 + c] = (byte)(sum / cnt);
						var add = y + radius + 1;
						var rem = y - radius;
						if (add < h)
						{
							sum += tmp[(add * w + x) * 4 + c];
							cnt++;
						}

						if (rem >= 0)
						{
							sum -= tmp[(rem * w + x) * 4 + c];
							cnt--;
						}
					}
				}
			}

			return outp;
		}

		private static byte[] Compute(byte[] src, int w, int h, bool[]? covered)
		{
			var n = w * h;
			var gutter = covered != null
				? Uncovered(covered)
				: DetectGutter(src, w, h);
			var outp = (byte[])src.Clone();

			// Fill every gutter pixel with the nearest island colour. The grassfire leaves Voronoi-like streaks that
			// the blur below clears.
			NearestFill(outp, gutter, w, h);

			// Smooth the gutter only, re-pinning the original island pixels after each (growing) blur pass.
			foreach (var radius in BlurRadii)
			{
				var blurred = BoxBlur(outp, w, h, radius);
				for (var i = 0; i < n; i++)
				{
					var o = i * 4;
					if (gutter[i])
					{
						outp[o] = blurred[o];
						outp[o + 1] = blurred[o + 1];
						outp[o + 2] = blurred[o + 2];
					}
					else
					{
						outp[o] = src[o];
						outp[o + 1] = src[o + 1];
						outp[o + 2] = src[o + 2];
					}
				}
			}

			return outp;
		}

		private static void Copy(byte[] rgba, int dst, int src)
		{
			var d = dst * 4;
			var s = src * 4;
			rgba[d] = rgba[s];
			rgba[d + 1] = rgba[s + 1];
			rgba[d + 2] = rgba[s + 2];
		}

		/// <summary>
		///     The heuristic gutter: the flat background reachable from the image border, found by flood fill.
		/// </summary>
		/// <remarks>
		///     Two guards keep the flood from leaking into skin, which matters because the tan background is close to
		///     some skin tones: a pixel must be near the sampled background colour, and it must be locally flat (tiny
		///     neighbour variation). Real skin, even tan skin, always carries gradient and pore detail, so the flood
		///     stops at the skin edge instead of eating into it. A global colour test, or a looser flood, smears skin
		///     by classifying skin pixels as gutter and blurring them.
		/// </remarks>
		internal static bool[] DetectGutter(byte[] rgba, int w, int h)
		{
			var gutter = new bool[w * h];
			var ci = (2 * w + 2) * 4;
			int br = rgba[ci], bg = rgba[ci + 1], bb = rgba[ci + 2];

			var q = new Queue<int>();

			for (var x = 0; x < w; x++)
			{
				Seed(x);
				Seed((h - 1) * w + x);
			}

			for (var y = 0; y < h; y++)
			{
				Seed(y * w);
				Seed(y * w + w - 1);
			}

			while (q.Count > 0)
			{
				var i = q.Dequeue();
				var x = i % w;
				var y = i / w;
				if (x > 0)
					Seed(i - 1);
				if (x < w - 1)
					Seed(i + 1);
				if (y > 0)
					Seed(i - w);
				if (y < h - 1)
					Seed(i + w);
			}

			return gutter;

			bool IsGutter(int i)
			{
				var o = i * 4;
				if (Math.Abs(rgba[o] - br) >= BackgroundTolerance || Math.Abs(rgba[o + 1] - bg) >= BackgroundTolerance || Math.Abs(rgba[o + 2] - bb) >= BackgroundTolerance)
					return false;

				return IsFlat(rgba, i, w, h);
			}

			void Seed(int i)
			{
				if (!gutter[i] && IsGutter(i))
				{
					gutter[i] = true;
					q.Enqueue(i);
				}
			}
		}

		/// <summary>
		///     Whether a pixel is flat: its 4-neighbourhood barely varies, with the maximum channel spread below a
		///     small threshold. That holds for the constant background gutter and not for skin, which always carries
		///     gradient and pore detail.
		/// </summary>
		private static bool IsFlat(byte[] rgba, int i, int w, int h)
		{
			const int flatTolerance = 6;
			var x = i % w;
			var y = i / w;
			var o = i * 4;
			for (var c = 0; c < 3; c++)
			{
				int v = rgba[o + c], lo = v, hi = v;
				if (x > 0)
				{
					var nv = rgba[(i - 1) * 4 + c];
					lo = Math.Min(lo, nv);
					hi = Math.Max(hi, nv);
				}

				if (x < w - 1)
				{
					var nv = rgba[(i + 1) * 4 + c];
					lo = Math.Min(lo, nv);
					hi = Math.Max(hi, nv);
				}

				if (y > 0)
				{
					var nv = rgba[(i - w) * 4 + c];
					lo = Math.Min(lo, nv);
					hi = Math.Max(hi, nv);
				}

				if (y < h - 1)
				{
					var nv = rgba[(i + w) * 4 + c];
					lo = Math.Min(lo, nv);
					hi = Math.Max(hi, nv);
				}

				if (hi - lo > flatTolerance)
					return false;
			}

			return true;
		}

		private static int IslandNeighbour(bool[] gutter, int i, int w, int h)
		{
			var x = i % w;
			var y = i / w;
			if (x > 0 && !gutter[i - 1])
				return i - 1;
			if (x < w - 1 && !gutter[i + 1])
				return i + 1;
			if (y > 0 && !gutter[i - w])
				return i - w;
			if (y < h - 1 && !gutter[i + w])
				return i + w;

			return -1;
		}

		/// <summary>
		///     A multi-source breadth-first grassfire from the island edges: each gutter pixel takes the colour of the
		///     first filled pixel to reach it, so gutter regions fill with their nearest island's colour in time linear
		///     in the pixel count.
		/// </summary>
		private static void NearestFill(byte[] rgba, bool[] gutter, int w, int h)
		{
			var pending = (bool[])gutter.Clone(); // true = still-empty gutter pixel
			var q = new Queue<int>();

			// Seed the queue: gutter pixels touching an island copy that island neighbour's colour.
			for (var i = 0; i < pending.Length; i++)
			{
				if (!gutter[i])
					continue;

				var src = IslandNeighbour(gutter, i, w, h);
				if (src < 0)
					continue;

				Copy(rgba, i, src);
				pending[i] = false;
				q.Enqueue(i);
			}

			while (q.Count > 0)
			{
				var i = q.Dequeue();
				var x = i % w;
				var y = i / w;
				if (x > 0 && pending[i - 1])
				{
					Copy(rgba, i - 1, i);
					pending[i - 1] = false;
					q.Enqueue(i - 1);
				}

				if (x < w - 1 && pending[i + 1])
				{
					Copy(rgba, i + 1, i);
					pending[i + 1] = false;
					q.Enqueue(i + 1);
				}

				if (y > 0 && pending[i - w])
				{
					Copy(rgba, i - w, i);
					pending[i - w] = false;
					q.Enqueue(i - w);
				}

				if (y < h - 1 && pending[i + w])
				{
					Copy(rgba, i + w, i);
					pending[i + w] = false;
					q.Enqueue(i + w);
				}
			}
		}

		/// <summary>
		///     The gutter as the plain complement of the mesh's UV coverage: no colour test, no flatness test and no
		///     flood fill from the border.
		/// </summary>
		private static bool[] Uncovered(bool[] covered)
		{
			var gutter = new bool[covered.Length];
			for (var i = 0; i < covered.Length; i++)
				gutter[i] = !covered[i];

			return gutter;
		}

		/// <summary>
		///     A sparse signature of the coverage mask, folded into the cache key; 0 when there is no mask, so a
		///     heuristic result keys on the atlas alone.
		/// </summary>
		private static long MaskSignature(bool[]? covered)
		{
			if (covered == null)
				return 0;

			var sig = unchecked((long)0x9E3779B97F4A7C15UL) ^ covered.Length;
			for (var i = 0; i < covered.Length; i += 1021)
			{
				sig = (sig * 1099511628211L) ^ (covered[i]
					? 1u
					: 0u);
			}

			return sig;
		}

		/// <summary>
		///     A cheap content signature — dimensions plus sparsely sampled bytes — keying the per-atlas cache.
		/// </summary>
		private static long Signature(byte[] rgba, int width, int height)
		{
			var sig = ((long)width << 20) ^ ((long)height << 4) ^ rgba.Length;
			for (var i = 0; i < rgba.Length; i += 4099)
				sig = (sig * 1099511628211L) ^ rgba[i];

			return sig;
		}
	}
}
