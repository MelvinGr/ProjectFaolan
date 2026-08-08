using System;
using System.Linq;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Mcp;
using Xunit;

namespace Faolan.Tests
{
	// The faolan-chat bridge's first-read backlog floor (#205). A fresh bridge process (spawned on every /mcp
	// reconnect / session restart) must NOT replay the whole in-memory log, but must still forward posts made in
	// the moments before it connected - otherwise a player who spoke just before the reconnect is silently dropped
	// even though the server store kept the message.
	public class ChannelBridgeBacklogTests
	{
		private static readonly DateTime Now = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
		private static readonly TimeSpan Grace = TimeSpan.FromSeconds(120);

		[Fact]
		public void InitialCursor_AllOld_SkipsEverything()
		{
			(ulong, DateTime)[] messages = [(1, Now.AddMinutes(-10)), (2, Now.AddMinutes(-5))];
			// Whole log is backlog -> floor is the latest (2); nothing is newer, so nothing is forwarded.
			Assert.Equal(2UL, ChannelBridge.InitialCursor(messages, Now, Grace));
		}

		[Fact]
		public void InitialCursor_AllRecent_ForwardsEverything()
		{
			(ulong, DateTime)[] messages = [(1, Now.AddSeconds(-40)), (2, Now.AddSeconds(-10))];
			// No backlog floor -> 0, so the loop forwards both (Sequence > 0).
			Assert.Equal(0UL, ChannelBridge.InitialCursor(messages, Now, Grace));
		}

		[Fact]
		public void InitialCursor_EmptyLog_IsZero()
		{
			Assert.Equal(0UL, ChannelBridge.InitialCursor([], Now, Grace));
		}

		[Fact]
		public void InitialCursor_ForwardsRecentPosts_SkipsOldBacklog()
		{
			// seq 1-3 are old (10+ min ago), seq 4-5 landed 30s before the bridge connected.
			(ulong, DateTime)[] messages = [(1, Now.AddMinutes(-10)), (2, Now.AddMinutes(-9)), (3, Now.AddMinutes(-8)), (4, Now.AddSeconds(-30)), (5, Now.AddSeconds(-15))];

			// Floor is the newest OLD message (3); the loop forwards everything with Sequence > 3 (the recent 4, 5).
			Assert.Equal(3UL, ChannelBridge.InitialCursor(messages, Now, Grace));
		}
	}

	// The Claude chat-bridge channel log. #97: the inbound MODE (whisper vs channel) is carried structurally on the
	// message (ViaWhisper) - NOT as a "(whisper) " text prefix - so the bridge can have Claude reply in the same mode.
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
