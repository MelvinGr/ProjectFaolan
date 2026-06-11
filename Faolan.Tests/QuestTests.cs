using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Quest state packets (docs/quests-and-combat.md), byte-verified against the hoxxling-login-gameplay capture
	// (quest 0x34A7 = 13479, player 0x01316E71).
	public class QuestTests
	{
		[Fact]
		public void BuildQuestIir_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestIir(0x01316E71, 0x34A7, WorldQuest.StateActive);
			Assert.Equal(
				"97e023140000c35001316e71000000000100000002000034a700000000",
				System.Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildQuestGoal_MatchesCapture()
		{
			var body = WorldQuest.BuildQuestGoal(0x01316E71, 0x34A7, 0x1670, 1);
			Assert.Equal(
				"0774c8c10000c35001316e7101000034a700001670000000010000000000000000",
				System.Convert.ToHexString(body).ToLowerInvariant());
		}

		private static CharacterQuest KillQuest(string target, uint progress, uint required, QuestState state = QuestState.Active) =>
			new CharacterQuest { QuestId = 1, State = state, GoalTargetName = target, GoalProgress = progress, GoalRequired = required };

		[Fact]
		public void AdvancesOnKill_MatchesActiveIncompleteTarget()
		{
			Assert.True(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5), "Scavenger Lackey"));
			Assert.True(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 4, 5), "scavenger lackey")); // case-insensitive
		}

		[Fact]
		public void AdvancesOnKill_RejectsNonMatching()
		{
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5), "Wolf")); // wrong target
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 5, 5), "Scavenger Lackey")); // already done
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest("Scavenger Lackey", 0, 5, QuestState.Completed), "Scavenger Lackey")); // completed
			Assert.False(QuestProgress.AdvancesOnKill(KillQuest(null, 0, 5), "Scavenger Lackey")); // no kill objective
		}
	}
}
