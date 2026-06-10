using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network.AgentServer;

namespace Faolan.Core.Network.GameServer
{
	public interface IGameServerDispatcher : IGameServerProvider
	{
		void Register(IGameServerProvider provider);
		void Unregister(IGameServerProvider provider);
	}

	/// <summary>
	/// Multi-provider dispatcher: Faolan runs two GameServer listeners (creator + world ports), and a client
	/// may be connected to either, so unlike the single-provider agent dispatcher this aggregates sessions
	/// across all registered listeners and routes a GM command to whichever one actually holds the character.
	/// </summary>
	public sealed class GameServerDispatcher : IGameServerDispatcher
	{
		private readonly Lock _lock = new();
		private readonly List<IGameServerProvider> _providers = [];

		public void Register(IGameServerProvider provider)
		{
			lock (_lock)
				if (!_providers.Contains(provider))
					_providers.Add(provider);
		}

		public void Unregister(IGameServerProvider provider)
		{
			lock (_lock)
				_providers.Remove(provider);
		}

		public IReadOnlyList<SessionInfo> GetSessions()
		{
			IGameServerProvider[] providers;
			lock (_lock)
				providers = _providers.ToArray();

			return providers.SelectMany(p => p.GetSessions()).ToArray();
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			IGameServerProvider[] providers;
			lock (_lock)
				providers = _providers.ToArray();

			foreach (var provider in providers)
				if (await provider.RunGmCommand(characterId, commandText))
					return true;

			return false;
		}
	}
}
