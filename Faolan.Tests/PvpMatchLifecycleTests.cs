using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Pvp;
using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The end of a PvP minigame match: who may hit whom, what a kill scores, and what the two
	// client-recovered end messages carry.
	//
	// None of this is captured - no captured session ever finishes a minigame - so what is gated here is the
	// state machine, not bytes. The messages' own layouts are pinned by the client instead
	// (docs/re/minigame-massivepvp-messages.md); the one thing this file does pin about the wire is the
	// `MinigameResult` word, whose three values come off the client's own results-page branch.
	public class PvpMatchLifecycleTests : IDisposable
	{
		private const uint Blue = 1;
		private const uint Red = 2;
		private const uint MinigameMap = 5006; // The Call of Jhebbal Sag - the pair the captured signups used most
		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				try
				{
					File.Delete(path);
				}
				catch (IOException)
				{
					// a throwaway file DB the OS still holds; the temp dir sweeps it
				}
		}

		private sealed record Harnessed(PvpMatchService Matches, RecordingGameServerDispatcher World, uint[] Members);

		// Two queued players force-formed into one match: the formation locks them to alternating sides 1 and 2,
		// which is exactly the opponent pair the PvP gate is about.
		private Harnessed Harness(params uint[] members)
		{
			var (options, path) = TestDb.NewSeededDatabase(members[0]);
			_databases.Add(path);
			var database = new DatabaseRepository(new TestDb.ContextFactory(options));

			var world = new RecordingGameServerDispatcher();
			var clients = new Dictionary<uint, RecordingNetworkClient>();
			foreach (var id in members)
			{
				var client = new RecordingNetworkClient(id);
				client.Session.Character!.Name = $"C{id}";
				client.Session.Character.Class = ConanClass.Barbarian;
				clients[id] = client;
			}

			var connections = new ConnectionRegistry();
			foreach (var client in clients.Values)
				connections.Add(client);

			var teams = new TeamService(NullLogger<CsPlayerAgentListener>.Instance, world, new RecordingChatServerDispatcher(), connections);
			var matches = new PvpMatchService(NullLogger<CsPlayerAgentListener>.Instance, teams, connections, world, database, new WorkLoop("CS tier", NullLogger.Instance));

			var signup = new PvpMatchMakingSignup(MinigameMap, 4, false, 0);
			var queued = members.Select(id => (CharacterId: id, Signup: signup)).ToList();
			matches.ForceFormFromQueue(queued, _ => { }).GetAwaiter().GetResult();

			return new Harnessed(matches, world, members);
		}

		[Fact]
		public void PvpGate_OpensOnlyAcrossTheSideLineOfOneStartedMatch()
		{
			var h = Harness(1, 2, 3);

			// 1 and 3 are both on side Blue (the formation alternates in queue order), 2 is Red.
			Assert.True(h.Matches.ArePvpMatchOpponents(1, 2));
			Assert.True(h.Matches.ArePvpMatchOpponents(2, 1));

			Assert.False(h.Matches.ArePvpMatchOpponents(1, 3)); // same side
			Assert.False(h.Matches.ArePvpMatchOpponents(1, 1)); // themselves
			Assert.False(h.Matches.ArePvpMatchOpponents(1, 99)); // not in the match at all
			Assert.False(h.Matches.ArePvpMatchOpponents(99, 1)); // ...from either direction
		}

		[Fact]
		public async Task AKill_ScoresForTheKillersSideOnly()
		{
			var h = Harness(1, 2);

			await h.Matches.CreditKill(1, 2);
			// A kill of a team-mate scores nothing - the gate refuses it, so nothing reaches the board.
			await h.Matches.CreditKill(2, 2);

			// Nothing has ended yet: one kill is a long way off the ruleset's score limit.
			Assert.Empty(h.World.Ended);
		}

		[Fact]
		public async Task ReachingTheScoreLimit_EndsTheMatchAndTellsEachSideItsOwnResult()
		{
			var h = Harness(1, 2);

			// The queue-formed ruleset's score limit is 100; the 100th kill is what ends it.
			for (var i = 0; i < 120; i++)
				await h.Matches.CreditKill(1, 2);

			// Exactly one end, to each member, naming the winner's side...
			Assert.Equal(2, h.World.Ended.Count);
			Assert.All(h.World.Ended, e => Assert.Equal(Blue, e.WinningSide));

			// ...and each member's own result: the client renders MatchWon/MatchLost from this word alone.
			Assert.Equal(MinigameResult.Won, h.World.Ended.Single(e => e.Character == 1).Result);
			Assert.Equal(MinigameResult.Lost, h.World.Ended.Single(e => e.Character == 2).Result);

			// And the match is finished: the gate shuts, so a swing landing after the last kill cannot score.
			Assert.False(h.Matches.ArePvpMatchOpponents(1, 2));
		}

		[Fact]
		public async Task AForcedStop_TakesTheClientsAbortPathRatherThanTheResultsPage()
		{
			var h = Harness(1, 2);

			var outcome = await h.Matches.ForceStopMatch(1, "");

			Assert.Contains("force-stopped", outcome);
			Assert.Equal(2, h.World.Stopped.Count);
			Assert.Empty(h.World.Ended);
		}

		// The clock end - the one a match reaches on its own. A custom match carries the client's own ruleset, so a
		// one-second time limit is a legitimate one to build; the point is that the end fires off the ruleset without
		// anybody asking, and that with no score on either side it is a draw for everyone (winning side 0, which is
		// the value the client's own reset leaves in the field and which it renders as MatchTie).
		[Fact]
		public async Task TheTimeLimitEndsTheMatchByItself_AndAScorelessMatchIsATieForEveryone()
		{
			var h = Harness(1, 2);
			var world = new RecordingGameServerDispatcher();
			var clients = new Dictionary<uint, RecordingNetworkClient>();
			foreach (var id in new uint[] { 10, 11 })
			{
				var client = new RecordingNetworkClient(id);
				client.Session.Character!.Name = $"C{id}";
				client.Session.Character.Class = ConanClass.Barbarian;
				clients[id] = client;
			}

			var connections = new ConnectionRegistry();
			foreach (var client in clients.Values)
				connections.Add(client);
			var (options, path) = TestDb.NewSeededDatabase(10);
			_databases.Add(path);
			var matches = new PvpMatchService(NullLogger<CsPlayerAgentListener>.Instance, new TeamService(NullLogger<CsPlayerAgentListener>.Instance, world, new RecordingChatServerDispatcher(), connections), connections, world,
				new DatabaseRepository(new TestDb.ContextFactory(options)), new WorkLoop("CS tier", NullLogger.Instance));

			await matches.HandleCreateMatch(clients[10], new PvpMatchRules(4, MinigameMap, 2, 1, 1, "", TimeLimitSeconds: 1, ScoreLimit: 100, 0, false, false, 0));
			await matches.HandleJoinMatch(clients[11], 1, bringTeam: false, password: null);
			await matches.HandleSetSide(clients[10], Blue);
			await matches.HandleSetSide(clients[11], Red);
			await matches.HandleStartMatch(clients[10]);

			await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

			Assert.Equal(2, world.Ended.Count);
			Assert.All(world.Ended, e => Assert.Equal(0u, e.WinningSide));
			Assert.All(world.Ended, e => Assert.Equal(MinigameResult.Tie, e.Result));
		}

		[Fact]
		public async Task ForceStop_CarriesTheReasonLineToEveryMember()
		{
			var h = Harness(1, 2);

			await h.Matches.ForceStopMatch(2, "The arena is closing.");

			Assert.Equal(2, h.World.Stopped.Count);
			Assert.All(h.World.Stopped, s => Assert.Equal("The arena is closing.", s.Message));
			Assert.Equal([1u, 2u], h.World.Stopped.Select(s => s.Character).OrderBy(id => id).ToArray());
		}

		[Fact]
		public async Task AMatchThatHasAlreadyEnded_CannotBeStoppedTwice()
		{
			var h = Harness(1, 2);

			await h.Matches.ForceStopMatch(1, "first");
			var second = await h.Matches.ForceStopMatch(1, "second");

			Assert.Contains("already ended", second);
			Assert.Equal(2, h.World.Stopped.Count);
		}
	}
}
