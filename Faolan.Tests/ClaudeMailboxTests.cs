using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The "Claude" mailbox (#281): an in-game mail addressed to the Claude chat channel's name. There is no character
	// behind it - Mail keys its recipient by name + realm - so the mailbox is the name, and its arrival notification
	// is an entry on the same ClaudeChannel log the chat bridge polls (whisper mode, like a /tell).
	//
	// Two gates: with the bridge's log handed in, a mail to "Claude" is written and surfaced; without it (MCP off),
	// the same mail is refused exactly like any unknown recipient, because nothing would ever read it.
	public class ClaudeMailboxTests
	{
		private const uint Sender = 7001;

		[Theory]
		[InlineData("Claude", true)]
		[InlineData("claude", true)]
		[InlineData(" CLAUDE ", true)]
		[InlineData("Claudette", false)]
		[InlineData("", false)]
		[InlineData(null, false)]
		public void OnlyTheChannelsOwnName_IsTheMailbox(string? name, bool expected)
		{
			Assert.Equal(expected, ClaudeMailbox.IsClaude(name));
		}

		[Fact]
		public async Task AMailToClaude_IsWrittenUnderThatName_AndSurfacedOnTheChannelLog()
		{
			var (options, path) = TestDb.NewSeededDatabase(Sender);
			try
			{
				var database = new DatabaseRepository(new TestDb.ContextFactory(options));
				var channel = new ClaudeChannel();
				var (client, service) = await Compose(database, channel);

				await service.Handle(client, SendMailTo("Claude"), SendMailBody("Claude", "hello there", 250));

				var mail = Assert.Single(await database.GetMailsForRecipient(ClaudeMailbox.Name, client.Session.Character.RealmId));
				Assert.Equal(client.Session.Character.Name, mail.SenderName);
				Assert.Equal("hello there", mail.BodyXml);
				Assert.Equal(250UL, mail.Money);

				// The bridge's view: one whisper-mode entry from the sender, naming the mail and what rides on it.
				var entry = Assert.Single(channel.ReadSince(0));
				Assert.Equal(Sender, entry.SenderId);
				Assert.False(entry.FromClaude);
				Assert.True(entry.ViaWhisper);
				Assert.Contains($"0x{mail.MailId:X16}", entry.Message);
				Assert.Contains("hello there", entry.Message);
				Assert.Contains("250 tin", entry.Message);

				// ...and the sender got the ordinary success result.
				Assert.NotEmpty(client.Sent);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task WithoutTheBridge_AMailToClaude_IsRefusedLikeAnyUnknownRecipient()
		{
			var (options, path) = TestDb.NewSeededDatabase(Sender);
			try
			{
				var database = new DatabaseRepository(new TestDb.ContextFactory(options));
				var (client, service) = await Compose(database, null);

				await service.Handle(client, SendMailTo("Claude"), SendMailBody("Claude", "anyone home?", 0));

				Assert.Empty(await database.GetMailsForRecipient(ClaudeMailbox.Name, client.Session.Character.RealmId));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private static async Task<(RecordingNetworkClient Client, TradepostService Service)> Compose(DatabaseRepository database, ClaudeChannel? channel)
		{
			var client = new RecordingNetworkClient(Sender);
			client.Session.Character = (await database.GetCharacter(Sender))!;
			var service = new TradepostService(database, NullLogger.Instance, new NoOpEconomyOutbound(), new NoCsTier(), channel);
			return (client, service);
		}

		// The c2s SendMail body as the client writes it: recipient, text, money, then the count-framed attachment list.
		private static FaolanStream SendMailBody(string recipient, string text, ulong money)
		{
			using var s = new FaolanStream();
			s.WriteString(recipient).WriteString(text).WriteUInt64(money).WriteUInt32(ListFrame.ForCount(0));
			return new FaolanStream(s.ToArray());
		}

		private static Rpc SendMailTo(string recipient)
		{
			return new Rpc(new ProtoEndpoint { Type = 0, Instance = Sender }, new ProtoEndpoint { Type = 0, Instance = 1 }, TradepostAgentMethods.SendMail, s => s.WriteString(recipient));
		}

		private sealed class NoCsTier : ICsPlayerAgentProvider
		{
			public Task<bool> LeaveTeam(uint characterId) => Task.FromResult(false);
			public Task<bool> KickFromTeam(uint leaderId, uint targetId) => Task.FromResult(false);
			public Task<bool> NotifyMailReceived(uint recipientId, ulong mailId) => Task.FromResult(false);
			public Task<bool> DepositToGuildBank(uint characterId, ulong amountTin) => Task.FromResult(false);
			public IReadOnlyList<uint> TeamMembersOf(uint characterId) => [];
			public uint? TeamIdOf(uint characterId) => null;
		}
	}
}
