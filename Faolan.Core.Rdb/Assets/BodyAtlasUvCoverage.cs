using System;
using System.Collections.Generic;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Which texels of a body atlas the mesh actually samples, found by rasterizing the mesh's own UV triangles.
	/// </summary>
	/// <remarks>
	///     This is the authoritative answer <see cref="BodyAtlasDilator" /> needs: the dilator's colour and flatness
	///     test only guesses at "is this pixel background", and the tan gutter is close enough to tan skin that a
	///     smooth patch of real chest passes both guards and gets flattened. A UV triangle covering a texel is not a
	///     guess — that texel is drawn on the character, so it is skin by definition — and a texel no triangle covers
	///     is never drawn, so nothing is lost by filling it.
	///     <para>
	///         Coverage is deliberately conservative: a texel counts as covered if the triangle touches it at all
	///         (the centre-inside fill plus all three edges walked), not only if its centre is inside. The two errors
	///         are not symmetric — a missed island texel gets blurred away and shows as an artifact, while an extra
	///         covered texel merely goes unfilled in a region the mesh does not sample.
	///     </para>
	/// </remarks>
	public static class BodyAtlasUvCoverage
	{
		/// <summary>
		///     Rasterize every sub-mesh's UV triangles into a <paramref name="width" />×<paramref name="height" />
		///     coverage mask. Null when no sub-mesh carries UVs (a mesh whose atlas we cannot attribute), which is the
		///     caller's signal to fall back to the colour heuristic rather than treat the whole atlas as gutter.
		/// </summary>
		/// <param name="flipV">
		///     Sample V as <c>1 - v</c>. Exposed for the orientation check in `--report body-atlas-coverage`; the
		///     shipped path never sets it, because the measurement says the atlas and the UVs share an origin.
		/// </param>
		public static bool[]? TryRasterize(IReadOnlyList<RenderMeshReader.SubMesh> subs, int width, int height, bool flipV = false)
		{
			if (width <= 0 || height <= 0)
				return null;

			var covered = new bool[width * height];
			var any = false;

			foreach (var sub in subs)
			{
				if (sub.Uvs == null || sub.Indices.Length < 3)
					continue;

				any = true;
				for (var t = 0; t + 2 < sub.Indices.Length; t += 3)
				{
					var (au, av) = Uv(sub, sub.Indices[t]);
					var (bu, bv) = Uv(sub, sub.Indices[t + 1]);
					var (cu, cv) = Uv(sub, sub.Indices[t + 2]);

					// UVs outside [0,1] are a legal tiling address, and the tile is a property of the triangle, not of
					// each corner: wrapping the corners independently would tear a triangle that straddles a tile
					// boundary into one spanning the whole atlas. Shift all three by the same whole-tile offset
					// instead, so the shape survives and anything still off the edge is simply clipped by Mark.
					var du = MathF.Floor(MathF.Min(au, MathF.Min(bu, cu)));
					var dv = MathF.Floor(MathF.Min(av, MathF.Min(bv, cv)));
					Triangle(covered, width, height,
						(au - du) * width, (av - dv) * height,
						(bu - du) * width, (bv - dv) * height,
						(cu - du) * width, (cv - dv) * height);
				}
			}

			return any
				? covered
				: null;

			(float U, float V) Uv(RenderMeshReader.SubMesh sub, int vertex)
			{
				var v = sub.Uvs![vertex * 2 + 1];
				return (sub.Uvs[vertex * 2], flipV
					? 1f - v
					: v);
			}
		}

		/// <summary>
		///     Walks a UV edge with a DDA, marking every texel it crosses. This is what catches the sliver triangles a
		///     centre-inside test drops entirely, and an island's outline is exactly where the seam artifacts are.
		/// </summary>
		private static void Edge(bool[] covered, int w, int h, float x0, float y0, float x1, float y1)
		{
			var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0))) + 1;
			for (var i = 0; i <= steps; i++)
			{
				var s = (float)i / steps;
				Mark(covered, w, h, (int)MathF.Floor(x0 + (x1 - x0) * s), (int)MathF.Floor(y0 + (y1 - y0) * s));
			}
		}

		private static void Mark(bool[] covered, int w, int h, int x, int y)
		{
			if (x >= 0 && x < w && y >= 0 && y < h)
				covered[y * w + x] = true;
		}

		private static void Triangle(bool[] covered, int w, int h, float ax, float ay, float bx, float by, float cx, float cy)
		{
			var minX = Math.Clamp((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), 0, w - 1);
			var maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))), 0, w - 1);
			var minY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), 0, h - 1);
			var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), 0, h - 1);

			var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
			if (MathF.Abs(area) > 1e-6f)
			{
				var inv = 1f / area;
				for (var y = minY; y <= maxY; y++)
				{
					var py = y + 0.5f;
					for (var x = minX; x <= maxX; x++)
					{
						var px = x + 0.5f;
						var u = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
						var v = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
						if (u >= 0 && v >= 0 && u + v <= 1)
							covered[y * w + x] = true;
					}
				}
			}

			Edge(covered, w, h, ax, ay, bx, by);
			Edge(covered, w, h, bx, by, cx, cy);
			Edge(covered, w, h, cx, cy, ax, ay);
		}

	}
}
