using System.Linq;
using Faolan.Core.Network.AgentServer;
using Xunit;

namespace Faolan.Tests
{
	// The Claude chat-bridge channel log. #97: the inbound MODE (whisper vs channel) is carried structurally on the
	// message (ViaWhisper) - NOT as a "(whisper) " text prefix - so the bridge can have Claude reply in the same mode.
	public class ClaudeChannelTests
	{
		[Fact]
		public void Append_ChannelPost_IsNotViaWhisper()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "hello channel", fromClaude: false, viaWhisper: false);

			Assert.False(entry.ViaWhisper);
			Assert.Equal("hello channel", entry.Message); // no mode prefix baked into the text
		}

		[Fact]
		public void Append_Whisper_IsViaWhisper_WithCleanText()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "psst", fromClaude: false, viaWhisper: true);

			Assert.True(entry.ViaWhisper);
			Assert.Equal("psst", entry.Message); // the mode is the flag, not a "(whisper) " prefix
		}

		[Fact]
		public void Append_DefaultsToChannelMode()
		{
			var channel = new ClaudeChannel();
			var entry = channel.Append(0x1234, "Hoxxling", "hi", fromClaude: false);

			Assert.False(entry.ViaWhisper);
		}

		[Fact]
		public void ReadSince_PreservesModePerMessage()
		{
			var channel = new ClaudeChannel();
			channel.Append(0x1, "A", "on channel", fromClaude: false, viaWhisper: false);
			channel.Append(0x2, "B", "in private", fromClaude: false, viaWhisper: true);

			var messages = channel.ReadSince(0);
			Assert.Equal(2, messages.Count);
			Assert.False(messages.Single(m => m.SenderName == "A").ViaWhisper);
			Assert.True(messages.Single(m => m.SenderName == "B").ViaWhisper);
		}
	}
}
