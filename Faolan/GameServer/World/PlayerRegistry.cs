using System.Collections.Concurrent;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The connection-to-entity map: every player currently in the world, keyed by the connection that owns them.
	/// </summary>
	/// <remarks>
	///     A named type rather than a bare <c>ConcurrentDictionary&lt;INetworkClient, WorldEntity&gt;</c> so the
	///     container can name it, and <see cref="WorldSimulation" /> and <see cref="PlayerDeathService" /> can be
	///     handed the same instance without either reaching into the other. Deriving is deliberate: it is a pure alias
	///     with no behaviour of its own, so every dictionary call site keeps working unchanged.
	/// </remarks>
	public sealed class PlayerRegistry : ConcurrentDictionary<INetworkClient, WorldEntity>
	{
	}
}
