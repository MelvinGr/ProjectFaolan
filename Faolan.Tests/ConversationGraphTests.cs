using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// The fold that turns the harvested WALKS into the dialogue GRAPH (#642).
	//
	// A capture is a walk - one route down the tree, one row per position - which is the right shape to harvest and the
	// wrong shape to store. These pin the four properties the graph exists for: a screen reached two ways is ONE row, a
	// menu that loops back is an EDGE rather than an infinite unrolling, a branch is named by the option's own LDB id
	// rather than by an index, and two genuinely different screens that happen to share their text still separate.
	//
	// Built from ConversationPacket rows directly rather than from wire bytes: the fold reads the columnized form, and
	// the byte-exact decode that produces it is gated by ConversationConstructTests / KnubotCodecTests already.
	public class ConversationGraphTests
	{
		private const string Npc = "Casilda";

		// The NPC's TEMPLATE - the screen's identity since #646, so every synthetic walk here carries Casilda's real one.
		private const uint NpcTemplate = 9411;
		private const uint TypeId = 42008;

		// The headline case. Two routes reach the same screen, so the answer-path tree stored it twice - once per route,
		// with different keys and no way to tell they were the same screen.
		[Fact]
		public void AConvergentScreen_IsOneNodeWithTwoInboundEdges()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615, 8149)),
				Walk("0", Line(200), Options(0)),
				Walk("1", Line(200), Options(0))
			]);

			// Three walks, but the two children say exactly the same thing, so there are only two screens.
			Assert.Equal(2, graph.Nodes.Count);
			var target = graph.Nodes.Single(n => n.ContentKey.Contains("200"));
			Assert.Equal(2, graph.Edges.Count(e => e.ToNodeId == target.Id));
			Assert.Equal([7615u, 8149u], [.. graph.Edges.Where(e => e.ToNodeId == target.Id).Select(e => e.OptionId).OrderBy(o => o)]);
		}

		// The defect the path key could not express at all: a menu whose option returns to a screen already on the
		// route. Under the old key each lap had to be captured separately, and the lap after the deepest one closed the
		// window on the player. As a graph it is one edge back.
		[Fact]
		public void ALoopBecomesAnEdgeBackIntoTheGraph()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615)),
				Walk("0", Line(200), Options(8149)),
				Walk("0>0", Line(100), Options(7615)) // back to the greeting's screen
			]);

			Assert.Equal(2, graph.Nodes.Count);
			var greeting = graph.Nodes.Single(n => n.ContentKey.Contains("100"));
			var second = graph.Nodes.Single(n => n.ContentKey.Contains("200"));
			Assert.Contains(graph.Edges, e => e.FromNodeId == greeting.Id && e.OptionId == 7615 && e.ToNodeId == second.Id);
			Assert.Contains(graph.Edges, e => e.FromNodeId == second.Id && e.OptionId == 8149 && e.ToNodeId == greeting.Id);
		}

		// The branch is named by the option's own LDB id, taken off the screen that OFFERED it - never by the index.
		// The same index means different things on different variants of one screen, which is what made the path key
		// re-select a child by quest gate rather than by what the player actually clicked.
		[Fact]
		public void BranchesAreNamedByTheOptionsOwnLdbId()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(12557, 7615, 0)),
				Walk("2", Line(300), Options(0))
			]);

			var edge = Assert.Single(graph.Edges);
			Assert.Equal(0u, edge.OptionId); // index 2 on that screen IS the "Goodbye" option
		}

		// Retail reuses the same line and option strings at two different script states and the follow-ups differ
		// (Tina 2683, Tina 12413, Laranga 19827, Turach 2822). Those must NOT merge - and the split has to come from a
		// SHARED slot disagreeing, not merely from one walk being shorter than the other.
		[Fact]
		public void SameContentWithConflictingFollowUps_IsDiscriminated()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615)),
				Walk("0", Line(200), Options(8149)),
				Walk("0>0", Line(400), Options(0)),
				Walk("1", Line(100), Options(7615)), // same content as the greeting...
				Walk("1>0", Line(500), Options(0)) // ...but option 7615 leads somewhere else
			]);

			var twins = graph.Nodes.Where(n => n.ContentKey.Contains("100")).ToList();
			Assert.Equal(2, twins.Count);
			Assert.Equal([0u, 1u], [.. twins.Select(n => n.Discriminator).OrderBy(d => d)]);
			Assert.Contains(graph.Report, r => r.Contains("distinct screens share the content"));
		}

		// ...and the converse, which is what makes a truncated capture harmless: a walk that simply STOPS earlier says
		// nothing about the slots it never exercised, so it merges into the fuller screen rather than splitting off as a
		// near-duplicate. This is what lets one lap of a loop and the full menu be the same row.
		[Fact]
		public void AShorterWalkOfTheSameScreenMergesInsteadOfSplitting()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615)),
				Walk("0", Line(200), Options(8149)),
				Walk("0>0", Line(300), Options(7615)), // the same screen as "" - and nobody walked past it
				Walk("0>0>0", Line(400), Options(0))
			]);

			Assert.Equal(4, graph.Nodes.Count); // 100, 200, 300, 400 - all distinct content
			Assert.DoesNotContain(graph.Report, r => r.Contains("distinct screens share the content"));
		}

		// The three ways retail opens a window become one enum on the root, replacing the ServerInitiated / TypeId 0 /
		// AnswerPath == "" trio that used to encode it across three columns of the screen.
		[Theory]
		[InlineData(false, null, ConversationTrigger.Talk)]
		[InlineData(true, 1044692756u, ConversationTrigger.UseWorldObject)]
		[InlineData(true, null, ConversationTrigger.Approach)]
		public void TheRootRecordsHowRetailOpenedTheWindow(bool serverInitiated, uint? worldObjectId, ConversationTrigger expected)
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(0)) with
				{
					Walk = Walk("", Line(100), Options(0)).Walk with
					{
						ServerInitiated = serverInitiated,
						TriggerWorldObjectId = worldObjectId
					}
				}
			]);

			Assert.Equal(expected, Assert.Single(graph.Entries).Trigger);
		}

		// The grant belongs to the BRANCH, not to the screen: Turach's 2832 was harvested twice with identical content,
		// once granting quest 12371 and once granting nothing, and the difference is how you got there.
		[Fact]
		public void TheGrantRidesTheBranchThatReachesTheScreen()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615)),
				WalkGranting("0", 13479, Line(200), Options(0))
			]);

			Assert.Equal(13479u, Assert.Single(graph.Edges).GrantQuestId);
		}

		// The fold has to be a pure function of the corpus: two runs over the same walks in a different order must
		// produce the same ids and the same Discriminators, or faolan.sql churns for nothing on every re-import.
		[Fact]
		public void TheFoldIsDeterministicRegardlessOfWalkOrder()
		{
			List<ConversationGraph.Harvested> walks =
			[
				Walk("", Line(100), Options(7615)),
				Walk("0", Line(200), Options(8149)),
				Walk("0>0", Line(400), Options(0)),
				Walk("1", Line(100), Options(7615)),
				Walk("1>0", Line(500), Options(0))
			];

			var forward = ConversationGraph.Fold(walks);
			var backward = ConversationGraph.Fold([.. Enumerable.Reverse(walks)]);

			Assert.Equal(forward.Nodes.Select(n => n.Id).OrderBy(i => i), backward.Nodes.Select(n => n.Id).OrderBy(i => i));
			Assert.Equal(forward.Edges.Select(e => (e.FromNodeId, e.OptionId, e.ToNodeId)).OrderBy(e => e.FromNodeId).ThenBy(e => e.OptionId),
				backward.Edges.Select(e => (e.FromNodeId, e.OptionId, e.ToNodeId)).OrderBy(e => e.FromNodeId).ThenBy(e => e.OptionId));
		}

		// A branch off a screen whose option list was never captured has no durable name, so it is dropped and SAID -
		// inventing an index-shaped id would put a branch in the graph that nothing on the wire supports.
		[Fact]
		public void ABranchOffAnUncapturedOptionListIsDroppedAndReported()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100)), // the capture missed the answer list
				Walk("0", Line(200), Options(0))
			]);

			Assert.Empty(graph.Edges);
			Assert.Contains(graph.Report, r => r.Contains("option list was never captured"));
		}

		// The harvested parent link is what names a branch (#642), and it beats the answer path where the two disagree.
		// Casilda's `2` sub-tree is the case that forced it: harvested under her five-option 12373 greeting but itself
		// ungated, so a path-derived parent attached it to the three-option unconditional greeting and index 2 named
		// "Goodbye" instead of the option actually clicked.
		[Fact]
		public void TheHarvestedParentLink_BeatsTheAnswerPath()
		{
			var gated = Walk("", Line(100), Options(17474, 17488, 17481, 0)) with
			{
				Walk = Walk("", Line(100), Options(17474, 17488, 17481, 0)).Walk with
				{
					ConditionQuestId = 12373,
					ConditionQuestState = QuestState.Active
				}
			};
			var plain = Walk("", Line(900), Options(7615, 0)); // the unconditional greeting, a DIFFERENT option list
			var child = Walk("2", Line(300), Options(0)) with
			{
				Walk = Walk("2", Line(300), Options(0)).Walk with
				{
					ParentContentKey = "50002:100,50001:17474,50001:17488,50001:17481,50001:0",
					OptionId = 17481
				}
			};

			var graph = ConversationGraph.Fold([plain, gated, child]);

			var edge = Assert.Single(graph.Edges);
			Assert.Equal(17481u, edge.OptionId); // the option the player actually clicked...
			Assert.Equal(graph.Nodes.Single(n => n.ContentKey.StartsWith("50002:100")).Id, edge.FromNodeId); // ...on the gated greeting
		}

		// The replay gate has to walk each hop under the gate THAT HOP was harvested under, not under the leaf walk's
		// (#644). Turach is the shape: his 10320-Active greeting offers 2142 both unconditionally and gated on 12407
		// Completed, leading to two different screens, and the walk hanging under the gated one is itself gated on 10320
		// Active. Matching the leaf's gate at hop 1 found neither branch, fell through to the unconditional one, and the
		// rest of the route did not exist there - so the deepest walks of his tree stopped replaying.
		[Fact]
		public void ARouteCrossingAGateOtherThanItsOwn_StillReplays()
		{
			var root = Gated(Walk("", Line(2833), Options(2142, 0)), 10320, QuestState.Active);
			var rootKey = "50002:2833,50001:2142,50001:0";
			var plain = Gated(Under(Walk("0", Line(2820), Options(2144, 0)), rootKey, 2142), 10320, QuestState.Active);
			var deeper = Gated(Under(Walk("0", Line(2820), Options(2144, 17310, 0)), rootKey, 2142), 12407, QuestState.Completed);
			var leaf = Gated(Under(Walk("0>0", Line(2822), Options(0)), "50002:2820,50001:2144,50001:17310,50001:0", 2144), 10320, QuestState.Active);

			var graph = ConversationGraph.Fold([root, plain, deeper, leaf]);

			// Both branches of 2142 are in the graph, and the leaf hangs off the 12407-gated one...
			Assert.Equal(2, graph.Edges.Count(e => e.OptionId == 2142));
			Assert.Equal(graph.Nodes.Single(n => n.ContentKey.Contains("17310")).Id, graph.Edges.Single(e => e.OptionId == 2144).FromNodeId);
			// ...and every walk still replays, which is the property that broke.
			Assert.DoesNotContain(graph.Report, r => r.Contains("unreplayable"));
		}

		// The answer-path FALLBACK has to shortlist on what each candidate actually offered. A path names several walks -
		// one per quest-gated variant of that screen - and Casilda's '0>0' includes a variant that carries no answer list
		// at all, so picking by gate alone landed on a screen with no button for index 0 and the branch was dropped (#644).
		[Fact]
		public void ThePathFallbackPrefersAParentThatOfferedTheClickedIndex()
		{
			var graph = ConversationGraph.Fold([
				Walk("", Line(100), Options(7615)),
				Walk("0", Line(200)), // this capture of the screen never recorded its option list...
				Gated(Walk("0", Line(200), Options(8149, 0)), 12407, QuestState.Active), // ...this one did
				Walk("0>0", Line(300), Options(0))
			]);

			Assert.Equal(8149u, graph.Edges.Single(e => e.ToNodeId == graph.Nodes.Single(n => n.ContentKey.Contains("300")).Id).OptionId);
			Assert.DoesNotContain(graph.Report, r => r.Contains("option list was never captured"));
		}

		private static ConversationGraph.Harvested Gated(ConversationGraph.Harvested walk, uint questId, QuestState state)
		{
			return walk with { Walk = walk.Walk with { ConditionQuestId = questId, ConditionQuestState = state } };
		}

		private static ConversationGraph.Harvested Under(ConversationGraph.Harvested walk, string parentContentKey, uint optionId)
		{
			return walk with { Walk = walk.Walk with { ParentContentKey = parentContentKey, OptionId = optionId } };
		}

		private static ConversationGraph.Harvested Walk(string answerPath, params ConversationPacket[] packets)
		{
			return WalkGranting(answerPath, 0, packets);
		}

		private static ConversationGraph.Harvested WalkGranting(string answerPath, uint grant, params ConversationPacket[] packets)
		{
			for (var i = 0; i < packets.Length; i++)
				packets[i].Sequence = i;

			return new ConversationGraph.Harvested(new ConversationWalk(TypeId, answerPath, 0x4DEE9, [], Npc, NpcTemplate, GrantQuestId: grant), [.. packets]);
		}

		// The NPC's spoken line: one screen, category 50002.
		private static ConversationPacket Line(uint screenId)
		{
			return Packet(ConversationSubOpcode.KnubotAppendText, 50002, screenId);
		}

		// The option buttons, in the order the client draws them: category 50001, `0` being the universal "Goodbye".
		private static ConversationPacket Options(params uint[] optionIds)
		{
			return Packet(ConversationSubOpcode.KnubotAnswerList, 50001, optionIds);
		}

		private static ConversationPacket Packet(ConversationSubOpcode subOpcode, uint category, params uint[] screenIds)
		{
			var packet = new ConversationPacket
			{
				SubOpcode = subOpcode
			};
			for (var i = 0; i < screenIds.Length; i++)
			{
				packet.Screens.Add(new ConversationScreen
				{
					Sequence = i,
					Category = category,
					ScreenId = screenIds[i]
				});
			}

			return packet;
		}
	}
}
