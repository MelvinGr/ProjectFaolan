using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.AgentServer
{
	public interface IAgentServerProvider
	{
		Task CharacterSay(uint characterId, string message);
		Task SendSystemMessage(uint characterId, string message);
		Task SystemBroadcast(string message);

		/// <summary>Snapshot of currently connected sessions. Cheap, read-only, safe to call at any time.</summary>
		IReadOnlyList<SessionInfo> GetSessions();
	}
}
