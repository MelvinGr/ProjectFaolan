using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// The rule that decides which capture of a screen SURVIVES when several captured the same one.
	//
	// It needs its own tests because the corpus barely exercises it: of 242 merge keys only 29 have more than one
	// candidate, and only TWO of those differ in packet count - so a regression here would sail through a full
	// import untouched. It matters more than that coverage suggests, because it is also the rule an incremental
	// import has to apply against already-stored rows (Gitea #419): a truncated dump must never displace a screen
	// already captured in full.
	public class ConversationMergeRuleTests
	{
		private const uint TypeId = 42008;

		// COMPLETENESS decides: the capture with more response packets wins, whichever order they arrive in.
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void RicherCaptureWins_RegardlessOfOrder(bool richerFirst)
		{
			var thin = Entry("", packets: 1);
			var rich = Entry("", packets: 3);

			var merged = ConversationExtractor.Merge(richerFirst
				? [rich, thin]
				: [thin, rich]);

			Assert.Equal(3, Assert.Single(merged).Packets.Count);
		}

		// The grant is harvested from whichever capture happened to walk the ACCEPT branch. It must survive even when
		// that capture loses the completeness contest - otherwise re-importing a dump that only browsed the dialogue
		// would silently un-grant the quest.
		[Fact]
		public void GrantSurvivesEvenWhenItsCaptureLoses()
		{
			var thinWithGrant = Entry("", packets: 1, grant: 13479);
			var richWithout = Entry("", packets: 3);

			foreach (var merged in new[] { ConversationExtractor.Merge([thinWithGrant, richWithout]), ConversationExtractor.Merge([richWithout, thinWithGrant]) })
			{
				var only = Assert.Single(merged);
				Assert.Equal(3, only.Packets.Count); // the richer screen still wins
				Assert.Equal(13479u, only.GrantQuestId); // ...but carries the grant the thin one saw
			}
		}

		// Distinct answer paths are distinct screens - merging must union the option tree, not collapse it. This is
		// what makes several partial walks add up to a whole dialogue.
		[Fact]
		public void DifferentAnswerPathsAreDifferentScreens()
		{
			var merged = ConversationExtractor.Merge([Entry(""), Entry("0"), Entry("0>1")]);

			Assert.Equal(3, merged.Count);
			Assert.Equal(["", "0", "0>1"], merged.Select(e => e.AnswerPath).OrderBy(p => p.Length).ThenBy(p => p).ToArray());
		}

		// A quest-state-gated variant is a SEPARATE screen, not a competitor: Casilda's pre- and post-quest greetings
		// share (name, type, path) and must both survive, or the runtime loses the ability to serve either.
		[Fact]
		public void QuestGatedVariantsAreNotMergedTogether()
		{
			var unconditional = Entry("", packets: 3);
			var gated = Entry("", packets: 1, conditionQuest: 13480, conditionState: QuestState.Completed);

			var merged = ConversationExtractor.Merge([unconditional, gated]);

			Assert.Equal(2, merged.Count);
			Assert.Contains(merged, e => e.ConditionQuestId == null);
			Assert.Contains(merged, e => e.ConditionQuestId == 13480);
		}

		// Two NPCs saying the same thing at the same path are still two screens.
		[Fact]
		public void DifferentNpcsAreNotMergedTogether()
		{
			var merged = ConversationExtractor.Merge([Entry("", npcName: "Casilda"), Entry("", npcName: "Kalanthes")]);

			Assert.Equal(2, merged.Count);
		}

		private static ConversationWalk Entry(string answerPath, int packets = 1, uint grant = 0, uint? conditionQuest = null, QuestState? conditionState = null, string npcName = "Casilda")
		{
			return new ConversationWalk(TypeId, answerPath, 0x4DEE9, [.. Enumerable.Range(0, packets).Select(i => new[] { (byte)i })], npcName, ConditionQuestId: conditionQuest,
				ConditionQuestState: conditionState, GrantQuestId: grant);
		}
	}
}
