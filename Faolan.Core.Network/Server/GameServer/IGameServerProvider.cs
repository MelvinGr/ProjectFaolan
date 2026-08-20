using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.ChatServer;

namespace Faolan.Core.Network.Server.GameServer
{
	/// <summary>
	///     Seam into a live GameServer listener, mirroring <see cref="IChatServerProvider" /> but for the
	///     world connection: enumerating connected world sessions and driving server-side actions (spawns,
	///     teleports, …) by issuing the same GM commands a game master would. Implemented by the listener;
	///     reached through <see cref="IGameServerDispatcher" />.
	/// </summary>
	public interface IGameServerProvider
	{
		/// <summary>
		///     Force-disconnect a connected character (closes the world socket). Returns false when the
		///     character is not connected to this listener. Used by the web admin "ban + kick" path via MCP.
		/// </summary>
		bool DisconnectCharacter(uint characterId);

		/// <summary>
		///     The live world state of a connected character (position, vitals, buffs), or null when the
		///     character is not connected to this listener.
		/// </summary>
		CharacterLiveState? GetCharacterLiveState(uint characterId);

		/// <summary>
		///     The COMPLETE live <see cref="INetworkClientSession" /> of every client connected to this listener -
		///     the full session state (selected target, energy, conversation, entry mode, account/character, …).
		///     Read-only inspection (e.g. the MCP list_world_sessions tool); do not mutate the returned objects.
		/// </summary>
		IEnumerable<INetworkClientSession> GetWorldSessions();

		/// <summary>
		///     Snapshot of the live world model for a map: every entity in the zone (players, NPCs, loot bags)
		///     with vitals/transform. Cheap, read-only; empty when this listener tracks nothing on the map.
		/// </summary>
		IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId);

		/// <summary>
		///     Runs a GM command (e.g. ".teleport 5", ".test") for the given character if it is connected to
		///     this listener. Returns false when the character is not connected here. The text must include the
		///     leading command prefix.
		/// </summary>
		Task<bool> RunGmCommand(uint characterId, string commandText);

		/// <summary>
		///     Pushes the in-world guild tag (0x2550C99A GuildUpdateIIR) onto the character's live world
		///     connection, if connected to this listener. An empty name clears the tag. Used by the
		///     CsPlayerAgent guild flow so joining/leaving a guild updates the world view without a relog.
		/// </summary>
		Task<bool> SendGuildTag(uint characterId, string guildName);

		/// <summary>
		///     Pushes a group-membership record (AddToGroupIIR / RemoveFromGroupIIR) onto <paramref name="recipientId" />'s
		///     live world connection, saying that <paramref name="memberId" /> joined or left <paramref name="teamId" />.
		///     Retail emits these alongside the CsPlayerAgent roster dispatches - the team lives on the CS tier, but the
		///     world connection is told about membership too (docs/subsystems/teams-trade-friends.md). Returns false when
		///     the recipient is not connected to this listener.
		/// </summary>
		Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined);

		/// <summary>
		///     Teleports a connected character to a map, riding the same retail change-map sequence the travel box
		///     uses (announce + handoff + reconnect). <paramref name="position" /> null lands at the map's default
		///     spawn. The seam the CS tier's group-finder entry uses to put a formed team into its instance (#511);
		///     returns false when the character is not connected to this listener.
		/// </summary>
		Task<bool> TeleportCharacter(uint characterId, uint mapId, System.Numerics.Vector3? position = null);

		/// <summary>
		///     Adds <paramref name="amountTin" /> to a connected character's wallet, persists it and pushes the
		///     UpdateCash the client renders. Returns false when the character is not connected here, so the caller can
		///     refuse rather than move money nobody will see.
		///     <para>
		///         The wallet is the GAME tier's: it lives on the live session's <c>Character.Money</c> and the client
		///         learns of a change only through that tier's UpdateCash. A guild-bank withdrawal is decided on the CS
		///         tier (which owns <c>Guild.Cash</c> and the rank ladder) and paid out here, so this is the seam
		///         between the two halves of one transaction — hence "credit the connected character", not "set money".
		///     </para>
		/// </summary>
		Task<bool> CreditCharacterCash(uint characterId, ulong amountTin);
	}
}
