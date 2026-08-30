using System.Collections.Concurrent;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	// The connection -> live entity map: every player currently IN the world, keyed by the connection that owns them.
	//
	// It is a named type rather than a bare `ConcurrentDictionary<INetworkClient, WorldEntity>` for one reason - so
	// the container can name it, and `WorldSimulation` and `PlayerDeathService` can be handed the SAME instance
	// without one of them having to reach into the other (#900). Deriving is deliberate: it is a pure alias with no
	// behaviour of its own, so every existing `GetOrAdd` / `TryGetValue` / `.Values` / `TryRemove` call site keeps
	// working unchanged, which is what keeps a 30-site rename out of a refactor that is already large.
	public sealed class PlayerRegistry : ConcurrentDictionary<INetworkClient, WorldEntity>
	{
	}
}
