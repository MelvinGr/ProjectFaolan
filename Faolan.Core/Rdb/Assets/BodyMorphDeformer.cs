using System;

namespace Faolan.Core.Rdb.Assets
{
	// Applies a character's body-proportion morphs (CharacterBodies Arm/Chest/Bosom/Stomach/Arse/Thigh/Leg) to the
	// rendered body mesh for the 3D viewer (#275).
	//
	// AoC ships the authentic morph data as a CLUS section in the body rendermesh (30 named deformation clusters that
	// index the DPPM morph-submesh), but the cluster name->geometry association is unreliable to recover (the leg-named
	// clusters sit at the hands; the lower legs have no cluster at all) and the CHARACTERCREATOR BodyNode that drives
	// magnitude is not decoded. So for the viewer we approximate the morphs GEOMETRICALLY: the body is a fixed A-pose,
	// so each slider maps to an anatomical region defined by normalised height (+ radial / front-back position) and we
	// scale that region's girth (or, for the bosom, push it forward). This is robust to whichever body mesh renders
	// (the exact race/sex body or the local fallback) and gives a clearly slim-vs-full silhouette matching the sliders.
	public static class BodyMorphDeformer
	{
		// The 7 body-proportion sliders as stored in CharacterBodies (raw editor units, range about [-100,100]).
		public readonly record struct Morphs(float Arm, float Chest, float Bosom, float Stomach, float Arse, float Thigh, float Leg)
		{
			public bool IsNeutral => Arm == 0 && Chest == 0 && Bosom == 0 && Stomach == 0 && Arse == 0 && Thigh == 0 && Leg == 0;
		}

		// Deform the body submesh positions in place. positions = the rendered base mesh's xyz array. No-op if neutral.
		public static void Apply(float[] positions, Morphs morphs)
		{
			if (morphs.IsNeutral)
				return;

			var n = positions.Length / 3;
			if (n == 0)
				return;

			// Vertical extent -> normalised height ny in [0,1] (feet 0, head 1), so the height bands generalise across
			// body meshes of different scale.
			float ymin = float.MaxValue, ymax = float.MinValue;
			for (var i = 0; i < n; i++)
			{
				var y = positions[i * 3 + 1];
				if (y < ymin) ymin = y;
				if (y > ymax) ymax = y;
			}
			var height = ymax - ymin;
			if (height <= 1e-4f)
				return;

			var orig = (float[])positions.Clone();
			var dispX = new float[n];
			var dispY = new float[n];
			var dispZ = new float[n];

			// Girth morphs: thicken/thin a region's cross-section about its own centre (radial in X/Z, height
			// preserved). Limb regions split left/right so each limb scales about its OWN centreline.
			GirthRegion(orig, dispX, dispZ, ymin, height, morphs.Leg, 0.15f, 0.08f, 0.30f, limbX: 0.25f, perSide: true);    // calfs/shins
			GirthRegion(orig, dispX, dispZ, ymin, height, morphs.Thigh, 0.17f, 0.27f, 0.52f, limbX: 0.25f, perSide: true);  // thighs
			GirthRegion(orig, dispX, dispZ, ymin, height, morphs.Arse, 0.16f, 0.47f, 0.63f, limbX: 0.25f, perSide: false);  // hips/seat
			GirthRegion(orig, dispX, dispZ, ymin, height, morphs.Stomach, 0.15f, 0.56f, 0.77f, limbX: 0.25f, perSide: false); // waist/belly
			GirthRegion(orig, dispX, dispZ, ymin, height, morphs.Chest, 0.15f, 0.66f, 0.85f, limbX: 0.25f, perSide: false);   // ribcage
			// Arms: select by |x| (they splay out past the torso) and scale girth about each arm's centreline.
			ArmRegion(orig, dispX, dispY, dispZ, morphs.Arm, 0.15f, armX: 0.26f);
			// Bosom: a forward (z) protrusion of the upper-chest front, not a girth scale.
			BosomRegion(orig, dispZ, ymin, height, morphs.Bosom, 0.28f, 0.74f, 0.86f);

			for (var v = 0; v < n; v++)
			{
				positions[v * 3] = orig[v * 3] + dispX[v];
				positions[v * 3 + 1] = orig[v * 3 + 1] + dispY[v];
				positions[v * 3 + 2] = orig[v * 3 + 2] + dispZ[v];
			}
		}

		// Scale a height-banded region's X/Z girth about its centre. raw is the slider [-100,100]; amp is the girth
		// fraction at the slider extreme. limbX excludes the splayed arms; perSide scales each side about its own centre
		// (legs) so thickening doesn't just widen the stance. A smooth window over [loN,hiN] feathers the band edges.
		private static void GirthRegion(float[] p, float[] dispX, float[] dispZ, float ymin, float height,
			float raw, float amp, float loN, float hiN, float limbX, bool perSide)
		{
			if (raw == 0)
				return;
			var k = raw / 100f * amp;
			var n = p.Length / 3;

			// Per-side centres (X/Z) over the windowed core verts.
			Span<float> sumX = stackalloc float[2];
			Span<float> sumZ = stackalloc float[2];
			Span<float> sumW = stackalloc float[2];
			for (var v = 0; v < n; v++)
			{
				var x = p[v * 3];
				if (MathF.Abs(x) >= limbX)
					continue;
				var w = Window((p[v * 3 + 1] - ymin) / height, loN, hiN);
				if (w <= 0)
					continue;
				var s = perSide && x < 0 ? 1 : 0;
				sumX[s] += x * w; sumZ[s] += p[v * 3 + 2] * w; sumW[s] += w;
			}
			if (sumW[0] <= 0 && sumW[1] <= 0)
				return;
			Span<float> cx = stackalloc float[2];
			Span<float> cz = stackalloc float[2];
			for (var s = 0; s < 2; s++)
				if (sumW[s] > 0) { cx[s] = sumX[s] / sumW[s]; cz[s] = sumZ[s] / sumW[s]; }

			for (var v = 0; v < n; v++)
			{
				var x = p[v * 3];
				if (MathF.Abs(x) >= limbX)
					continue;
				var w = Window((p[v * 3 + 1] - ymin) / height, loN, hiN);
				if (w <= 0)
					continue;
				var s = perSide && x < 0 ? 1 : 0;
				if (sumW[s] <= 0)
					continue;
				dispX[v] += (x - cx[s]) * k * w;
				dispZ[v] += (p[v * 3 + 2] - cz[s]) * k * w;
			}
		}

		// Scale each arm's girth about its medial (long) axis. Arms are the verts past the torso width (|x| >= armX);
		// we split left/right and scale the per-vertex offset PERPENDICULAR to the arm's principal axis, so the arm
		// thins/thickens radially and never collapses along its length.
		private static void ArmRegion(float[] p, float[] dispX, float[] dispY, float[] dispZ, float raw, float amp, float armX)
		{
			if (raw == 0)
				return;
			var k = raw / 100f * amp;
			var n = p.Length / 3;

			for (var side = 0; side < 2; side++)
			{
				float cx = 0, cy = 0, cz = 0;
				var count = 0;
				for (var v = 0; v < n; v++)
				{
					var x = p[v * 3];
					if (MathF.Abs(x) < armX || (x < 0) != (side == 1))
						continue;
					cx += x; cy += p[v * 3 + 1]; cz += p[v * 3 + 2]; count++;
				}
				if (count < 3)
					continue;
				cx /= count; cy /= count; cz /= count;

				// Principal axis via power iteration on the covariance of the arm verts.
				double c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
				for (var v = 0; v < n; v++)
				{
					var x = p[v * 3];
					if (MathF.Abs(x) < armX || (x < 0) != (side == 1))
						continue;
					double dx = x - cx, dy = p[v * 3 + 1] - cy, dz = p[v * 3 + 2] - cz;
					c00 += dx * dx; c01 += dx * dy; c02 += dx * dz; c11 += dy * dy; c12 += dy * dz; c22 += dz * dz;
				}
				double ax = 1, ay = 0, az = 0;
				for (var iter = 0; iter < 24; iter++)
				{
					var nx = c00 * ax + c01 * ay + c02 * az;
					var ny2 = c01 * ax + c11 * ay + c12 * az;
					var nz = c02 * ax + c12 * ay + c22 * az;
					var len = Math.Sqrt(nx * nx + ny2 * ny2 + nz * nz);
					if (len < 1e-12) break;
					ax = nx / len; ay = ny2 / len; az = nz / len;
				}

				for (var v = 0; v < n; v++)
				{
					var x = p[v * 3];
					if (MathF.Abs(x) < armX || (x < 0) != (side == 1))
						continue;
					double dx = x - cx, dy = p[v * 3 + 1] - cy, dz = p[v * 3 + 2] - cz;
					var along = dx * ax + dy * ay + dz * az;
					dispX[v] += (float)(dx - along * ax) * k;
					dispY[v] += (float)(dy - along * ay) * k;
					dispZ[v] += (float)(dz - along * az) * k;
				}
			}
		}

		// Push the upper-chest front forward (breast size). Only front verts (z above the band's centre) in the chest
		// band are displaced, proportional to how far forward they already sit, so the chest swells/flattens.
		private static void BosomRegion(float[] p, float[] dispZ, float ymin, float height, float raw, float amp, float loN, float hiN)
		{
			if (raw == 0)
				return;
			var k = raw / 100f * amp;
			var n = p.Length / 3;

			float sumZ = 0, sumW = 0;
			for (var v = 0; v < n; v++)
			{
				if (MathF.Abs(p[v * 3]) >= 0.25f)
					continue;
				var w = Window((p[v * 3 + 1] - ymin) / height, loN, hiN);
				if (w <= 0)
					continue;
				sumZ += p[v * 3 + 2] * w; sumW += w;
			}
			if (sumW <= 0)
				return;
			var cz = sumZ / sumW;

			for (var v = 0; v < n; v++)
			{
				if (MathF.Abs(p[v * 3]) >= 0.25f)
					continue;
				var z = p[v * 3 + 2];
				if (z <= cz)
					continue; // front only
				var w = Window((p[v * 3 + 1] - ymin) / height, loN, hiN);
				if (w <= 0)
					continue;
				dispZ[v] += (z - cz) * k * w;
			}
		}

		// Smooth trapezoidal window over [lo,hi] with a smoothstep feather at each edge, so banded regions blend into
		// the unmorphed body instead of leaving a hard ring.
		private static float Window(float t, float lo, float hi)
		{
			const float feather = 0.07f;
			if (t <= lo - feather || t >= hi + feather)
				return 0f;
			if (t < lo + feather)
				return SmoothStep((t - (lo - feather)) / (2 * feather));
			if (t > hi - feather)
				return SmoothStep(((hi + feather) - t) / (2 * feather));
			return 1f;
		}

		private static float SmoothStep(float x)
		{
			x = Math.Clamp(x, 0f, 1f);
			return x * x * (3f - 2f * x);
		}
	}
}
