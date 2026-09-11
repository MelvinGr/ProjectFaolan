using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// Levelling curve. Retail's three XP stats are cumulative - Exp is a running total, bounded below by
	// ExpCurrentLevelBase (the total at which the current level began) and above by MaxExp (the total at which the
	// next one does) - so a level's cost is the difference between two thresholds, and the anchor table stores the
	// thresholds. The character row keeps within-level progress; Apply spends it against the derived per-level costs.
	//
	// The corpus proof that the anchors are thresholds and not costs is LevelingCurveCorpusTests.
	public class LevelingTests
	{
		[Fact]
		public void Apply_CapsAtMaxLevel_NoOverflow()
		{
			var (gained, remaining) = LevelingResolver.Apply(LevelingResolver.MaxLevel, 999999);
			Assert.Equal(0u, gained);
			Assert.Equal(0u, remaining); // bar sits empty at cap
		}

		[Fact]
		public void Apply_MultiLevelInOneAward()
		{
			// real curve: level 1->2 costs 100, 2->3 costs 240 (thresholds 100 and 340). 500 -> +2 levels
			// (100+240 = 340 consumed), 160 left.
			var (gained, remaining) = LevelingResolver.Apply(1, 500);
			Assert.Equal(2u, gained);
			Assert.Equal(160u, remaining);
		}

		[Fact]
		public void Apply_NoLevelWhenUnderThreshold()
		{
			var (gained, remaining) = LevelingResolver.Apply(1, 40);
			Assert.Equal(0u, gained);
			Assert.Equal(40u, remaining); // still 40/100 toward level 2
		}

		[Fact]
		public void Apply_SingleLevelCarriesRemainder()
		{
			// level 1 needs 100; 130 -> ding to 2 with 30 left over.
			var (gained, remaining) = LevelingResolver.Apply(1, 130);
			Assert.Equal(1u, gained);
			Assert.Equal(30u, remaining);
		}

		[Fact]
		public void Apply_StopsExactlyAtMaxLevel()
		{
			// L79->L80 is ~627k on the real curve, so award well past it; still only the one level to the cap.
			var (gained, _) = LevelingResolver.Apply(LevelingResolver.MaxLevel - 1, 999_999_999);
			Assert.Equal(1u, gained);
		}

		// The captured retail thresholds: the cumulative experience at which each level begins. These are read
		// straight off the wire (ExpCurrentLevelBase / MaxExp / the NewLevelIIR deltas) and must be exact.
		[Theory]
		[InlineData(1u, 0u), InlineData(2u, 100u), InlineData(3u, 340u), InlineData(4u, 740u), InlineData(5u, 1440u)]
		[InlineData(6u, 3940u), InlineData(8u, 10240u), InlineData(9u, 14140u)]
		[InlineData(23u, 230240u), InlineData(24u, 263640u), InlineData(25u, 298040u)]
		[InlineData(80u, 34771240u), InlineData(81u, 47907140u)]
		public void CumulativeXpForLevel_MatchesTheCapturedRetailThresholds(uint level, uint expected)
		{
			Assert.Equal(expected, LevelingResolver.CumulativeXpForLevel(level));
		}

		// A per-level cost is exact wherever both of its ends are anchored. These are the differences retail's own
		// bar measures, and a mis-anchored table is wrong on them by a factor of up to ~9.
		[Theory]
		[InlineData(1u, 100u), InlineData(2u, 240u), InlineData(3u, 400u), InlineData(4u, 700u), InlineData(5u, 2500u)]
		[InlineData(8u, 3900u), InlineData(23u, 33400u), InlineData(24u, 34400u), InlineData(80u, 13135900u)]
		public void XpForNextLevel_IsTheDifferenceBetweenTwoAnchoredThresholds(uint level, uint expected)
		{
			Assert.Equal(expected, LevelingResolver.XpForNextLevel(level));
		}

		[Fact]
		public void XpForNextLevel_InterpolatesMonotonicallyBetweenAnchors()
		{
			Assert.Equal(LevelingResolver.XpForNextLevel(1), LevelingResolver.XpForNextLevel(0)); // 0 treated as 1
			// L7 is the one unanchored level below 10; its cost sits between its anchored neighbours.
			Assert.InRange(LevelingResolver.XpForNextLevel(7), LevelingResolver.XpForNextLevel(5), LevelingResolver.XpForNextLevel(8));

			// The cumulative curve is strictly increasing, so the derived per-level costs never go backwards.
			for (uint l = 1; l < 80; l++)
			{
				Assert.True(LevelingResolver.CumulativeXpForLevel(l) < LevelingResolver.CumulativeXpForLevel(l + 1));
				// ...up to 1 XP of integer truncation: a gap's cumulative interpolation is truncated per level, so two
				// consecutive costs inside one gap can read n and n-1 for the same real slope.
				Assert.True(LevelingResolver.XpForNextLevel(l) <= LevelingResolver.XpForNextLevel(l + 1) + 1, $"cost fell at level {l}");
			}
		}
	}
}
