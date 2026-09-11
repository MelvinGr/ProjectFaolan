using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The death-penalty ladder. The one thing worth gating here is that the tiers stack rather than replace, and
	// that the tier is read from what the player is already carrying - because the obvious implementation (a counter, or
	// "escalate the one debuff") is wrong, and the wire says so.
	//
	// Ground truth: in NecroL80_..._chat-annotated-4 the playfield reload at 20:24:36 bulk re-applies the surviving buffs
	// with their remaining durations, and Death Penalty 1 (1,261,960 ms left) and Death Penalty 2 (1,775,260 ms left)
	// are both in the same batch - two live countdowns from two separate deaths. The lava death at 20:26:01 then adds
	// Death Penalty 3 on top.
	public class DeathPenaltyTests
	{
		[Fact]
		public void FirstDeath_GivesTierOne()
		{
			Assert.Equal(2544u, DeathPenalty.NextTier([]));
		}

		// The stack grows: each death adds the next tier alongside the ones already running.
		[Fact]
		public void EachDeath_AddsTheNextTier_Alongside()
		{
			Assert.Equal(2543u, DeathPenalty.NextTier([2544]));
			Assert.Equal(2542u, DeathPenalty.NextTier([2544, 2543]));
		}

		// The capture's exact state at 20:24:36 - holding 1 and 2 - and the lava death that followed took 3.
		[Fact]
		public void HoldingOneAndTwo_TheLavaDeathAddsThree()
		{
			Assert.Equal(2542u, DeathPenalty.NextTier([2544, 2543]));
		}

		// A fourth death inside the 30-minute window adds nothing: there is no "Death Penalty 4" anywhere in the corpus
		// or in the Spells table, so the ladder saturates rather than wrapping or throwing.
		[Fact]
		public void FourthDeath_AddsNothing()
		{
			Assert.Equal(0u, DeathPenalty.NextTier([2544, 2543, 2542]));
		}

		// The order held in doesn't matter - the count does. (ActiveBuffs is a dictionary, so no order is guaranteed.)
		[Fact]
		public void OrderOfTheHeldPenalties_IsIrrelevant()
		{
			Assert.Equal(2542u, DeathPenalty.NextTier([2543, 2544]));
		}

		// Ordinary buffs alongside the penalties must not be counted as one.
		[Fact]
		public void OtherBuffs_AreNotCounted()
		{
			// Frozen Hatred / Double XP / Mounted / Shield of the Risen - the rest of that same 20:24:36 batch.
			Assert.Equal(2543u, DeathPenalty.NextTier([4377209, 4552864, 3271445, 3666828, 2544]));
		}

		[Fact]
		public void IsDeathPenalty_RecognisesExactlyTheThreeTiers()
		{
			Assert.True(DeathPenalty.IsDeathPenalty(2544));
			Assert.True(DeathPenalty.IsDeathPenalty(2543));
			Assert.True(DeathPenalty.IsDeathPenalty(2542));

			// 27631 is also named "Death Penalty 2" in the Spells table but carries a 100 s duration instead of the
			// ladder's 30 min, and no capture ever applies it. Matching on the name rather than the id would pull it in.
			Assert.False(DeathPenalty.IsDeathPenalty(27631));
			// 4266542 is a bare "Death Penalty" (60 s) - likewise not part of the ladder.
			Assert.False(DeathPenalty.IsDeathPenalty(4266542));
			Assert.False(DeathPenalty.IsDeathPenalty(0));
		}
	}
}
