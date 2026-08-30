using System.Linq;
using Faolan.Core.Network.Server.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// The Claude chat-channel log. #97: the inbound MODE (whisper vs channel) is carried structurally on the message
	// (ViaWhisper) - NOT as a "(whisper) " text prefix - so a reader can answer in the mode it was addressed in.
	// `read_claude_channel` surfaces the flag and `send_claude_whisper` is the private half of the reply.
	//
	// (The stdio channel bridge that used to PUSH these into a Claude Code session is gone, and with it
	// ChannelBridgeBacklogTests - its backlog-cursor floor was the only thing that file also covered.)
	public class ClaudeChannelTests
	{
		[Fact]
		public void Append_ChannelPost_IsNotViaWhisper()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "hello channel", false);

			Assert.False(entry.ViaWhisper);
			Assert.Equal("hello channel", entry.Message); // no mode prefix baked into the text
		}

		[Fact]
		public void Append_DefaultsToChannelMode()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "hi", false);

			Assert.False(entry.ViaWhisper);
		}

		[Fact]
		public void Append_Whisper_IsViaWhisper_WithCleanText()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "psst", false, true);

			Assert.True(entry.ViaWhisper);
			Assert.Equal("psst", entry.Message); // the mode is the flag, not a "(whisper) " prefix
		}

		[Fact]
		public void ReadSince_PreservesModePerMessage()
		{
			var channel = new ClaudeChannel();
			channel.Append(0x1, "A", "on channel", false);
			channel.Append(0x2, "B", "in private", false, true);

			var messages = channel.ReadSince(0);
			Assert.Equal(2, messages.Count);
			Assert.False(messages.Single(m => m.SenderName == "A").ViaWhisper);
			Assert.True(messages.Single(m => m.SenderName == "B").ViaWhisper);
		}
	}
}
