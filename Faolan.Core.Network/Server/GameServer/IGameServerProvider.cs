using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.AgentServer;

namespace Faolan.Core.Network.Server.GameServer
{
	/// <summary>
	///     Seam into a live GameServer listener, mirroring <see cref="IAgentServerProvider" /> but for the
	///     world connection: enumerating connected world sessions and driving server-side actions (spawns,
	///     teleports, …) by issuing the same GM commands a game master would. Implemented by the listener;
	///     reached through <see cref="IGameServerDispatcher" />.
	/// </summary>
	public interface IGameServerProvider
	{
		/// <summary>
		///     Force-disconnect a connected character (closes the world socket). Returns false when the
		///     character is not connected to this listener (so a dispatcher can try the next provider). Used by
		///     the web admin "ban + kick" path via MCP.
		/// </summary>
		bool DisconnectCharacter(uint characterId);

		/// <summary>
		///     The live world state of a connected character (position, vitals, buffs), or null when the
		///     character is not connected to this listener (so a dispatcher can try the next provider).
		/// </summary>
		CharacterLiveState GetCharacterLiveState(uint characterId);

		/// <summary>Snapshot of clients connected to this listener. Cheap, read-only.</summary>
		IReadOnlyList<SessionInfo> GetSessions();

		/// <summary>
		///     Snapshot of the live world model for a map: every entity in the zone (players, NPCs, loot bags)
		///     with vitals/transform. Cheap, read-only; empty when this listener tracks nothing on the map.
		/// </summary>
		IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId);

		/// <summary>
		///     Runs a GM command (e.g. ".teleport 5", ".test") for the given character if it is connected to
		///     this listener. Returns false when the character is not connected here (so a dispatcher can try
		///     the next provider). The text must include the leading command prefix.
		/// </summary>
		Task<bool> RunGmCommand(uint characterId, string commandText);

		/// <summary>
		///     Pushes the in-world guild tag (0x2550C99A GuildUpdateIIR) onto the character's live world
		///     connection, if connected to this listener. An empty name clears the tag. Used by the
		///     CsPlayerAgent guild flow so joining/leaving a guild updates the world view without a relog.
		/// </summary>
		Task<bool> SendGuildTag(uint characterId, string guildName);
	}
}
