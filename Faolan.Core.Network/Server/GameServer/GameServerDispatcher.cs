using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.GameServer
{
	public interface IGameServerDispatcher : IGameServerProvider
	{
		void Register(IGameServerProvider provider);
		void Unregister(IGameServerProvider provider);
	}

	/// <summary>
	///     Multi-provider dispatcher: more than one GameServer listener can register, so unlike the
	///     single-provider agent dispatcher this aggregates sessions across all registered listeners and
	///     routes a GM command to whichever one actually holds the character.
	/// </summary>
	public sealed class GameServerDispatcher : IGameServerDispatcher
	{
		// Registration is set-semantics (deduped, order-independent) and reads vastly outnumber the rare
		// register/unregister, so a concurrent set gives lock-free enumeration. The byte value is unused - only
		// the keys matter (a routed character lives in exactly one provider, so iteration order never matters).
		private readonly ConcurrentDictionary<IGameServerProvider, byte> _providers = new();

		public void Register(IGameServerProvider provider)
		{
			_providers.TryAdd(provider, 0);
		}

		public void Unregister(IGameServerProvider provider)
		{
			_providers.TryRemove(provider, out _);
		}

		public bool DisconnectCharacter(uint characterId)
		{
			var disconnected = false;
			foreach (var provider in _providers.Keys)
				disconnected |= provider.DisconnectCharacter(characterId);
			return disconnected;
		}

		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			return _providers.Keys.Select(p => p.GetCharacterLiveState(characterId)).FirstOrDefault(s => s != null);
		}

		public IEnumerable<INetworkClientSession> GetWorldSessions()
		{
			return _providers.Keys.SelectMany(p => p.GetWorldSessions());
		}

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			// The listeners share one world simulation, so the first non-empty view is THE view.
			foreach (var provider in _providers.Keys)
			{
				var entities = provider.GetZoneEntities(mapId);
				if (entities.Count > 0)
					return entities;
			}

			return [];
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			foreach (var provider in _providers.Keys)
			{
				if (await provider.RunGmCommand(characterId, commandText))
					return true;
			}

			return false;
		}

		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			foreach (var provider in _providers.Keys)
			{
				if (await provider.SendGuildTag(characterId, guildName))
					return true;
			}

			return false;
		}
	}
}
