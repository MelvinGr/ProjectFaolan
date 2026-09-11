using System.ComponentModel;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.ChatServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Claude's voice in the game: every client is subscribed to the in-game "Claude" channel on login, and these
	///     tools broadcast or whisper onto it under the Claude identity (via <see cref="IChatServerDispatcher" />).
	///     <para>
	///         Outbound only: there is no read tool, because nothing ingests player chat. A player's post on the
	///         channel is relayed and persisted to the chat history like any other channel's, and reaches Claude
	///         nowhere.
	///     </para>
	/// </summary>
	[McpServerToolType]
	public sealed class ClaudeChannelTools
	{
		private readonly IChatServerDispatcher _dispatcher;

		public ClaudeChannelTools(IChatServerDispatcher dispatcher)
		{
			_dispatcher = dispatcher;
		}

		[McpServerTool(Name = "send_claude_channel_message"),
		 Description("Post a chat message to the in-game Claude channel as \"Claude\". Delivered immediately to every connected client (all players are subscribed to the channel on login).")]
		public async Task<string> SendClaudeChannelMessage([Description("The chat message to post.")] string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				return "Message is empty; nothing sent.";

			var delivered = await _dispatcher.SendClaudeChannelMessage(message);
			return delivered == 0
				? "No clients are currently connected to the ChatServer; nothing was sent."
				: $"Posted to the Claude channel; delivered to {delivered} connected client(s).";
		}

		[McpServerTool(Name = "send_claude_whisper"),
		 Description(
			 "Whisper (private tell) a single connected character as \"Claude\" - only they see it, unlike the channel broadcast. Takes the target's character id (e.g. from list_sessions).")]
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
