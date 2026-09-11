using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// The alternate-advancement economy, against the numbers retail announced.
	//
	// Every expectation here is a value the corpus states in words: retail sends a `FormatFeedbackIIR` carrying
	// `AlternateAdvancementPvEXpGranted` for each grant, with the amount as its first parameter, and the `AAPvEXP`
	// stat push in the same frame moves by exactly that much. `--report aa-xp` re-derives the whole table from the
	// dumps; these are its rows, pinned.
	public class AlternateAdvancementRulesTests
	{
		private const uint MaxLevel = 80;

		// The ordinary payers - `NpcType` 4 with no flag word - at each creature level the corpus caught, against a
		// level-80 killer. Two of them repeat across sessions and are byte-identical each time, so there is no roll.
		[Theory]
		[InlineData(73u, 27u)] // Zingaran Operative, Hired Bodyguard
		[InlineData(74u, 57u)] // Hired Bodyguard
		[InlineData(75u, 91u)] // Zingaran Operative, Hired Bodyguard (two sessions)
		[InlineData(78u, 211u)] // Wharf Rat Thug (twice)
		[InlineData(79u, 234u)] // Wharf Rat Thug
		[InlineData(80u, 258u)] // Ravenous Crocodile Follower
		public void AnOrdinaryKill_PaysWhatRetailAnnounced(uint creatureLevel, uint expected)
		{
			Assert.Equal(expected, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, creatureLevel, 4, 0));
		}

		// The grey rule, measured at its own boundary: level 71 and 72 pay nothing to a level-80 player across
		// 30+ kills in eight sessions, and 73 pays 27. So the cutoff is the player's level minus eight, and the
		// evidence is the pair either side of it rather than the shape of the curve.
		[Theory]
		[InlineData(72u)]
		[InlineData(71u)]
		[InlineData(26u)]
		public void ACreatureAtOrBelowTheCutoff_PaysNothing(uint creatureLevel)
		{
			Assert.Equal(0u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, creatureLevel, 4, 0));
		}

		// The accrual gate. Every kill grant in the corpus was made by a character at the level cap, and no kill in
		// any levelling capture pays at all - while a flat item redemption pays a Tortage lowbie 15,000. A kill feeds
		// the pool only at the cap, which is why the gate is on the killer's level and not just on the gap.
		[Theory]
		[InlineData(6u, 6u)]
		[InlineData(24u, 24u)]
		[InlineData(79u, 79u)]
		public void BelowTheLevelCap_AKillPaysNothing_EvenAtGapZero(uint playerLevel, uint creatureLevel)
		{
			Assert.Equal(0u, AlternateAdvancementRules.KillXp(playerLevel, MaxLevel, creatureLevel, 4, 0));
		}

		// The named tier pays roughly double and one flagged tier ~15% more. Both factors are approximations of a
		// single sample each - see the remarks on Multiplier - so these assert the shipped factor, and the comment
		// carries what retail actually paid.
		[Fact]
		public void ANamedCreature_PaysAboutDouble()
		{
			// Argus the Thief, level 79, NpcType 2: retail paid 461 against the ordinary 234 (x1.970).
			Assert.Equal(468u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, 79, 2, 0));

			// Eerika of the Maidens, level 80, NpcType 2 and the 0x1C0000 flag word: retail paid 510 against 258
			// (x1.977). The flag does not compound - she pays the named rate, not the named rate times 1.15.
			Assert.Equal(516u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, 80, 2, 0x1C0000));
		}

		[Fact]
		public void AFlaggedCreature_PaysFifteenPercentMore()
		{
			// Boros, level 80, NpcType 4 with the 0x1C0000 flag: retail paid 297 against 258 (x1.151).
			Assert.Equal(297u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, 80, 4, 0x1C0000));
		}

		// A boss below the cutoff pays a floor of 2 where an ordinary grey pays nothing - three captured bosses each
		// paid exactly that (Battlemaster Torgvall at 26, the epic-instance Lurking Horror at 28).
		[Fact]
		public void ASubCutoffBoss_PaysTheFloor()
		{
			Assert.Equal(2u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, 26, 3, 0x2)); // Battlemaster Torgvall
			Assert.Equal(0u, AlternateAdvancementRules.KillXp(MaxLevel, MaxLevel, 26, 4, 0)); // an ordinary grey beside him
		}

		// The give-away: a full pool pays 3 tokens into its own specific wallet plus 1 generic, and the remainder
		// carries over rather than being discarded.
		[Fact]
		public void AFullPool_PaysThreeSpecificAndOneGeneric()
		{
			Assert.Equal((0, 0, 999_999u), AlternateAdvancementRules.Drain(999_999));
			Assert.Equal((3, 1, 0u), AlternateAdvancementRules.Drain(AlternateAdvancementRules.XpCap));
			Assert.Equal((3, 1, 258u), AlternateAdvancementRules.Drain(AlternateAdvancementRules.XpCap + 258));
		}

		// One grant can fill the pool more than once over - the corpus's item redemptions pay 100,000 and 125,000 at
		// a time - so the drain loops rather than subtracting the cap once.
		[Fact]
		public void AGrantThatFillsThePoolTwice_PaysTwice()
		{
			Assert.Equal((6, 2, 1u), AlternateAdvancementRules.Drain(2 * AlternateAdvancementRules.XpCap + 1));
		}

		// The announcement itself, byte-for-byte as retail sends it. The `key` is a client-validated content hash -
		// harvested, not derivable - and it is the same on all 19 captured records.
		[Fact]
		public void TheGrantAnnouncement_IsTheCapturedRemoteFormat()
		{
			Assert.Equal(
				"<remoteformat token=\"AlternateAdvancementPvEXpGranted\" category=\"100\" key=\"mgIKCns38CFWy#)9Xb@e\" knubot=\"0\"  ><parameter value=\"2;27\" /></remoteformat>",
				AlternateAdvancementXpCodec.Build(27));

			// Hakotep's daily turn-in: 35 AA xp through a +30% modifier, and 35 == 27 x 1.3.
			Assert.Equal(
				"<remoteformat token=\"AlternateAdvancementPvEXpGrantedWithBonusPercent\" category=\"100\" key=\"bAMpex`PIz&#92;yxtM7wU^B\" knubot=\"0\"  ><parameter value=\"2;35\" /><parameter value=\"1;30\" /></remoteformat>",
				AlternateAdvancementXpCodec.Build(new AlternateAdvancementXpGranted(35, 30)));
		}

		// The three AA point pools the client's own `AAFeatPlannerPointType` enum names are the same three ids its
		// token table carries - which is what makes a perk's price and the wallet it is paid from one field.
		[Fact]
		public void ThePerkPointTypes_AreTheTokenPoolIds()
		{
			Assert.Equal(2, (int)TokenPool.AltAdvancementPvPToken); // e_PvP
			Assert.Equal(3, (int)TokenPool.AltAdvancementPvEToken); // e_PvE
			Assert.Equal(4, (int)TokenPool.AltAdvancementGeneralToken); // e_Generic
		}
	}
}
