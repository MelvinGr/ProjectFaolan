using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.AgentServer
{
	public interface IAgentServerProvider
	{
		// Delivers a character's "say" to AgentServer clients as a CharacterChat (0x0022). `recipientCharacterIds`
		// scopes it to those characters (proximity say - the GameServer computes who is in range, since it holds the
		// live positions); null = every connected client (server-wide).
		Task CharacterSay(uint characterId, string message, IReadOnlyCollection<uint> recipientCharacterIds = null);
		Task SendSystemMessage(uint characterId, string message);
		Task SystemBroadcast(string message);

		/// <summary>Snapshot of currently connected sessions. Cheap, read-only, safe to call at any time.</summary>
		IReadOnlyList<SessionInfo> GetSessions();
	}
}
