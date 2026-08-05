using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.Core.Network.Server.ChatServer
{
	public interface IChatServerProvider
	{
		// Delivers a character's "say" to ChatServer clients as a CharacterChat (0x0022). `recipientCharacterIds`
		// scopes it to those characters (proximity say - the GameServer computes who is in range, since it holds the
		// live positions); null = every connected client (server-wide).
		Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null);

		// An NPC's ambient/encounter BARK, as the typed content rather than pre-rendered wire text. The world model
		// caches the bark VALUE and this tier renders it, so the remoteformat layer stays inside `Codecs/` - the
		// broadcaster used to build the string at spawn time and store it on the entity (#539).
		Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null);

		/// <summary>
		///     The COMPLETE live <see cref="INetworkClientSession" /> of every connected client - full session state.
		///     Cheap, read-only, safe to call at any time (e.g. the MCP list_sessions tool); do not mutate the returned
		///     objects.
		/// </summary>
		IEnumerable<INetworkClientSession> GetWorldSessions();

		/// <summary>
		///     Posts a message on the Claude channel (<see cref="ClaudeChannel" />) as the synthetic "Claude" sender,
		///     broadcast to every connected client. Returns the number of clients it was delivered to.
		/// </summary>
		Task<int> SendClaudeChannelMessage(ChatMarkup message);

		/// <summary>
		///     Brings a character's <c>~Playfield</c> chat channel in line with the map they are now on: drops the old
		///     channel (0x003D) and subscribes the new one (0x003C). IDEMPOTENT - a call naming the map the client is
		///     already subscribed to does nothing, so the world tier can call it after any map change without tracking
		///     what it sent last.
		///     <para>
		///         It matters because channel routing is the CLIENT's job: a ChannelChat is relayed to everyone and each
		///         client shows only the channels it is subscribed to. Leaving the subscription stale does not just
		///         linger - the player keeps hearing the playfield they left and never hears the one they are in (#562).
		///     </para>
		/// </summary>
		Task RefreshPlayfieldChannel(uint characterId, uint mapId);

		/// <summary>
		///     Brings a character's <c>~Guild</c> chat channel in line with the guild they are now in: drops the old
		///     channel (0x003D) and subscribes the new one (0x003C). <paramref name="guildId" /> is null when they are
		///     guildless, which just drops. IDEMPOTENT, like <see cref="RefreshPlayfieldChannel" />, so the guild tier
		///     can call it after any membership change without tracking what it sent last.
		///     <para>
		///         The subscription is what makes the guild chat TAB appear: without it the window opens but the tab
		///         never does, because a channel the client holds no subscription for is one it silently drops (#663).
		///     </para>
		/// </summary>
		Task RefreshGuildChannel(uint characterId, uint? guildId);

		/// <summary>
		///     Whispers (0x001E tell) a single character as the synthetic "Claude" sender. Returns false when the
		///     character has no connected ChatServer client.
		/// </summary>
		Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message);

		Task SendSystemMessage(uint characterId, ChatMarkup message);
		Task SystemBroadcast(ChatMarkup message);
	}
}
