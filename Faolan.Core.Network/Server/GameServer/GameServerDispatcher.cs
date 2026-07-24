using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.GameServer
{
	public interface IGameServerDispatcher : IGameServerProvider
	{
		void Register(IGameServerProvider provider);
		void Unregister(IGameServerProvider provider);
	}

	/// <summary>
	///     Single-provider dispatcher, mirroring <see cref="ChatServer.ChatServerDispatcher" />: a process hosts
	///     at most ONE realm (<c>--role realm=&lt;id&gt;</c>, #324) and that realm has exactly one GameServer
	///     listener, so every call forwards straight to it. Multi-realm is process-per-realm, never
	///     listener-per-realm - so there is nothing to aggregate or route between here.
	/// </summary>
	public sealed class GameServerDispatcher : IGameServerDispatcher
	{
		private readonly Lock _lock = new Lock();
		private IGameServerProvider? _provider;

		public void Register(IGameServerProvider provider)
		{
			lock (_lock)
			{
				if (_provider != null && _provider != provider)
					throw new InvalidOperationException("Only one game server provider can be registered at a time.");

				_provider = provider;
			}
		}

		public void Unregister(IGameServerProvider provider)
		{
			lock (_lock)
			{
				if (_provider == provider)
					_provider = null;
			}
		}

		public bool DisconnectCharacter(uint characterId)
		{
			return _provider?.DisconnectCharacter(characterId) ?? false;
		}

		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			return _provider?.GetCharacterLiveState(characterId);
		}

		public IEnumerable<INetworkClientSession> GetWorldSessions()
		{
			lock (_lock)
			{
				return _provider?.GetWorldSessions() ?? [];
			}
		}

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			return _provider?.GetZoneEntities(mapId) ?? [];
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			if (_provider != null)
				return await _provider.RunGmCommand(characterId, commandText);

			return false;
		}

		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			if (_provider != null)
				return await _provider.SendGuildTag(characterId, guildName);

			return false;
		}
	}
}
