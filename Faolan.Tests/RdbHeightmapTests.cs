using System;
using System.Collections.Generic;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The RDB terrain heightmap (type 1066611) parser + world->grid sampling (#284). Format decoded + validated
	// byte-exact against the live client RDB: `[u32 levels=1][u32 W][u32 H]` + a W×H row-major float32 grid (row = Z),
	// sampled as grid[round(z/cell)*W + round(x/cell)]. Real-data check (not hermetic, done manually): City of Tortage
	// (4020) at known player ground positions matches the players' Y to <0.5u. These tests lock the parse + transform.
	public class RdbHeightmapTests
	{
		[Fact]
		public void CellSize_From_TilemapExtent_Scales_The_Transform()
		{
			// A downsampled map: 4 samples across an 8-unit playfield -> 2 units/cell.
			var body = Build(4, 1, (gx, _) => gx * 100f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm, 8f));
			Assert.Equal(2f, hm.CellSize);
			Assert.Equal(0f, hm.HeightAt(0, 0)); // world 0 -> cell 0
			Assert.Equal(100f, hm.HeightAt(2, 0)); // world 2 -> cell 1
			Assert.Equal(300f, hm.HeightAt(6, 0)); // world 6 -> cell 3
		}

		[Fact]
		public void OffGrid_Returns_Null()
		{
			var body = Build(4, 3, (_, _) => 5f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));
			Assert.Null(hm.HeightAt(-1, 0));
			Assert.Null(hm.HeightAt(0, 3)); // gz3 is out of a height-3 grid
			Assert.Null(hm.HeightAt(4, 0)); // gx4 is out of a width-4 grid
		}

		[Fact]
		public void Parses_And_Samples_RowMajor_By_Z()
		{
			// grid[gz*W + gx] = gz*10 + gx, so the row index is Z and the column is X.
			var body = Build(4, 3, (gx, gz) => gz * 10 + gx);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));
			Assert.Equal(4, hm.Width);
			Assert.Equal(3, hm.Height);
			Assert.Equal(1f, hm.CellSize); // no tilemap extent passed -> 1 unit/cell

			Assert.Equal(0f, hm.HeightAt(0, 0)); // gx0,gz0
			Assert.Equal(3f, hm.HeightAt(3, 0)); // gx3,gz0
			Assert.Equal(12f, hm.HeightAt(2, 1)); // gz1*10 + gx2
			Assert.Equal(22f, hm.HeightAt(2.4f, 1.5f)); // rounds to gx2,gz2 -> 22 (nearest-cell)
		}

		[Fact]
		public void Rejects_Truncated_Or_Bogus()
		{
			Assert.False(RdbHeightmap.TryParse(new byte[8], out _)); // < 12-byte header
			Assert.False(RdbHeightmap.TryParse(Build(4, 4, (_, _) => 0f)[..20], out _)); // header ok, grid truncated
			var bogus = new List<byte>();
			bogus.AddRange(BitConverter.GetBytes(1u));
			bogus.AddRange(BitConverter.GetBytes(0u)); // W = 0
			bogus.AddRange(BitConverter.GetBytes(4u));
			Assert.False(RdbHeightmap.TryParse(bogus.ToArray(), out _));
		}

		// The measured Tortage-beach case (#830-adjacent, the invisible loot bag). Scavenger Tuut died at Y=145.19 on
		// the edge of a sea stack's footprint: his NEAREST heightmap cell is the rock's top at 199.63, while the beach
		// cells a metre away read 146.1. The nearest-cell sample therefore put the ground 54 m away, the loot drop's 5 m
		// snap tolerance refused it, and the bag kept the death Y - 0.6 m under the sand, where it can be neither seen
		// nor clicked.
		[Fact]
		public void HeightNear_TakesTheSurfaceTheEntityStoodOn_NotTheRockItStoodBeside()
		{
			// A 5x5 beach at 146.1 with a rock occupying the centre cell.
			var body = Build(5, 5, (gx, gz) => gx == 2 && gz == 2
				? 199.63f
				: 146.1f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));

			Assert.Equal(199.63f, hm.HeightAt(2, 2)); // what the drop used to sample
			Assert.Equal(146.1f, hm.HeightNear(2, 2, 145.19f, 2f, 5f)); // ...and the sand it was standing on
		}

		[Fact]
		public void HeightNear_OnOpenGround_IsTheNearestCell()
		{
			// No competing surface: every candidate is the same, so this must not move a normal drop at all.
			var body = Build(5, 5, (gx, gz) => 145f + gx * 0.1f + gz * 0.1f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));

			Assert.Equal(hm.HeightAt(2, 2), hm.HeightNear(2, 2, 145.2f, 2f, 5f));
		}

		[Fact]
		public void HeightNear_KeepsAnIndoorKillOutOfReachOfTheSurface()
		{
			// Kier-Dheed's case: killed at Y=153 in a cave under a surface of ~181. NOTHING nearby is at his height, so
			// the closest candidate is still ~28 m away and the caller's tolerance still refuses to snap - which is what
			// stops a bag being flung into the open air above the cave.
			var body = Build(5, 5, (gx, gz) => 181f + gx * 0.2f + gz * 0.2f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));

			var ground = hm.HeightNear(2, 2, 153f, 2f, 5f);
			Assert.NotNull(ground);
			Assert.True(MathF.Abs(ground!.Value - 153f) > 5f, "an indoor kill must stay past the snap tolerance");
		}

		[Fact]
		public void HeightNear_OffGrid_IsStillNull()
		{
			var body = Build(4, 3, (_, _) => 5f);
			Assert.True(RdbHeightmap.TryParse(body, out var hm));
			Assert.Null(hm.HeightNear(-1, 0, 5f, 2f, 5f));
		}

		// #832: 1066611 is per-PLAYFIELD and bakes that playfield's own props into the surface, so playfields sharing a
		// tilemap (Tortage Island 4010 + City of Tortage 4020/4022 + End Battle 4030, all `tilemap ID="4040"`) each read
		// their one shared terrain differently. Props only ADD height, so the lower envelope is the ground under all of
		// them.
		[Fact]
		public void LowerEnvelope_TakesTheGroundUnderEveryPlayfieldsProps()
		{
			var terrain = Build(4, 4, (gx, gz) => 145f + gx + gz);
			var withTrees = Build(4, 4, (gx, gz) => gz == 1 && gx != 3
				? 199f // this playfield has a canopy along one row...
				: 145f + gx + gz);
			var withTower = Build(4, 4, (gx, gz) => gx == 3 && gz != 1
				? 260f // ...and this one a building along a column
				: 145f + gx + gz);

			Assert.True(RdbHeightmap.TryLowerEnvelope([withTrees, withTower], out var merged));
			Assert.True(RdbHeightmap.TryParse(merged, out var hm));
			Assert.True(RdbHeightmap.TryParse(terrain, out var bare));

			for (var gx = 0; gx < 4; gx++)
			for (var gz = 0; gz < 4; gz++)
				Assert.Equal(bare.HeightAt(gx, gz), hm.HeightAt(gx, gz));
		}

		// ⚠️ The honest limit of the envelope, pinned so it is a known property and not a surprise: a cell that carries
		// a prop in EVERY sibling keeps the lowest prop, because no grid in the group ever saw the ground there. On
		// Tortage that is the case for cells under permanent geometry, and it is why the loot drop's own sampling
		// (HeightNear) still has to tolerate an implausible cell rather than trusting the bake.
		[Fact]
		public void LowerEnvelope_CannotRecoverGroundNoSiblingEverSaw()
		{
			var a = Build(2, 2, (gx, _) => gx == 0
				? 199f
				: 145f);
			var b = Build(2, 2, (gx, _) => gx == 0
				? 260f
				: 145f);

			Assert.True(RdbHeightmap.TryLowerEnvelope([a, b], out var merged));
			Assert.True(RdbHeightmap.TryParse(merged, out var hm));
			Assert.Equal(199f, hm.HeightAt(0, 0)); // the lower of the two props, not the ground under them
			Assert.Equal(145f, hm.HeightAt(1, 0));
		}

		// A sibling at another resolution is SKIPPED, not fatal - and this is the case that made the first cut of the
		// merge a no-op in practice: Tortage's tilemap group is 4010/4015/4020/4022/4030/4040 and 4022 alone is a 768²
		// grid, so vetoing the group on it left the island exactly as it was.
		[Fact]
		public void LowerEnvelope_SkipsASiblingSampledAtAnotherResolution_AndStillMergesTheRest()
		{
			var own = Build(4, 4, (_, _) => 199f);
			var otherSize = Build(8, 8, (_, _) => 1f);
			var sibling = Build(4, 4, (_, _) => 145f);

			Assert.True(RdbHeightmap.TryLowerEnvelope([own, otherSize, sibling], out var merged));
			Assert.True(RdbHeightmap.TryParse(merged, out var hm));
			Assert.Equal(145f, hm.HeightAt(2, 2)); // the same-size sibling still won
			Assert.Equal(4, hm.Width); // ...and the odd one out did not decide the shape
		}

		[Fact]
		public void LowerEnvelope_WithNothingToMergeInto_IsRefused()
		{
			// One usable grid is not an envelope. False means "keep the body you already have" - the caller must not
			// rewrite an identical file and report a merge that never happened.
			var only = Build(4, 4, (gx, gz) => 145f + gx + gz);
			Assert.False(RdbHeightmap.TryLowerEnvelope([only], out _));
			Assert.False(RdbHeightmap.TryLowerEnvelope([only, Build(8, 8, (_, _) => 1f)], out _));
		}

		internal static byte[] Build(int w, int h, Func<int, int, float> height, uint levels = 1)
		{
			var bytes = new List<byte>();
			bytes.AddRange(BitConverter.GetBytes(levels));
			bytes.AddRange(BitConverter.GetBytes((uint)w));
			bytes.AddRange(BitConverter.GetBytes((uint)h));
			for (var gz = 0; gz < h; gz++)
			for (var gx = 0; gx < w; gx++)
				bytes.AddRange(BitConverter.GetBytes(height(gx, gz)));
			return bytes.ToArray();
		}
	}
}
