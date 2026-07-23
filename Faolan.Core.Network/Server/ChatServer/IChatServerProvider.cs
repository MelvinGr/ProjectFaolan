using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.ChatServer
{
	public interface IChatServerProvider
	{
		// Delivers a character's "say" to ChatServer clients as a CharacterChat (0x0022). `recipientCharacterIds`
		// scopes it to those characters (proximity say - the GameServer computes who is in range, since it holds the
		// live positions); null = every connected client (server-wide).
		Task CharacterSay(uint characterId, string message, IReadOnlyCollection<uint>? recipientCharacterIds = null);

		/// <summary>
		///     The COMPLETE live <see cref="INetworkClientSession" /> of every connected client - full session state.
		///     Cheap, read-only, safe to call at any time (e.g. the MCP list_sessions tool); do not mutate the returned
		///     objects.
		/// </summary>
		IReadOnlyList<INetworkClientSession> GetWorldSessions();

		/// <summary>
		///     Posts a message on the Claude channel (<see cref="ClaudeChannel" />) as the synthetic "Claude" sender,
		///     broadcast to every connected client. Returns the number of clients it was delivered to.
		/// </summary>
		Task<int> SendClaudeChannelMessage(string message);

		/// <summary>
		///     Whispers (0x001E tell) a single character as the synthetic "Claude" sender. Returns false when the
		///     character has no connected ChatServer client.
		/// </summary>
		Task<bool> SendClaudeWhisper(uint targetCharacterId, string message);

		Task SendSystemMessage(uint characterId, string message);
		Task SystemBroadcast(string message);
	}
}
