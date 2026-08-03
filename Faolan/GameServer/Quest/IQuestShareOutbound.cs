using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.GameServer;

namespace Faolan.GameServer.Quest
{
	// The wire + world side of a quest SHARE, as a narrow port the listener implements - the same shape ITradeOutbound
	// takes for the other two-player interaction. QuestSharingService owns the rules and reaches everything else
	// through this, so it never holds the listener.
	//
	// Everything is addressed by CHARACTER ID: a share always spans two connections, and the service has no business
	// resolving either.
	public interface IQuestShareOutbound
	{
		/// <summary>
		///     s2c DispatchQuestShareRequest (0x204A): raise the share window on <paramref name="recipientId" />'s client,
		///     naming the sharer and the quest. Returns false when that character is not connected here.
		/// </summary>
		Task<bool> SendQuestShareRequest(uint recipientId, uint sharerId, uint questId, string questName);

		/// <summary>s2c DispatchQuestShareGlobalFeedback (0x2048): the answer to the sharer about the share as a whole.</summary>
		Task SendQuestShareGlobalFeedback(uint recipientId, QuestShareFeedback type, uint value = 0);

		/// <summary>s2c DispatchQuestShareIndividualFeedback (0x2049): the answer to the sharer about one named player.</summary>
		Task SendQuestShareIndividualFeedback(uint recipientId, QuestShareFeedback type, string playerName);

		/// <summary>
		///     The connected client for <paramref name="characterId" />, or null. A grant has to run against a live session
		///     (it sends the J-panel packets), which is exactly what the accept path holds an id for.
		/// </summary>
		INetworkClient? ClientFor(uint characterId);

		/// <summary>
		///     Where <paramref name="characterId" /> is right now (playfield + position), or null when not in the world.
		///     Drives the range test - the client applies the same one on its side, see QuestSharingService.ShareRadius.
		/// </summary>
		CharacterLiveState? LiveStateOf(uint characterId);
	}
}
