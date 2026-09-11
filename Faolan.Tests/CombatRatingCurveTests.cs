using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The level-scaled rating quad - AttackRating / DefenseRating / MagicRating / MagicalResist.
	//
	// Unlike the skill-point pool next door this one is a formula. It was recovered by sweeping every captured
	// StatIIR record carrying one of the four ids: 2,100 records, 31 captures, 13 characters across 6 classes,
	// 8 distinct values - and all 2,100 fit `400 x level - 300` exactly.
	public class CombatRatingCurveTests
	{
		// Every value the corpus holds, with the level it was measured at. The three beach captures walk a character
		// 1 -> 5 a level at a time, which is what makes the low end a line rather than five coincidences; 23/24/80
		// come from levelled characters (a rogue-assassin, Hakotep, and the level-80 necromancer whose 31,700 was
		// mistaken for a spellweave burst constant).
		[Theory]
		[InlineData(1u, 100u)]
		[InlineData(2u, 500u)]
		[InlineData(3u, 900u)]
		[InlineData(4u, 1300u)]
		[InlineData(5u, 1700u)]
		[InlineData(23u, 8900u)]
		[InlineData(24u, 9300u)]
		[InlineData(80u, 31700u)]
		public void MatchesEveryCapturedValue(uint level, uint expected)
		{
			Assert.Equal(expected, CombatRatingCurve.For(level));
		}

		// The corpus is read the other way too: no captured value is off the line, so a value that does not invert to
		// a whole level would have falsified the formula. Checking the inverse keeps that property explicit.
		[Fact]
		public void EveryCapturedValue_InvertsToAWholeLevel()
		{
			foreach (var value in new uint[] { 100, 500, 900, 1300, 1700, 8900, 9300, 31700 })
			{
				Assert.Equal(0u, (value + 300) % 400);
				Assert.Equal(value, CombatRatingCurve.For((value + 300) / 400));
			}
		}

		// All four ids carry the same value in the same send - true of every one of the 525 sends of each id - so the
		// quad is one number, not four.
		[Fact]
		public void TheQuadIsTheFourClientNamedRatingStats()
		{
			Assert.Equal(
				[CharacterStatId.AttackRating, CharacterStatId.DefenseRating, CharacterStatId.MagicRating, CharacterStatId.MagicalResist],
				CombatRatingCurve.Stats);
			Assert.Equal(4, CombatRatingCurve.Stats.Distinct().Count());
		}

		// Level 0 does not exist. Without the clamp the formula underflows to -300, which on an unsigned wire field
		// would ship 4,294,966,996 - the shape of bug the create-editor sheet's negative resists already are.
		[Fact]
		public void LevelZero_IsTreatedAsLevelOne()
		{
			Assert.Equal(CombatRatingCurve.For(1), CombatRatingCurve.For(0));
		}

		[Fact]
		public void ItIsLinear_400PerLevel()
		{
			for (var level = 1u; level < 80u; level++)
				Assert.Equal(400u, CombatRatingCurve.For(level + 1) - CombatRatingCurve.For(level));
		}
	}
}
