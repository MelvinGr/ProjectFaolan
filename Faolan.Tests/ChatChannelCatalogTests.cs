using System;
using System.Linq;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// Every chat channel the server offers, gated against the bytes retail sent for it.
	//
	// The point is that `ChatChannels` is the one place a channel's (type, name, flag) is written down - the login
	// burst, the mid-session moves and the drops all read it - so re-deriving each captured body from its descriptor
	// is what makes that catalog the wire fact rather than a set of literals somebody re-typed.
	public class ChatChannelCatalogTests
	{
		// The standing channels, from the login bursts of `Hoxxling_..._login` (fr102/114) and
		// `DemoL80_..._login_create-guild` (fr134/139). Their ids are constants, so the whole body is pinned.
		private static readonly (string Name, string Hex, uint Id)[] Standing =
		[
			("~Trial", "040000232a00067e547269616c000082440000", 0x0000232Au),
			("~Global", "040000232800077e476c6f62616c000080440000", 0x00002328u),
			("~LFG", "040000232900047e4c4647000080440000", 0x00002329u),
			("~GlobalEnglish", "1100002335000e7e476c6f62616c456e676c697368000080440000", 0x00002335u),
			("~NewbieHelp", "87001e8480000b7e4e657762696548656c70000080440000", 0x001E8480u)
		];

		// The three whose id is what the player belongs to, so the id in each body is capture-specific:
		// ~Playfield on map 0x2B20 (DemoL80 fr139), ~Guild on the guild created in that same capture (fr369),
		// ~Team on the first of the three teams in `login_2-clients_group-trade-friend` (fr701).
		private static readonly (string Name, string Hex, uint Id)[] Membership =
		[
			("~Playfield", "9000002b20000a7e506c61796669656c64000080440000", 0x00002B20u),
			("~Guild", "0300086c4700067e4775696c64000080040000", 0x00086C47u),
			("~Team", "820051dd9a00057e5465616d000080040000", 0x0051DD9Au)
		];

		public static TheoryData<string, string, uint> StandingBodies => Rows(Standing);
		public static TheoryData<string, string, uint> MembershipBodies => Rows(Membership);

		private static TheoryData<string, string, uint> Rows((string Name, string Hex, uint Id)[] rows)
		{
			var data = new TheoryData<string, string, uint>();
			foreach (var (name, hex, id) in rows)
				data.Add(name, hex, id);

			return data;
		}

		private static ChatChannel ByName(string name)
		{
			return new[] { ChatChannels.Trial, ChatChannels.Global, ChatChannels.Lfg, ChatChannels.GlobalEnglish, ChatChannels.NewbieHelp, ChatChannels.Playfield, ChatChannels.Guild, ChatChannels.Team }
				.Single(c => c.Name == name);
		}

		[Theory]
		[MemberData(nameof(StandingBodies))]
		[MemberData(nameof(MembershipBodies))]
		public void TheDescriptor_RebuildsTheCapturedSubscription(string name, string hex, uint id)
		{
			var channel = ByName(name);

			Assert.Equal(Convert.FromHexString(hex), ChannelSubscriptionCodec.Build(channel.Type, id, channel.Name, channel.Flag));
		}

		// The standing half of the login burst is the catalog's own list, ids included - so the burst cannot quietly
		// gain or lose a channel, and the ids stay attached to the descriptors they belong to.
		[Fact]
		public void TheLoginBurst_IsExactlyTheCapturedStandingChannels()
		{
			var burst = ChatChannels.StandingAtLogin(false).ToList();

			Assert.Equal(Standing.Length, burst.Count);
			foreach (var (name, hex, id) in Standing)
			{
				var sent = burst.Single(b => b.Channel.Name == name);

				Assert.Equal(id, sent.Id);
				Assert.Equal(Convert.FromHexString(hex), ChannelSubscriptionCodec.Build(sent.Channel.Type, sent.Id, sent.Channel.Name, sent.Channel.Flag));
			}
		}

		// The assistant channel is ours, not retail's, so there is no captured body to hold it to - what is worth
		// gating is that it stays out of the way of a real one: its id sits far outside the retail range, and it
		// appears only when the MCP host that answers it is running.
		[Fact]
		public void TheAssistantChannel_IsOptIn_AndCannotCollideWithARetailChannel()
		{
			Assert.DoesNotContain(ChatChannels.StandingAtLogin(false), c => c.Channel == ChatChannels.Assistant);
			Assert.Contains(ChatChannels.StandingAtLogin(true), c => c.Channel == ChatChannels.Assistant && c.Id == ClaudeChannel.ChannelId);

			var retailIds = Standing.Concat(Membership).Select(r => r.Id).ToList();
			Assert.DoesNotContain(ClaudeChannel.ChannelId, retailIds);
			Assert.True(ClaudeChannel.ChannelId > retailIds.Max(), "the assistant channel id must sit above every retail channel id, so a new retail channel cannot grow into it");
		}
	}
}
