using System;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// The ~Guild chat channel (#663): the 0x003C subscription that makes the guild chat TAB appear.
	//
	// The point of these is not that a subscription encodes - ChannelSubscriptionCodec is already gated - but that the
	// channel's IDENTITY is the guild id, which is what let the server construct one at all. Two captures state it
	// independently, one on a create and one on a login, and in both the id is the same word the guild's own header
	// carries.
	public class GuildChatChannelTests
	{
		// `DemoL80_..._login_create-guild` frame 369: the subscription retail sends three frames after the
		// DispatchGuildCreated (0x2044) for the guild it just made, 0x00086C47.
		private const string CreatedGuildBody = "0300086c4700067e4775696c64000080040000";

		// `Hoxxling_..._login` frame 114: the same channel in the LOGIN burst of a character already in guild
		// 0x0007585B ("Sepulchra Invictus"), whose 0x203F full update opens `03 0007585b`.
		private const string LoginGuildBody = "030007585b00067e4775696c64000080040000";

		[Theory]
		[InlineData(CreatedGuildBody, 0x00086c47u)]
		[InlineData(LoginGuildBody, 0x0007585bu)]
		public void Parse_ReadsTheGuildIdAsTheChannelId(string hex, uint guildId)
		{
			var parsed = ChannelSubscriptionCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(0x03, parsed.Type);
			Assert.Equal(guildId, parsed.Id);
			Assert.Equal("~Guild", parsed.Name);

			// NOT the 0x00008044 the standing channels (~Global / ~LFG / ~Playfield / ~NewbieHelp) carry - the same
			// 0x00008004 the captured ~Team subscription uses, i.e. the per-membership channels share their own flag.
			Assert.Equal(0x00008004u, parsed.Flag);
		}

		[Theory]
		[InlineData(CreatedGuildBody, 0x00086c47u)]
		[InlineData(LoginGuildBody, 0x0007585bu)]
		public void Build_FromTheGuildId_ReproducesTheCapturedBytes(string hex, uint guildId)
		{
			Assert.Equal(Convert.FromHexString(hex), ChannelSubscriptionCodec.Build(ChatChannels.Guild.Type, guildId, ChatChannels.Guild.Name, ChatChannels.Guild.Flag));
		}

		// Leaving is the subscription's twin on the same channel - the shape the corpus shows retiring a ~Team and a
		// ~Playfield. The guild LEAVE itself is uncaptured (nothing in the corpus ever leaves a guild), so this pins
		// what we send, not what retail was seen sending. [V0: retail's 0x003D on a guild leave/kick]
		[Fact]
		public void Unsubscribe_IsTheSubscriptionsLeadingChannelPair()
		{
			var subscription = Convert.FromHexString(CreatedGuildBody);
			var unsubscribe = ChannelUnsubscribeCodec.Build(ChatChannels.Guild.Type, 0x00086c47);

			Assert.Equal(unsubscribe, subscription[..unsubscribe.Length]);
		}
	}
}
