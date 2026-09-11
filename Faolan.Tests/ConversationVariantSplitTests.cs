using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// The rule that turns "one NPC, several captured versions of the same screen" into quest-gated variants, derived
	// from the quest state each capture was in. A table keyed on capture file names instead would silently drop a
	// screen from any capture it does not list.
	//
	// The corpus exercises it at 31 groups, but only through the importer, which the suite cannot run (the captures
	// are a separate private repo). These pin the rule itself on the shapes the corpus actually contains.
	public class ConversationVariantSplitTests
	{
		private const uint TypeId = 42008;

		// The common case by far: every capture of the screen says the same thing, so it stays one unconditional row.
		// Gating on "whatever quest happened to be active" would make a vendor's greeting conditional on an unrelated
		// quest, which is the failure the conflict-driven split exists to avoid.
		[Fact]
		public void ConsistentCaptures_StayUnconditional()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493, 7615], quests: (13479, QuestState.Active)),
				Entry("", [12493, 7615], quests: (10238, QuestState.Active))
			]);

			Assert.All(split, e => Assert.Null(e.ConditionQuestId));
		}

		// Casilda's greeting, reduced: the offer (no chain progress) and the in-progress line. The less-informed
		// capture is the fallback a fresh character gets; the other is gated on the quest its captures agree on.
		[Fact]
		public void DisagreeingCaptures_SplitOnTheQuestTheyAgreeOn()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493, 7615]), // the offer - the capture never showed us a quest state
				Entry("", [12493, 12557], quests: (13479, QuestState.Active)),
				Entry("", [12493, 12557], quests: [(10238, QuestState.Active), (13479, QuestState.Active)])
			]);

			var offer = Assert.Single(split, e => Signature(e) == "12493,7615");
			Assert.Null(offer.ConditionQuestId);

			// 10238 is carried by only one of the two in-progress captures, so it cannot be what selects the variant.
			foreach (var gated in split.Where(e => Signature(e) == "12493,12557"))
			{
				Assert.Equal(13479u, gated.ConditionQuestId);
				Assert.Equal(QuestState.Active, gated.ConditionQuestState);
			}
		}

		// A pair that the fallback also carries cannot select anything, so it is skipped in favour of one that is
		// unique to this variant.
		[Fact]
		public void GateIsNotAPairTheFallbackAlsoCarries()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493, 7615], quests: (10238, QuestState.Active)),
				Entry("", [26909, 7617], quests: [(10238, QuestState.Active), (13480, QuestState.Active)])
			]);

			var gated = Assert.Single(split, e => Signature(e) == "26909,7617");
			Assert.Equal(13480u, gated.ConditionQuestId);
		}

		// Casilda's later chain greetings all sit on top of a still-active escort, so active beats completed when both
		// would do: a gate on a quest the player finished long ago is true from far earlier in the chain, and would win
		// the runtime's most-recent-quest tiebreak against the beat the player is actually on.
		[Fact]
		public void ActiveGatesArePreferredOverCompletedOnes()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493, 7615]),
				Entry("", [20127, 17474], quests: [(10238, QuestState.Completed), (12369, QuestState.Active)])
			]);

			var gated = Assert.Single(split, e => Signature(e) == "20127,17474");
			Assert.Equal(12369u, gated.ConditionQuestId);
			Assert.Equal(QuestState.Active, gated.ConditionQuestState);
		}

		// Two captures of the same screen, one of which happened to carry a trailing close window, are different
		// signatures with identical quest state. That pair must not cost the group its other gates: the Casilda ""
		// group is six real variants, and collapsing it to "keep the richest" loses five of them.
		[Fact]
		public void IndistinguishableVariants_DoNotCostTheGroupItsOtherGates()
		{
			var deep = new (uint, QuestState)[] { (12369, QuestState.Active) };
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493, 7615]),
				Entry("", [26909, 7617], quests: (13480, QuestState.Active)),
				Entry("", [20127, 17474], quests: deep),
				Entry("", [20127, 17474, 0], quests: deep) // the same screen, captured with its close window
			]);

			Assert.Equal(13480u, Assert.Single(split, e => Signature(e) == "26909,7617").ConditionQuestId);
			// The indistinguishable pair shares one gate, so Merge picks the richer of the two rather than the group
			// losing every gate it had.
			Assert.All(split.Where(e => Signature(e).StartsWith("20127")), e => Assert.Equal(12369u, e.ConditionQuestId));
		}

		// The quest state is not the only condition retail varies a screen on. Elethia says "Hello.{social greet}" the
		// first time you open her window and "What now?" every time after, with the same option list and the same quest
		// state - so the split falls to the greeting count.
		[Fact]
		public void VariantsTheQuestStateCannotSplit_FallBackToTheGreetingCount()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [10612], repeat: false),
				Entry("", [10614], repeat: true),
				Entry("", [10614], repeat: true)
			]);

			Assert.False(Assert.Single(split, e => Signature(e) == "10612").RequiresRepeatGreeting);
			Assert.All(split.Where(e => Signature(e) == "10614"), e => Assert.True(e.RequiresRepeatGreeting));
		}

		// ...but only when the flag splits the group cleanly. A variant the corpus caught on both a first meeting and a
		// repeat is a correlation, not the condition, and putting a gate on it would hide the screen from half the
		// players it belongs to - so that group stays ungated and says so.
		[Fact]
		public void AGreetingCountThatOnlyCorrelates_IsNotTakenAsTheGate()
		{
			var report = new List<string>();
			var split = ConversationVariants.SplitConflicts([
				Entry("", [10612], repeat: false),
				Entry("", [10614], repeat: true),
				Entry("", [10614], repeat: false) // the terse line was also captured on a first meeting
			], report);

			Assert.All(split, e => Assert.Null(e.RequiresRepeatGreeting));
			Assert.Contains(report, r => r.Contains("none the captured quest state can tell apart"));
		}

		// The quest gate still wins where it applies: the greeting count is only consulted once the state has failed.
		[Fact]
		public void TheQuestGateIsPreferredOverTheGreetingCount()
		{
			var split = ConversationVariants.SplitConflicts([
				Entry("", [12493], repeat: false),
				Entry("", [26909], repeat: true, quests: (13480, QuestState.Active))
			]);

			Assert.All(split, e => Assert.Null(e.RequiresRepeatGreeting));
			Assert.Equal(13480u, Assert.Single(split, e => Signature(e) == "26909").ConditionQuestId);
		}

		// The screen ids this entry was built from - the test's own handle on which variant a row is.
		private static string Signature(ConversationWalk e)
		{
			return string.Join(",", e.Packets.Select(p => Regex.Match(Encoding.Latin1.GetString(p), "id=\"(\\d+)\"").Groups[1].Value));
		}

		private static ConversationWalk Entry(string answerPath, uint[] screenIds, bool repeat = false, params (uint Quest, QuestState State)[] quests)
		{
			var packets = screenIds
				.Select(id => Encoding.Latin1.GetBytes($"<remoteformat id=\"{id}\" category=\"50002\" key=\"k\" knubot=\"0\"  ></remoteformat>"))
				.ToList();

			return new ConversationWalk(TypeId, answerPath, 0x4DEE9, packets, "Casilda",
				QuestStates: quests.ToDictionary(q => q.Quest, q => q.State), RepeatGreeting: repeat);
		}
	}
}
