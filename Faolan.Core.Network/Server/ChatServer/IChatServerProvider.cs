using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.Core.Network.Server.ChatServer
{
	public interface IChatServerProvider
	{
		/// <summary>
		///     Delivers a character's "say" to ChatServer clients as a CharacterChat.
		/// </summary>
		/// <param name="recipientCharacterIds">
		///     Scopes the message to those characters - the proximity say, whose in-range set the GameServer computes
		///     because it holds the live positions. Null delivers to every connected client.
		/// </param>
		Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null);

		/// <summary>
		///     An NPC's ambient or encounter bark, as typed content rather than pre-rendered wire text. The world model
		///     caches the bark value and this tier renders it, which keeps the remoteformat layer inside
		///     <c>Codecs/</c>.
		/// </summary>
		Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null);

		/// <summary>
		///     The complete live <see cref="INetworkClientSession" /> of every connected client. Cheap, read-only and
		///     safe to call at any time; do not mutate the returned objects.
		/// </summary>
		IEnumerable<INetworkClientSession> GetWorldSessions();

		/// <summary>
		///     Posts a message on the Claude channel (<see cref="ClaudeChannel" />) as the synthetic "Claude" sender,
		///     broadcast to every connected client. Returns the number of clients it was delivered to.
		/// </summary>
		Task<int> SendClaudeChannelMessage(ChatMarkup message);

		/// <summary>
		///     Brings a character's <c>~Playfield</c> chat channel in line with the map they are now on: drops the old
		///     channel and subscribes the new one. Idempotent: a call naming the map the client is already subscribed to
		///     does nothing, so the world tier can call it after any map change without tracking what it sent last.
		///     <para>
		///         Channel routing is the client's job - a ChannelChat is relayed to everyone and each client shows only
		///         the channels it is subscribed to - so a stale subscription leaves the player hearing the playfield
		///         they left and never the one they are in.
		///     </para>
		/// </summary>
		Task RefreshPlayfieldChannel(uint characterId, uint mapId);

		/// <summary>
		///     Brings a character's <c>~Guild</c> chat channel in line with the guild they are now in: drops the old
		///     channel and subscribes the new one. <paramref name="guildId" /> is null when they are guildless, which
		///     only drops. Idempotent, like <see cref="RefreshPlayfieldChannel" />, so the guild tier can call it after
		///     any membership change without tracking what it sent last.
		///     <para>
		///         The subscription is what makes the guild chat tab appear: without it the window opens but the tab
		///         never does, because a channel the client holds no subscription for is one it silently drops.
		///     </para>
		/// </summary>
		Task RefreshGuildChannel(uint characterId, uint? guildId);

		/// <summary>
		///     Brings a character's <c>~Team</c> chat channel in line with the team they are now in, or drops it when
		///     <paramref name="teamId" /> is null. Same idempotent contract as <see cref="RefreshGuildChannel" />.
		/// </summary>
		Task RefreshTeamChannel(uint characterId, uint? teamId);

		/// <summary>
		///     Whispers a single character as the synthetic "Claude" sender. Returns false when the character has no
		///     connected ChatServer client.
		/// </summary>
		Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message);

		Task SendSystemMessage(uint characterId, ChatMarkup message);
		Task SystemBroadcast(ChatMarkup message);
	}
}
