using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Levelling curve (Gitea #404): experience is within-level progress; Apply spends it
	// against the curve and reports the levels gained + leftover.
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
			// real curve: level 1->2 = 100, 2->3 = 340. 500 -> +2 levels (100+340 = 440 consumed), 60 left.
			var (gained, remaining) = LevelingResolver.Apply(1, 500);
			Assert.Equal(2u, gained);
			Assert.Equal(60u, remaining);
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
			// L79->L80 is ~47M on the real curve, so award well past it; still only the one level to the cap.
			var (gained, _) = LevelingResolver.Apply(LevelingResolver.MaxLevel - 1, 999_999_999);
			Assert.Equal(1u, gained);
		}

		[Fact]
		public void XpForNextLevel_InterpolatesMonotonicallyBetweenAnchors()
		{
			Assert.Equal(LevelingResolver.XpForNextLevel(1), LevelingResolver.XpForNextLevel(0)); // 0 treated as 1
			// L3 sits between the L2 (340) and L4 (1440) anchors.
			var l3 = LevelingResolver.XpForNextLevel(3);
			Assert.InRange(l3, 340u, 1440u);
			// strictly increasing across the whole 1..80 range.
			for (uint l = 1; l < 80; l++)
				Assert.True(LevelingResolver.XpForNextLevel(l) <= LevelingResolver.XpForNextLevel(l + 1));
		}

		[Theory, InlineData(1u, 100u), InlineData(2u, 340u), InlineData(4u, 1440u), InlineData(5u, 3940u), InlineData(23u, 263640u), InlineData(24u, 298040u), InlineData(80u, 47907140u)]
		// The captured retail anchors (MaxExp stat paired with the character's Level) — these MUST be exact.
		public void XpForNextLevel_MatchesTheCapturedRetailAnchors(uint level, uint expected)
		{
			Assert.Equal(expected, LevelingResolver.XpForNextLevel(level));
		}
	}
}
