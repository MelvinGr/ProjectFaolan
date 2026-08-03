using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.GameServer
{
	public interface IGameServerDispatcher : IGameServerProvider
	{
		void Register(IGameServerProvider provider);
		void Unregister(IGameServerProvider provider);
	}

	/// <summary>
	///     The <see cref="ProviderDispatcher{TProvider}" /> slot for the world listener: every op forwards to the
	///     one registered GameServer listener, or answers empty when none is running.
	/// </summary>
	public sealed class GameServerDispatcher : ProviderDispatcher<IGameServerProvider>, IGameServerDispatcher
	{
		public bool DisconnectCharacter(uint characterId)
		{
			return Provider?.DisconnectCharacter(characterId) ?? false;
		}

		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			return Provider?.GetCharacterLiveState(characterId);
		}

		public IEnumerable<INetworkClientSession> GetWorldSessions()
		{
			return Provider?.GetWorldSessions() ?? [];
		}

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			return Provider?.GetZoneEntities(mapId) ?? [];
		}

		public async Task<bool> CreditCharacterCash(uint characterId, ulong amountTin)
		{
			var provider = Provider;
			return provider != null && await provider.CreditCharacterCash(characterId, amountTin);
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			var provider = Provider;
			return provider != null && await provider.RunGmCommand(characterId, commandText);
		}

		public async Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
		{
			var provider = Provider;
			return provider != null && await provider.SendGroupMembership(recipientId, memberId, teamId, leaderId, joined);
		}

		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			var provider = Provider;
			return provider != null && await provider.SendGuildTag(characterId, guildName);
		}
	}
}
