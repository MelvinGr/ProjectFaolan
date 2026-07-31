using System;
using Faolan.Core.Network.Codecs.Text;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for ChannelUnsubscribe (0x003D) - the channel a client is dropped from (#562).
	//
	// Oracles are captured bodies, chosen for what they PROVE rather than for coverage: each one is a channel the same
	// capture is seen subscribing to with a 0x003C, which is what identifies 0x003D as the subscription's twin. If the
	// body were anything but the (type, id) pair, these would not line up.
	public class ChannelUnsubscribeCodecTests
	{
		// `NecroL80_..._actions-describe`: subscribed to ~Playfield on 90/000007f8, then dropped from 90/000007f8.
		private const string PlayfieldBody = "90000007f8";

		// `NecroL80_..._gameplay_chat-an`: a ~Team subscription on 82/000f08db, then the drop on the same channel.
		private const string TeamBody = "82000f08db";

		// `login_2-clients_group-trade-friend`: the "leave goup now" post rides channel 82/0051dd9a and the drop
		// follows on it - the clearest evidence of what the message MEANS, not just of its shape.
		private const string LeftTeamBody = "820051dd9a";

		[Theory]
		[InlineData(PlayfieldBody, 0x90, 0x000007f8u)]
		[InlineData(TeamBody, 0x82, 0x000f08dbu)]
		[InlineData(LeftTeamBody, 0x82, 0x0051dd9au)]
		public void Parse_ReadsTheCapturedChannel(string hex, byte type, uint id)
		{
			var parsed = ChannelUnsubscribeCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(type, parsed.Type);
			Assert.Equal(id, parsed.Id);
		}

		[Theory]
		[InlineData(PlayfieldBody)]
		[InlineData(TeamBody)]
		[InlineData(LeftTeamBody)]
		public void Build_ReproducesTheCapturedBytes(string hex)
		{
			var captured = Convert.FromHexString(hex);

			Assert.Equal(captured, ChannelUnsubscribeCodec.Build(ChannelUnsubscribeCodec.Parse(captured)));
		}

		// The pair that makes the identification: a subscription and an unsubscribe for one channel open with the very
		// same (type, id) bytes - the subscription just carries a name and flags after them.
		[Fact]
		public void Body_IsTheSubscriptionsLeadingChannelPair()
		{
			var unsubscribe = ChannelUnsubscribeCodec.Build(0x90, 0x000007f8);
			var subscription = ChannelSubscriptionCodec.Build(0x90, 0x000007f8, "~Playfield", 0x00008044);

			Assert.Equal(unsubscribe, subscription[..unsubscribe.Length]);
		}
	}
}
