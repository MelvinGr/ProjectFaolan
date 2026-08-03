using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The INTERPOLATED GameAction family's arithmetic (#597/#599). The shape is decoded from the client's own apply
	// (`FUN_008b2320` + `FUN_008c39f0`, docs/re/gameactionlist-grammar.md); this pins the four things about it that a
	// re-derivation from "it's a lerp" gets wrong - the clamp at BOTH ends, the float, the truncation, and the
	// degenerate ramp taking the HIGH value rather than the low.
	public class LevelRampTests
	{
		// `Battle Roar` as the client ships it: +887 health at level 20, +17500 at 80. Applying the low endpoint flat
		// - which is what shipped until the ramp columns existed - under-grants a level-80 character by 16613.
		[Theory]
		[InlineData(1u, 887)]
		[InlineData(19u, 887)]
		[InlineData(20u, 887)]
		[InlineData(21u, 1163)]
		[InlineData(50u, 9193)]
		[InlineData(79u, 17223)]
		[InlineData(80u, 17500)]
		[InlineData(255u, 17500)]
		public void BattleRoar_RampsFromItsLowLevelToItsHigh(uint level, int expected)
		{
			Assert.Equal(expected, new LevelRamp(887, 17500, 20, 80).ValueAt(level));
		}

		// The ramp is CLAMPED at both ends, not extrapolated: a level-1 character does not get a negative bonus and a
		// level-255 one does not get an unbounded one. The client clamps the LEVEL before taking the fraction
		// (`CMOVL`/`CMOVG`), which is why both ends come out exactly on an endpoint rather than near it.
		[Fact]
		public void OutsideTheRamp_IsClampedToAnEndpointExactly()
		{
			var ramp = new LevelRamp(-169, 400, 40, 60);

			Assert.Equal(-169, ramp.ValueAt(0));
			Assert.Equal(-169, ramp.ValueAt(39));
			Assert.Equal(400, ramp.ValueAt(61));
			Assert.Equal(400, ramp.ValueAt(uint.MaxValue));
		}

		// A DESCENDING ramp is not a special case - the lerp handles it, and the shipped spell corpus does carry a
		// negative endpoint (-169), so nothing here may assume low <= high.
		[Fact]
		public void ADescendingRamp_Interpolates_Downwards()
		{
			var ramp = new LevelRamp(1000, 0, 10, 20);

			Assert.Equal(1000, ramp.ValueAt(10));
			Assert.Equal(500, ramp.ValueAt(15));
			Assert.Equal(0, ramp.ValueAt(20));
		}

		// The client's own degenerate branch: when the two levels are equal the fraction is 1.0, so the ramp is worth
		// its HIGH value at every level. Reading it as "the low value" would be the natural guess and is wrong.
		[Fact]
		public void ADegenerateRamp_TakesTheHighValueAtEveryLevel()
		{
			var ramp = new LevelRamp(100, 900, 50, 50);

			Assert.Equal(900, ramp.ValueAt(1));
			Assert.Equal(900, ramp.ValueAt(50));
			Assert.Equal(900, ramp.ValueAt(80));
		}

		// The result TRUNCATES toward zero out of a float, which is the client's `(int)` on the lerp. A rounded or
		// double-precision read disagrees by one across most of a ramp's span, and the disagreement is visible: it is
		// a health pool the client renders.
		[Fact]
		public void TheResultTruncatesRatherThanRounding()
		{
			// 0 -> 10 over levels 0 -> 3: the thirds land at 3.33 and 6.66 and must read 3 and 6.
			var ramp = new LevelRamp(0, 10, 0, 3);

			Assert.Equal(3, ramp.ValueAt(1));
			Assert.Equal(6, ramp.ValueAt(2));
		}
	}
}
