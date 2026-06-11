using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Levelling curve (docs/server-architecture-roadmap.md): experience is within-level progress; Apply spends it
	// against the curve and reports the levels gained + leftover.
	public class LevelingTests
	{
		[Fact]
		public void XpForNextLevel_RampsWithLevel()
		{
			Assert.Equal(100u, LevelingResolver.XpForNextLevel(1));
			Assert.Equal(200u, LevelingResolver.XpForNextLevel(2));
			Assert.Equal(LevelingResolver.XpForNextLevel(1), LevelingResolver.XpForNextLevel(0)); // 0 treated as 1
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
		public void Apply_MultiLevelInOneAward()
		{
			// from level 1: 100 (->2) + 200 (->3) = 300 consumed; 350 -> +2 levels, 50 left.
			var (gained, remaining) = LevelingResolver.Apply(1, 350);
			Assert.Equal(2u, gained);
			Assert.Equal(50u, remaining);
		}

		[Fact]
		public void Apply_CapsAtMaxLevel_NoOverflow()
		{
			var (gained, remaining) = LevelingResolver.Apply(LevelingResolver.MaxLevel, 999999);
			Assert.Equal(0u, gained);
			Assert.Equal(0u, remaining); // bar sits empty at cap
		}

		[Fact]
		public void Apply_StopsExactlyAtMaxLevel()
		{
			var (gained, _) = LevelingResolver.Apply(LevelingResolver.MaxLevel - 1, 999999);
			Assert.Equal(1u, gained); // only the one level up to the cap
		}
	}
}
