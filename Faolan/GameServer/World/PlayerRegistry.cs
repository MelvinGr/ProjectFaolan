using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
		/// <summary>The players in <paramref name="zone" /> - its map and its instance - for that zone's own tick.</summary>
		public IEnumerable<KeyValuePair<INetworkClient, WorldEntity>> In(Zone zone)
		{
			return this.Where(p => p.Value.MapId == zone.MapId && p.Value.InstanceId == zone.InstanceId);
		}

		/// <summary>Whether <paramref name="client" />'s player is in <paramref name="zone" />.</summary>
		public bool IsIn(INetworkClient client, Zone zone)
		{
			return TryGetValue(client, out var entity) && entity.MapId == zone.MapId && entity.InstanceId == zone.InstanceId;
		}
	}
}
