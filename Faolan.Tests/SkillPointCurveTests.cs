using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The skill-point pool (stat 0x0F3) is a pure function of level - identical for every character at a given
	// level regardless of class. That is the claim the ticket asked to establish before modelling the value as an
	// event, and it is what these gates hold in place.
	//
	// The table is deliberately sparse: five of eighty levels are measured and the curve has no formula to fill the
	// rest with (level 23 -> 24 jumps 240 in one level, while 5 -> 23 averages under 14). The pressure on a sparse
	// table is always to "complete" it by interpolating, so the second test states outright that an unmeasured level
	// must return null - a filled-in value would be invented retail data the client then displays as authoritative.
	public class SkillPointCurveTests
	{
		[Theory]
		[InlineData(1u, 0u)] // Strie (assassin), Stihetfv (conqueror), xxx (necromancer)
		[InlineData(5u, 154u)] // Hoxxling (herald of xotli)
		[InlineData(23u, 402u)] // NecroL80 (necromancer) and AssassinL23 (rogue-assassin) - two classes, same value
		[InlineData(24u, 642u)] // Hakotep and Hoxxling
		[InlineData(80u, 1892u)] // NecroL80, across ten captures - the annotated "gained 1892 skillpoints"
		public void AMeasuredLevel_YieldsItsCapturedPool(uint level, uint expected)
		{
			Assert.Equal(expected, SkillPointCurve.For(level));
		}

		[Theory, InlineData(2u), InlineData(20u), InlineData(40u), InlineData(60u), InlineData(79u)]
		public void AnUnmeasuredLevel_YieldsNothingRatherThanAGuess(uint level)
		{
			Assert.Null(SkillPointCurve.For(level));
		}

		// The whole point of the sparse table is that it holds only what was measured. If someone fills it in, this
		// says so loudly rather than letting invented values ship as retail behaviour - add captures, not rows.
		[Fact]
		public void TheCurve_HoldsOnlyTheMeasuredLevels()
		{
			Assert.Equal<uint[]>([1, 5, 23, 24, 80], SkillPointCurve.Measured.Keys.ToArray());
		}

		// The stat id itself. 0x0F3 was `Unknown32` until the annotated capture pinned it, and the identification
		// rests on exactly one thing: 1892 is the number the player reported and 0x0F3 is the only stat in that
		// 630 KB snapshot carrying it. If the enum value moves, that link is broken.
		[Fact]
		public void TheSkillPointStatId_IsTheOneTheSnapshotCarries()
		{
			Assert.Equal(0x0F3u, (uint)CharacterStatId.SkillPoints);
		}
	}
}
