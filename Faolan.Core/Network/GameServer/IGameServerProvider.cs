using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.AgentServer;

namespace Faolan.Core.Network.GameServer
{
	/// <summary>
	/// Seam into a live GameServer listener, mirroring <see cref="IAgentServerProvider"/> but for the
	/// world connection: enumerating connected world sessions and driving server-side actions (spawns,
	/// teleports, …) by issuing the same GM commands a game master would. Implemented by the listener;
	/// reached through <see cref="IGameServerDispatcher"/>.
	/// </summary>
	public interface IGameServerProvider
	{
		/// <summary>Snapshot of clients connected to this listener. Cheap, read-only.</summary>
		IReadOnlyList<SessionInfo> GetSessions();

		/// <summary>
		/// Runs a GM command (e.g. ".teleport 5", ".test") for the given character if it is connected to
		/// this listener. Returns false when the character is not connected here (so a dispatcher can try
		/// the next provider). The text must include the leading command prefix.
		/// </summary>
		Task<bool> RunGmCommand(uint characterId, string commandText);
	}
}
