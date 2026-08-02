using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #559: retail draws FOUR quest-marker states, and the fourth is the greyed/silver '!' - "this NPC is part of a
	// chain but has nothing for you right now". One half of it (the quest is held, or already turned in) was already
	// sent; the other is a quest the player does NOT hold whose line has not reached it yet. Without that half a giver
	// deep in a chain drew the GOLD '!' from the moment you logged in, offering a quest you could not take.
	//
	// The oracle is `xxx_mage-necromancer_login_play2_tortage_city_first-quests`, where Turach (0x27929) and Laranga
	// (0x27917) mirror each other across the whole session. Turach reads 0x01 at login: his "The Quarry" is step 5 of
	// quest line 241 and the player has done none of steps 1-4.
	public class QuestMarkerLineTests
	{
		private const uint TheQuarry = 12371; // Turach's giver quest - step 5 of line 241
		private const uint TortageLine = 241;

		[Fact]
		public async Task TheQuarry_IsStepFive_OfALineTheCaptureWalksInOrder()
		{
			// The data the rule stands on: line 241 is genuinely ordered, so a lower JournalSequence really does mean
			// "earlier". If this line ever came back all-zero the gate would silently stop gating (see LineReached).
			var line = await TortageSteps();

			Assert.Equal(
				new[] { "The Slaver", "The Acheronian Gate", "The City of Tortage", "The Blacksmith", "The Quarry" },
				line.Take(5).Select(q => q.Name).ToArray());
			Assert.Equal(5u, line.Single(q => q.QuestId == TheQuarry).JournalSequence);
		}

		[Fact]
		public async Task Turach_AtLogin_IsNotOfferable_ButIsOnceTheChainReachesHim()
		{
			var line = await TortageSteps();
			var quarry = line.Single(q => q.QuestId == TheQuarry);

			// t=9.3s, a fresh character: nothing done, so the line has not reached step 5 -> silver, not gold.
			Assert.False(WorldBroadcaster.LineReached(quarry, line, []));

			// Steps 1-4 done (the capture's state by t=123.3s, when Turach actually grants it) -> gold.
			var doneThroughFour = line.Where(q => q.JournalSequence is > 0 and < 5)
				.Select(q => new CharacterQuest { QuestId = q.QuestId, State = QuestState.Completed })
				.ToArray();
			Assert.True(WorldBroadcaster.LineReached(quarry, line, doneThroughFour));

			// An earlier step merely ACTIVE is not done - the chain has not passed it, so he stays silver.
			var stillOnStepFour = doneThroughFour
				.Select(q => q.QuestId == line.Single(s => s.JournalSequence == 4).QuestId
					? new CharacterQuest { QuestId = q.QuestId, State = QuestState.Active }
					: q)
				.ToArray();
			Assert.False(WorldBroadcaster.LineReached(quarry, line, stillOnStepFour));
		}

		[Fact]
		public async Task TheFirstStepOfALine_IsAlwaysOfferable()
		{
			// Nothing precedes step 1, so a fresh character sees the gold '!' on the chain's head - the gate must not
			// silver every giver in the world.
			var line = await TortageSteps();
			var first = line.Single(q => q.JournalSequence == 1);

			Assert.True(WorldBroadcaster.LineReached(first, line, []));
		}

		[Fact]
		public async Task AnUnorderableLine_StaysOfferable()
		{
			// JournalSequence 0 is a legitimate captured value, not "unordered" - 182 lines carry it on every step. A
			// line that cannot say what comes first must not gate, or every one of its givers would go silver forever.
			var flat = new[]
			{
				new QuestDefinition { QuestId = 1, JournalSequence = 0 },
				new QuestDefinition { QuestId = 2, JournalSequence = 0 }
			};

			Assert.True(WorldBroadcaster.LineReached(flat[1], flat, []));
		}

		private static async Task<QuestDefinition[]> TortageSteps()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			return await db.QuestDefinitions
				.Where(q => q.QuestLineId == TortageLine && q.TextRefs.Any())
				.OrderBy(q => q.JournalSequence)
				.ToArrayAsync(TestContext.Current.CancellationToken);
		}
	}
}
