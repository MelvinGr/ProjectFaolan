using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network
{
	/// <summary>
	///     The live connections on one listener, and the lookups over them.
	/// </summary>
	/// <remarks>
	///     Wire classes whose only listener-scoped need is "give me the client for this character id" take this
	///     registry rather than the listener, which is what keeps outbound ports (<c>ITradeOutbound</c>,
	///     <c>IQuestShareOutbound</c>, <c>IApprenticeOutbound</c>, …) off <c>GameServerListener</c>.
	///     <para>
	///         <see cref="ServerBase" /> is handed one and delegates its own connection members to it.
	///     </para>
	/// </remarks>
	public sealed class ConnectionRegistry
	{
		// Used as a set: the value is unused, and the dictionary is concurrent because accepts, disconnects and world
		// broadcasts all touch it from different threads.
		private readonly ConcurrentDictionary<INetworkClient, byte> _clients = [];

		public IEnumerable<INetworkClient> Clients => _clients.Keys;
		public IEnumerable<INetworkClientSession> Sessions => _clients.Keys.Select(c => c.Session);

		public void Add(INetworkClient client)
		{
			_clients.TryAdd(client, 0);
		}

		public void Remove(INetworkClient client)
		{
			_clients.TryRemove(client, out _);
		}

		/// <summary>The connected client for <paramref name="characterId" />, or null when they are not on this listener.</summary>
		public INetworkClient? ForCharacter(uint characterId)
		{
			return _clients.Keys.FirstOrDefault(nc => nc.Session.Character?.Id == characterId);
		}

		/// <summary>The connected client for <paramref name="accountId" />, or null.</summary>
		public INetworkClient? ForAccount(uint accountId)
		{
			return _clients.Keys.FirstOrDefault(nc => nc.Session.Account?.Id == accountId);
		}
	}
}
