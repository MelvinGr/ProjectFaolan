using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.ChatServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     The Claude chat channel bridge: every client is subscribed to the in-game "Claude" channel on login;
	///     player posts on it land in the <see cref="ClaudeChannel" /> log, and these tools let Claude read that
	///     log and reply in-game (broadcast via <see cref="IChatServerDispatcher" /> under the Claude identity).
	/// </summary>
	[McpServerToolType]
	public sealed class ClaudeChannelTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true
		};

		private readonly ClaudeChannel _channel;
		private readonly IChatServerDispatcher _dispatcher;

		public ClaudeChannelTools(ClaudeChannel channel, IChatServerDispatcher dispatcher)
		{
			_channel = channel;
			_dispatcher = dispatcher;
		}

		[McpServerTool(Name = "read_claude_channel"),
		 Description("Read the in-game Claude chat channel log (player posts + Claude's replies), oldest first. Pass afterSequence from a previous read to only get new messages. Read-only.")]
		public string ReadClaudeChannel(
			[Description("Only return messages with Sequence greater than this (0 = all retained messages). Use the highest Sequence you have seen to poll for new posts.")] ulong afterSequence = 0)
		{
			var messages = _channel.ReadSince(afterSequence);
			if (messages.Count == 0)
				return $"No messages after sequence {afterSequence} (latest sequence: {_channel.LatestSequence}).";

			return JsonSerializer.Serialize(messages, JsonOptions);
		}

		[McpServerTool(Name = "send_claude_channel_message"),
		 Description("Post a chat message to the in-game Claude channel as \"Claude\". Delivered immediately to every connected client (all players are subscribed to the channel on login).")]
		public async Task<string> SendClaudeChannelMessage([Description("The chat message to post.")] string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				return "Message is empty; nothing sent.";

			var delivered = await _dispatcher.SendClaudeChannelMessage(message);
			return delivered == 0
				? "No clients are currently connected to the ChatServer; the message was logged on the channel but reached nobody."
				: $"Posted to the Claude channel; delivered to {delivered} connected client(s).";
		}

		[McpServerTool(Name = "send_claude_whisper"),
		 Description(
			 "Whisper (private tell) a single connected character as \"Claude\" - only they see it, unlike the channel broadcast. Use the sender's character id from the channel log / channel event meta.")]
		public async Task<string> SendClaudeWhisper([Description("Character id of the connected player to whisper.")] uint characterId, [Description("The whisper text.")] string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				return "Message is empty; nothing sent.";

			return await _dispatcher.SendClaudeWhisper(characterId, message)
				? $"Whispered character {characterId}."
				: $"Character {characterId} has no connected chat client; nothing sent.";
		}
	}
}
