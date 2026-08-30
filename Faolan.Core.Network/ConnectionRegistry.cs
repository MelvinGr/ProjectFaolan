using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network
{
	/// <summary>
	///     The live connections on one listener, and the lookups over them. Extracted off <see cref="ServerBase" />
	///     (#826) so the things that SEND to a character no longer have to be the listener to find it.
	/// </summary>
	/// <remarks>
	///     This is the dependency that kept the remaining outbound ports on <c>GameServerListener</c>: unlike
	///     <c>IWorldOutbound</c>, which was a facade over services, ports like <c>ITradeOutbound</c> /
	///     <c>IQuestShareOutbound</c> / <c>IApprenticeOutbound</c> are the listener's own wire code whose only
	///     listener-scoped need is "give me the client for this character id". Handing THIS to a wire class is what
	///     lets that code move without a pile of Func seams pointing back at the listener.
	///     <para>
	///         <see cref="ServerBase" /> owns one and delegates its own members to it, so every existing call site is
	///         unchanged and this is a pure seam, not a behaviour change.
	///     </para>
	/// </remarks>
	public sealed class ConnectionRegistry
	{
		// A set, not a list: the value is unused (ConcurrentDictionary is the only concurrent set in the BCL), and it
		// is concurrent because accepts, disconnects and world broadcasts all touch it from different threads.
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
