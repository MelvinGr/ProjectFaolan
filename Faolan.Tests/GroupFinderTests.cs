using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.GroupFinder;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The group-finder signup flow (#511), gated against the one capture that walks it end to end
	// (NecroL80_..._chat-annotated-4, t=1061..1104): every codec round-trips the captured body byte-exact, and the
	// service reproduces the captured decision - the refusal at the quest cap, the accept below it, and the
	// solo confirm forming the private team that owns the entered instance (#512).
	public class GroupFinderTests : IDisposable
	{
		private const uint Queuer = 0x01000001;

		// The captured bodies, hex-for-hex (tiny structural payloads; the flow's provenance is the capture named
		// above and the annotated trace in docs/subsystems/playfield-instance-ownership.md).
		private const string QueueBody = "00000fc40000000000000020000000000001";
		private const string NeededRoleBody = "0000002600000001";
		private const string SignupFailedBody =
			"00653c72656d6f7465666f726d61742069643d22323138333738393633222063617465676f72793d22313030333522206b65793d224b2e21532c4c2a6c4d6d455a33592d346f68765a22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e";
		private const string SignupSuccessBody = "00000fc400000000000000200000000000";
		private const string ReadyCheckBody = "0000002600000000";
		private const string ReadyCheckStatusBody = "000000010000000000000001";
		private const string LookingForGroupStatusBody = "000000";
		// #882: the rotating-activity pair. Byte-identical in all five captured exchanges across two captures, which
		// is why the field split comes from the client's own recovered field list rather than from variance.
		private const string RotatingActivityRequestBody = "00000001";
		private const string RotatingActivityChildInfoBody = "000000010000000200000003";

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		private static byte[] Bytes(string hex)
		{
			return Convert.FromHexString(hex);
		}

		// --- Codecs: the captured body parses, and re-building the parse is the identical bytes -------------------

		// ⭐ DispatchGroupFinderRemoved carries TWO tokens, and the second one is the answer this flow shipped without:
		// #882's captures answer all three `LeaveGroupFinder` withdrawals with `GroupFinderLeaderDequeued`. Both
		// builders are compared against the captured bytes whole - the remoteformat spelling, its LDB key's entity
		// escapes and the trailing `silent` byte included.
		[Fact]
		public void GroupFinderRemovedCodec_BuildsBothCapturedReasonsByteExact()
		{
			var dequeued = Bytes(
				"007F3C72656D6F7465666F726D617420746F6B656E3D2247726F757046696E6465724C65616465724465717565756564222063617465676F72793D"
				+ "2231303022206B65793D2238673075492671756F743B28312E692A262339323B302337615471783322206B6E75626F743D22302220203E3C2F72"
				+ "656D6F7465666F726D61743E00");
			var leftPlayfield = Bytes(
				"008A3C72656D6F7465666F726D617420746F6B656E3D2247726F757046696E6465724368617261637465724C656674506C61796669656C64222063"
				+ "617465676F72793D2231303022206B65793D226E4B5926616D703B2B684C5E4937262333363B4D40504F607A262333393B593022206B6E75626F"
				+ "743D22302220203E3C2F72656D6F7465666F726D61743E01");

			Assert.Equal(dequeued, GroupFinderRemovedCodec.Build(GroupFinderRemovalReasons.LeaderDequeued()));
			Assert.Equal(leftPlayfield, GroupFinderRemovedCodec.Build(GroupFinderRemovalReasons.CharacterLeftPlayfield(true)));

			var parsed = GroupFinderRemovedCodec.Parse(dequeued);
			Assert.Contains(GroupFinderRemovedCodec.LeaderDequeued, parsed.Reason, StringComparison.Ordinal);
			Assert.False(parsed.Silent); // 0 in all three captured dequeues
			Assert.Equal(dequeued, GroupFinderRemovedCodec.Build(parsed));

			// The login push is the only captured frame with silent = 1; the same builder writes the 0 form retail
			// sends on an instance exit, so the flag is a field rather than part of the spelling.
			Assert.Equal(leftPlayfield[..^1], GroupFinderRemovedCodec.Build(GroupFinderRemovalReasons.CharacterLeftPlayfield(false))[..^1]);
			Assert.Equal(0, GroupFinderRemovedCodec.Build(GroupFinderRemovalReasons.CharacterLeftPlayfield(false))[^1]);
		}

		// c2s RequestRotatingActivityChildInfo is a bare u32 (activity 1 in every captured request), and its reply is
		// three u32s the client names `activityId, currentChildIndex, nextChildIndex`. 12 bytes for three fields is
		// three u32s and can be nothing else - the arithmetic is what carves a body whose every frame is identical.
		[Fact]
		public void RotatingActivityChildInfoCodec_RoundTripsTheCapturedReply()
		{
			var captured = Bytes(RotatingActivityChildInfoBody);

			var parsed = RotatingActivityChildInfoCodec.Parse(captured);

			Assert.Equal(1u, parsed.ActivityId); // the activity every captured request asks about
			Assert.Equal(2u, parsed.CurrentChildIndex);
			Assert.Equal(3u, parsed.NextChildIndex);
			Assert.Equal(captured, RotatingActivityChildInfoCodec.Build(parsed));
			Assert.Equal(captured, RotatingActivityChildInfoCodec.Build(1, 2, 3));

			// The request is the activity id alone, and the reply echoes it into field 0.
			Assert.Equal(4, Bytes(RotatingActivityRequestBody).Length);
			Assert.Equal(parsed.ActivityId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Bytes(RotatingActivityRequestBody)));
		}

		[Fact]
		public void Queue_CapturedBody_RoundTrips_AndNamesActivity38()
		{
			var value = GroupFinderQueueCodec.Parse(Bytes(QueueBody));

			// The three-word vector is a bitmask over the activity catalog: the single set bit is activity 38, the
			// id RequestNeededRole and the ready check both carry in the same flow.
			Assert.Equal([0u, 0x20u, 0u], value.ActivityWords);
			Assert.Equal([38u], value.ActivityIds.ToArray());
			Assert.False(value.WantsLeader);
			Assert.True(value.PrivateTeam); // the annotated "entered as private team"

			Assert.Equal(Bytes(QueueBody), GroupFinderQueueCodec.Build(value));
		}

		[Fact]
		public void NeededRole_CapturedBody_RoundTrips()
		{
			var value = GroupFinderNeededRoleCodec.Parse(Bytes(NeededRoleBody));

			Assert.Equal(38u, value.ActivityId);
			Assert.Equal(1u, value.NeededRoles);
			Assert.Equal(Bytes(NeededRoleBody), GroupFinderNeededRoleCodec.Build(value));
		}

		[Fact]
		public void SignupFailed_CapturedBody_RoundTrips_AndIsTheTooManyQuestsConstant()
		{
			var value = GroupFinderSignupFailedCodec.Parse(Bytes(SignupFailedBody));

			Assert.Equal(GroupFinderSignupFailedCodec.TooManyQuests, value.Reason);
			Assert.Equal(Bytes(SignupFailedBody), GroupFinderSignupFailedCodec.Build(value));
		}

		[Fact]
		public void SignupSuccess_CapturedBody_RoundTrips()
		{
			var value = GroupFinderSignupSuccessCodec.Parse(Bytes(SignupSuccessBody));

			Assert.Equal([0u, 0x20u, 0u], value.ActivityWords);
			Assert.False(value.Flag);
			Assert.Equal(Bytes(SignupSuccessBody), GroupFinderSignupSuccessCodec.Build(value));
		}

		[Fact]
		public void ReadyCheck_CapturedBody_RoundTrips()
		{
			var value = GroupFinderReadyCheckCodec.Parse(Bytes(ReadyCheckBody));

			Assert.Equal(38u, value.ActivityId);
			Assert.Equal(0u, value.Word);
			Assert.Equal(Bytes(ReadyCheckBody), GroupFinderReadyCheckCodec.Build(value));
		}

		[Fact]
		public void ReadyCheckStatus_CapturedBody_RoundTrips()
		{
			var value = GroupFinderReadyCheckStatusCodec.Parse(Bytes(ReadyCheckStatusBody));

			Assert.Equal((1u, 0u, 1u), (value.Ready, value.NotReady, value.Expected));
			Assert.Equal(Bytes(ReadyCheckStatusBody), GroupFinderReadyCheckStatusCodec.Build(value));
		}

		[Fact]
		public void LookingForTeamStatus_CapturedBody_RoundTrips()
		{
			var value = LookingForGroupStatusCodec.Parse(Bytes(LookingForGroupStatusBody));

			Assert.False(value.Looking);
			Assert.Equal("", value.Message);
			Assert.Equal(Bytes(LookingForGroupStatusBody), LookingForGroupStatusCodec.Build(value));
		}

		// --- The service: the captured decision ------------------------------------------------------------------

		[Fact]
		public async Task QueueAtTheQuestCap_IsRefusedWithTheCapturedRemoteformat()
		{
			var h = await Harness(heldQuests: GroupFinderService.QuestLogCap);

			await h.Service.HandleQueue(h.Client, GroupFinderQueueCodec.Parse(Bytes(QueueBody)));

			// The refusal and nothing else - no role select, so the client shows the message and stays put.
			Assert.Contains(SignupFailedBody, h.Client.SentHex());
			Assert.Empty(h.Teleports);
		}

		[Fact]
		public async Task QueueBelowTheCap_OpensTheRoleSelect_AndTheConfirmEntersTheInstanceAsAPrivateTeam()
		{
			// One below the cap - the capture's own transition: deleting a single quest turned the refusal into an
			// accept, so the gate is >= 45, not > 45.
			var h = await Harness(heldQuests: GroupFinderService.QuestLogCap - 1);
			var request = GroupFinderQueueCodec.Parse(Bytes(QueueBody));

			await h.Service.HandleQueue(h.Client, request);
			Assert.DoesNotContain(SignupFailedBody, h.Client.SentHex());

			await h.Service.HandleSelectRoles(h.Client, 0x4);
			Assert.Contains(SignupSuccessBody, h.Client.SentHex());
			Assert.Contains(ReadyCheckBody, h.Client.SentHex());

			await h.Service.HandleReady(h.Client, ready: true, isTimeout: false);
			Assert.Contains(ReadyCheckStatusBody, h.Client.SentHex());
			Assert.Contains(LookingForGroupStatusBody, h.Client.SentHex());

			// The private team formed (#512: the entered instance is keyed to this id) and the world tier was told
			// to enter the captured activity's playfield at the captured arrival.
			Assert.NotNull(h.Teams.TeamOf(Queuer));
			var teleport = Assert.Single(h.Teleports);
			Assert.Equal(1092u, teleport.MapId);
			Assert.Equal(new System.Numerics.Vector3(112.0f, 70.0f, 39.0f), teleport.Position);
		}

		[Fact]
		public async Task ReadyWithoutASignup_DoesNothing()
		{
			var h = await Harness(heldQuests: 0);

			await h.Service.HandleReady(h.Client, ready: true, isTimeout: false);

			Assert.Null(h.Teams.TeamOf(Queuer));
			Assert.Empty(h.Teleports);
		}

		private sealed record Harnessed(GroupFinderService Service, TeamService Teams, RecordingNetworkClient Client, List<(uint CharacterId, uint MapId, System.Numerics.Vector3? Position)> Teleports);

		private async Task<Harnessed> Harness(int heldQuests)
		{
			var (options, path) = TestDb.NewSeededDatabase(Queuer);
			_databases.Add(path);

			var database = new DatabaseRepository(new TestDb.ContextFactory(options));
			await using (var ctx = new DatabaseContext(options))
			{
				// The gate counts ACTIVE journal rows; the definitions behind them are irrelevant to it.
				for (var i = 0; i < heldQuests; i++)
					ctx.CharacterQuests.Add(new CharacterQuest { CharacterId = Queuer, QuestId = 80000u + (uint)i, State = QuestState.Active });
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var client = new RecordingNetworkClient(Queuer);
			var dispatcher = new TeleportRecordingDispatcher();
			var teams = new TeamService(NullLogger.Instance, dispatcher, new NoOpChatDispatcher(), id => id == Queuer
				? client
				: null);
			var service = new GroupFinderService(NullLogger.Instance, database, teams, dispatcher);

			return new Harnessed(service, teams, client, dispatcher.Teleports);
		}

		private sealed class TeleportRecordingDispatcher : IGameServerDispatcher
		{
			public List<(uint CharacterId, uint MapId, System.Numerics.Vector3? Position)> Teleports { get; } = [];

			public Task<bool> TeleportCharacter(uint characterId, uint mapId, System.Numerics.Vector3? position = null)
			{
				Teleports.Add((characterId, mapId, position));
				return Task.FromResult(true);
			}

			public Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined) => Task.FromResult(true);
			public Task<bool> CreditCharacterCash(uint characterId, ulong amountTin) => Task.FromResult(false);
			public bool DisconnectCharacter(uint characterId) => false;
			public CharacterLiveState? GetCharacterLiveState(uint characterId) => null;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId) => [];
			public Task<bool> RunGmCommand(uint characterId, string commandText) => Task.FromResult(false);
			public Task<bool> SendGuildTag(uint characterId, string guildName) => Task.FromResult(false);
			public void Register(IGameServerProvider provider) { }
			public void Unregister(IGameServerProvider provider) { }
		}

		private sealed class NoOpChatDispatcher : IChatServerDispatcher
		{
			public Task RefreshTeamChannel(uint characterId, uint? teamId) => Task.CompletedTask;
			public Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
			public Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public Task<int> SendClaudeChannelMessage(ChatMarkup message) => Task.FromResult(0);
			public Task RefreshPlayfieldChannel(uint characterId, uint mapId) => Task.CompletedTask;
			public Task RefreshGuildChannel(uint characterId, uint? guildId) => Task.CompletedTask;
			public Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message) => Task.FromResult(false);
			public Task SendSystemMessage(uint characterId, ChatMarkup message) => Task.CompletedTask;
			public Task SystemBroadcast(ChatMarkup message) => Task.CompletedTask;
			public void Register(IChatServerProvider provider) { }
			public void Unregister(IChatServerProvider provider) { }
		}
	}
}
