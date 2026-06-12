using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Network.AgentServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Tools that observe the live running server (not just the database). Backed by
	///     <see cref="IAgentServerDispatcher" />, the existing seam into the registered agent-server provider.
	/// </summary>
	[McpServerToolType]
	public sealed class SessionTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

		private readonly IAgentServerDispatcher _dispatcher;

		public SessionTools(IAgentServerDispatcher dispatcher)
		{
			_dispatcher = dispatcher;
		}

		[McpServerTool(Name = "list_sessions"), Description("List the clients currently connected to the live Faolan agent server, with account/character identity and source ip. Read-only snapshot of the running server.")]
		public string ListSessions()
		{
			var sessions = _dispatcher.GetSessions();
			if (sessions.Count == 0)
				return "No clients are currently connected.";

			return JsonSerializer.Serialize(sessions, JsonOptions);
		}

		[McpServerTool(Name = "send_system_message"),
		 Description("Send an in-game system message (the yellow server text) to one connected character. Supports basic HTML like <br /> and <span style=\"color: red;\">.")]
		public async Task<string> SendSystemMessage([Description("Character id of a currently-connected player (see list_sessions)")] uint characterId, [Description("The message text")] string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				return "Message is empty; nothing sent.";

			if (_dispatcher.GetSessions().All(s => s.CharacterId != characterId))
				return $"Character {characterId} is not currently connected to the AgentServer; nothing sent.";

			await _dispatcher.SendSystemMessage(characterId, message);
			return $"Sent the system message to character {characterId}.";
		}
	}
}
