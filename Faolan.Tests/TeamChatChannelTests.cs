using System;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// The ~Team chat channel (#668): the 0x003C that makes the team chat tab appear, and the 0x003D that retires it.
	//
	// The guild twin (GuildChatChannelTests) has to infer its unsubscribe; this one does not. `login_2-clients_group-
	// trade-friend` subscribes every member on the accept and unsubscribes them all on the dissolve, three teams in a
	// row, so both directions are captured.
	public class TeamChatChannelTests
	{
		// `login_2-clients_group-trade-friend` fr701/702: the first of that capture's three teams, 0x0051DD9A - the id
		// DispatchYouJoinedTeam names three frames earlier as an ID32 of type 0xDEA9 (`0000dea9 0051dd9a`).
		private const string FirstTeamBody = "820051dd9a00057e5465616d000080040000";

		// The same capture's second team, 0x0051DD9B: consecutive ids, i.e. the channel tracks the team instance and
		// is not a per-account or per-session constant.
		private const string SecondTeamBody = "820051dd9b00057e5465616d000080040000";

		// `NecroL80_..._gameplay_chat-annotated` fr11949: a fourth team from a different session entirely, 0x000F08DB.
		private const string OtherSessionBody = "82000f08db00057e5465616d000080040000";

		[Theory]
		[InlineData(FirstTeamBody, 0x0051dd9au)]
		[InlineData(SecondTeamBody, 0x0051dd9bu)]
		[InlineData(OtherSessionBody, 0x000f08dbu)]
		public void Parse_ReadsTheTeamIdAsTheChannelId(string hex, uint teamId)
		{
			var parsed = ChannelSubscriptionCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(0x82, parsed.Type);
			Assert.Equal(teamId, parsed.Id);
			Assert.Equal("~Team", parsed.Name);

			// The per-membership flag, shared with ~Guild - not the 0x00008044 of the standing channels.
			Assert.Equal(0x00008004u, parsed.Flag);
		}

		[Theory]
		[InlineData(FirstTeamBody, 0x0051dd9au)]
		[InlineData(SecondTeamBody, 0x0051dd9bu)]
		[InlineData(OtherSessionBody, 0x000f08dbu)]
		public void Build_FromTheTeamId_ReproducesTheCapturedBytes(string hex, uint teamId)
		{
			Assert.Equal(Convert.FromHexString(hex), ChannelSubscriptionCodec.Build(ChatChannels.Team.Type, teamId, ChatChannels.Team.Name, ChatChannels.Team.Flag));
		}

		// The dissolve, captured on the same channel (fr923/926) - unlike the guild leave, this one needs no inference.
		[Fact]
		public void TheDissolvesUnsubscribe_IsTheSubscriptionsLeadingChannelPair()
		{
			var subscription = Convert.FromHexString(FirstTeamBody);
			var unsubscribe = ChannelUnsubscribeCodec.Build(ChatChannels.Team.Type, 0x0051dd9a);

			Assert.Equal(unsubscribe, subscription[..unsubscribe.Length]);
		}
	}
}
