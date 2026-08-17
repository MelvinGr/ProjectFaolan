using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Quest;
using Xunit;

namespace Faolan.Tests
{
	// #857: `MarkerStateFor` answered `None` for anything the dialogue graph does not list as a giver - and 38 of the
	// 62 NPC templates retail ever lights grant nothing at all. So we sent 0x00 for every one of them.
	//
	// The rule that fills the gap comes from #848 and is two measured NECESSARY conditions plus one exclusion:
	//
	//   1. a quest LINE the player has not begun runs through this NPC. Zero exceptions in 1,559 marker packets - of
	//      every packet about an NPC whose lines the player had all started, not one carried silver. "Runs through"
	//      covers the goal-TARGET relation, not just grants: 85% of retail's silver is on an NPC some untouched
	//      quest's goal will send the player to.
	//   2. the quest sits in the observed level band, -2..+1 of the player's own level. Every lit observation in the
	//      corpus is inside it and every delta outside is unanimously dark (0/243 far above, 0/164 far below).
	//   3. ...and the goal is not a KILL or a LOOT. A marker says "there is something here for you"; a mob the quest
	//      sends you to kill is not that, and retail agrees without exception - Saddur is 0x00 in all 76 of his marker
	//      packets and Scavenger Tuut in all 13, both while being goal targets of the live Tortage chain.
	//
	// ⚠️ Necessary, NOT sufficient, and shipped knowing it. Measured by `--report quest-markers`, the rule lights 6
	// packets across 2 NPCs that retail leaves dark (0.03% of the dark population) and cannot demonstrate a positive
	// hit at all, because every one of the 50 silver packets whose viewer level the corpus captured sits on an NPC the
	// quest harvest holds no relation for. A third gate exists; #848 has it.
	public class QuestMarkerChainTests
	{
		private const uint UnstartedLine = 222, StartedLine = 1054;

		private static QuestDefinition Quest(uint id, uint line, uint level)
		{
			return new QuestDefinition
			{
				QuestId = id,
				QuestLineId = line,
				Level = level
			};
		}

		// The rule firing: a chain the player has never begun passes through this NPC, at their own level.
		[Fact]
		public void AnUntouchedLineAtThePlayersLevel_LightsSilver()
		{
			Assert.True(QuestMarkerResolver.LightsSilver([Quest(10925, UnstartedLine, 20)], new HashSet<uint>(), 20));
		}

		// ⭐ The condition that took #848 three rounds to find: it is the LINE, not the quest. Casilda is the target of
		// an untouched quest 178 times while dark, because that quest is in the line the player is already ON.
		[Fact]
		public void AQuestInALineThePlayerHasBegun_StaysDark()
		{
			Assert.False(QuestMarkerResolver.LightsSilver([Quest(13480, StartedLine, 20)], new HashSet<uint> { StartedLine }, 20));
		}

		// The level band, both edges - the observed lit range and not one unit wider.
		[Theory]
		[InlineData(20, 22, true)] // delta -2, the lowest lit observation
		[InlineData(20, 19, true)] // delta +1, the highest
		[InlineData(20, 20, true)]
		[InlineData(20, 27, false)] // delta -7, dark in the corpus
		[InlineData(20, 1, false)] // far below, unanimously dark
		[InlineData(80, 4, false)]
		public void TheLevelBandIsTheObservedLitRange(uint playerLevel, uint questLevel, bool lit)
		{
			Assert.Equal(lit, QuestMarkerResolver.LightsSilver([Quest(10925, UnstartedLine, questLevel)], new HashSet<uint>(), playerLevel));
		}

		// ⛔ A quest that states NO level cannot be placed in the band, and an unstated level is not evidence of a lit
		// one - so it goes dark rather than being waved through. This is the clause that decides the behaviour for the
		// bulk of the catalog, where Level is 0.
		[Fact]
		public void AQuestStatingNoLevel_StaysDark()
		{
			Assert.False(QuestMarkerResolver.LightsSilver([Quest(10925, UnstartedLine, 0)], new HashSet<uint>(), 20));
		}

		// A fold, like the giver fold beside it: an NPC several chains run through is silver if ANY of them qualifies.
		// Turach is the case - dark for line 241 which he is granting, silver for 222 and 1305 which he is not.
		[Fact]
		public void AnyQualifyingChain_IsEnough()
		{
			IEnumerable<QuestDefinition?> through = [Quest(12371, StartedLine, 20), Quest(10925, UnstartedLine, 20)];

			Assert.True(QuestMarkerResolver.LightsSilver(through, new HashSet<uint> { StartedLine }, 20));
		}

		// ...and an NPC with nothing running through it is scenery, which is the distinction 0x00 carries and 0x01
		// does not: 52 NPCs in one capture returned only 0x00, and they are the ones no quest touches.
		[Fact]
		public void AnNpcNoChainRunsThrough_StaysDark()
		{
			Assert.False(QuestMarkerResolver.LightsSilver([], new HashSet<uint>(), 20));
		}
	}
}
