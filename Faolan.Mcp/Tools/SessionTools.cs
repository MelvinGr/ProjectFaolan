using System.Text.Json;
using System.ComponentModel;
using Faolan.Core.Network.AgentServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	/// Tools that observe the live running server (not just the database). Backed by
	/// <see cref="IAgentServerDispatcher"/>, the existing seam into the registered agent-server provider.
	/// </summary>
	[McpServerToolType]
	public sealed class SessionTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		private readonly IAgentServerDispatcher _dispatcher;

		public SessionTools(IAgentServerDispatcher dispatcher) => _dispatcher = dispatcher;

		[McpServerTool(Name = "list_sessions")]
		[Description("List the clients currently connected to the live Faolan agent server, with account/character identity and source ip. Read-only snapshot of the running server.")]
		public string ListSessions()
		{
			var sessions = _dispatcher.GetSessions();
			if (sessions.Count == 0)
				return "No clients are currently connected.";

			return JsonSerializer.Serialize(sessions, JsonOptions);
		}
	}
}
