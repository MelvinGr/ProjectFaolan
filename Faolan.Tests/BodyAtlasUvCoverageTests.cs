using System.Linq;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The body atlas's gutter is the set of texels the mesh's UV triangles do not cover. These pin the two
	// properties the fix rests on - that the rasterizer marks what a triangle covers, and that the dilator treats the
	// mask as authoritative rather than blending it with its colour guess.
	//
	// They are synthetic on purpose. The real evidence is `--report body-atlas-coverage`, which needs the client RDB
	// (not in a plain clone) and is a picture, not an assertion; what belongs in the suite is the behaviour that
	// picture depends on.
	public class BodyAtlasUvCoverageTests
	{
		// A UV triangle covering the top-left quarter marks that quarter and nothing in the opposite corner.
		[Fact]
		public void ATriangle_CoversItsOwnTexelsAndNotTheRest()
		{
			var covered = BodyAtlasUvCoverage.TryRasterize([Quad(0f, 0f, 0.5f, 0.5f)], 64, 64);

			Assert.NotNull(covered);
			Assert.True(covered[10 * 64 + 10]); // inside
			Assert.False(covered[60 * 64 + 60]); // the far corner
			// Half the image, give or take the conservative edge walk.
			var count = covered.Count(c => c);
			Assert.InRange(count, 64 * 64 / 4 - 200, 64 * 64 / 4 + 400);
		}

		// The whole point of the mask: the dilator must not touch a covered texel, and must fill an uncovered one.
		// A colour test cannot tell these two apart here - both halves are the same flat tone - which is exactly the
		// tan-skin-on-tan-background case the heuristic gets wrong.
		[Fact]
		public void DilateWithAMask_PreservesCoveredTexelsAndFillsTheRest()
		{
			const int w = 32, h = 32;
			var rgba = new byte[w * h * 4];
			for (var i = 0; i < w * h; i++)
			{
				// Left half a distinct colour, right half the "background"; both perfectly flat.
				var left = i % w < w / 2;
				rgba[i * 4] = left
					? (byte)200
					: (byte)100;
				rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4] ;
				rgba[i * 4 + 3] = 255;
			}

			// The mask says the right half is drawn and the left is not - the opposite of what any colour rule would
			// pick, so a result that preserves the right half can only have come from the mask.
			var covered = new bool[w * h];
			for (var i = 0; i < w * h; i++)
				covered[i] = i % w >= w / 2;

			var outp = BodyAtlasDilator.Dilate(rgba, w, h, covered);

			for (var i = 0; i < w * h; i++)
			{
				if (covered[i])
					Assert.Equal(rgba[i * 4], outp[i * 4]);
			}

			// The uncovered half is filled from the covered edge, so it differs from its original tone.
			Assert.NotEqual(rgba[(5 * w + 1) * 4], outp[(5 * w + 1) * 4]);
		}

		// A mesh with no UVs cannot say anything about the atlas, and must say so rather than report "nothing is
		// covered" - which would flatten the whole texture.
		[Fact]
		public void AMeshWithNoUvs_YieldsNoMaskRatherThanAnEmptyOne()
		{
			var noUvs = new RenderMeshReader.SubMesh([0, 0, 0, 1, 0, 0, 0, 1, 0], null, null, [0, 1, 2]);

			Assert.Null(BodyAtlasUvCoverage.TryRasterize([noUvs], 32, 32));
		}

		// Two triangles tiling the UV rectangle (u0,v0)-(u1,v1).
		private static RenderMeshReader.SubMesh Quad(float u0, float v0, float u1, float v1)
		{
			float[] uvs = [u0, v0, u1, v0, u1, v1, u0, v1];
			return new RenderMeshReader.SubMesh(new float[4 * 3], null, uvs, [0, 1, 2, 0, 2, 3]);
		}
	}
}
