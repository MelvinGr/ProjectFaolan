using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The wire side of local travel - what <see cref="TravelService" /> needs from the GameServer tier. A narrow
	///     port rather than the listener itself, like <see cref="IWorldOutbound" /> and
	///     <see cref="IEnvironmentOutbound" />.
	/// </summary>
	public interface ITravelOutbound
	{
		/// <summary>Tell the player why the NPC will not take them: criminal, or too poor.</summary>
		Task SendTravelRefusal(INetworkClient client, string reason);

		/// <summary>Push the new wallet balance after the fee is taken.</summary>
		Task SendTravelFeeCharged(INetworkClient client, ulong balanceTin);

		/// <summary>
		///     What retail sends on the destination pick, before the relocate or handoff: the trip's own
		///     GameActionListIIR - the 0xD00D "the server ran the travel script" action, stating the whole trip in one
		///     string - and the casterless Racer buff behind it.
		/// </summary>
		/// <param name="sameMap">
		///     Picks the action's trailing flag: <c>Local=1</c> for a relocate on the playfield the player is already
		///     on, <c>HeroicEnabled=0</c> for one that crosses.
		/// </param>
		Task SendTripDeparture(INetworkClient client, uint destinationMapId, Vector3 arrival, bool sameMap);

		/// <summary>
		///     A cross-map trip: the full changemap handoff, the same path the GM teleport and the recalls use.
		/// </summary>
		/// <param name="arrival">Null for the destination map's default spawn.</param>
		Task TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival);

		/// <summary>
		///     A same-map trip: relocate in place, with no loading screen - the wire path the revive teleport uses.
		/// </summary>
		Task RelocateOnSameMap(INetworkClient client, Vector3 arrival);

		/// <summary>
		///     The baked terrain height at (x, z), or null when no terrain is loaded for that map.
		/// </summary>
		/// <remarks>
		///     A <c>{teleportplayer}</c> directive may state only X and Z - Wagoner Polus's
		///     <c>{teleportplayer 349 564 1000}</c> does - and dropping the whole arrival for a missing height would
		///     send the player to the map's default spawn instead of where the NPC said. It goes through the port
		///     because the terrain lives on the simulation.
		/// </remarks>
		float? GroundHeight(uint mapId, float x, float z);
	}
}
