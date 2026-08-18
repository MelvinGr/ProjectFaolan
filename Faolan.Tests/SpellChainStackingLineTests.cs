using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// #813: a fatality put ALL TEN ranks of `Furious Inspiration` on the killer's buff bar at once, for the full 15
	// seconds, and the visit set was the only reason it stopped at ten rather than looping.
	//
	// `SpellAreaEffects.Kind = 53051` is #784's "when this lands, ALSO run X", and two shapes in the data are not
	// that:
	//
	//   * a rank LADDER - each rank additionally chains the rank BELOW it (299064 -> 299073 -> 299072 -> ...), so
	//     entering anywhere applies everything underneath. 116 edges across 62 carriers.
	//   * a rank TABLE - a nameless, duration-less line header (299074) carrying one edge per rank, seq 0 -> rank 10
	//     down to seq 9 -> rank 1. 105 carriers.
	//
	// ⭐ Both collapse under ONE rule, which is why it is one rule: a `GuiStackingLine` is a MUTUAL-EXCLUSION group,
	// so a walk that applies two members of one line has produced something the client cannot represent. The ladder's
	// two ends share a line; the table's ten targets share a line with each other.
	//
	// ⚠️ Note the two need the rule keyed differently, which is why "suppress edges into the CARRIER's own line" is
	// not enough on its own: the table's header 299074 is in line **0**, so it shares no line with its targets. Only
	// claiming the TARGETS' lines catches it.
	public class SpellChainStackingLineTests
	{
		private const uint FuriousInspirationLine = 44235;
		private const uint Rank10 = 299064, Rank9 = 299073, Rank8 = 299072, Rank1 = 299065;
		private const uint CompanionSpirit = 4188727;

		// The reported case: ten edges into one line yield ONE buff.
		[Fact]
		public void ARankTable_AppliesExactlyOneMemberOfTheLine()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(Rank10, FuriousInspirationLine)); // seq 0
			Assert.False(walk.TryEnter(Rank9, FuriousInspirationLine));
			Assert.False(walk.TryEnter(Rank8, FuriousInspirationLine));
			Assert.False(walk.TryEnter(Rank1, FuriousInspirationLine));
		}

		// The ladder is the same rule reached by a different path - rank 10 chains 9, which chains 8.
		[Fact]
		public void ARankLadder_StopsAtTheRankItEntered()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(Rank8, FuriousInspirationLine)); // entered mid-ladder
			Assert.False(walk.TryEnter(Rank9, FuriousInspirationLine)); // ...and does not climb
			Assert.False(walk.TryEnter(Rank1, FuriousInspirationLine)); // ...nor descend
		}

		// ⛔ And the mechanic itself is untouched - 10,432 of the 53051 edges target a DIFFERENT line and are the real
		// "also run X". A rule that suppressed those would break #784 rather than fix #813.
		[Fact]
		public void AnEdgeIntoADifferentLine_StillRuns()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(Rank10, FuriousInspirationLine));
			Assert.True(walk.TryEnter(CompanionSpirit, 1234));
		}

		// ⚠️ Line 0 means "in no stacking line", which is most spells. It must never collide with itself, or one
		// line-less chain edge would suppress every other one in the same walk.
		[Fact]
		public void LineZero_NeverCollides()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(CompanionSpirit));
			Assert.True(walk.TryEnter(299074)); // the nameless table header - also line 0
			Assert.True(walk.TryEnter(791221));
		}

		// The id-level guard it already had still holds: the same spell twice is one application either way.
		[Fact]
		public void TheSameSpellTwice_IsStillRefused()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(Rank10, FuriousInspirationLine));
			Assert.False(walk.TryEnter(Rank10, FuriousInspirationLine));
		}

		// A registry walk refuses everything, line or no line (FatalityService's case).
		[Fact]
		public void ARegistryWalk_StillRefusesEverything()
		{
			var walk = SpellChainWalk.Registry();

			Assert.False(walk.TryEnter(Rank10, FuriousInspirationLine));
			Assert.False(walk.TryEnter(CompanionSpirit));
		}
	}
}
