using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	// The wire side of local travel (#506) - what TravelService needs from the listener. Narrow port rather than the
	// listener itself, same pattern as IWorldOutbound / IEconomyOutbound / IEnvironmentOutbound.
	public interface ITravelOutbound
	{
		// Tell the player why the NPC will not take them (criminal, or too poor).
		Task SendTravelRefusal(INetworkClient client, string reason);

		// Push the new wallet balance after the fee is taken.
		Task SendTravelFeeCharged(INetworkClient client, ulong balanceTin);

		// #636: what retail sends on the destination pick, BEFORE the relocate/handoff - the trip's own
		// GameActionListIIR (the 0xD00D "the server ran the travel script" action, stating the whole trip in one
		// string) and the casterless *Racer* buff behind it. `sameMap` picks the action's trailing flag: `Local=1`
		// for a relocate on the playfield the player is already on, `HeroicEnabled=0` for one that crosses.
		Task SendTripDeparture(INetworkClient client, uint destinationMapId, Vector3 arrival, bool sameMap);

		// A CROSS-MAP trip: the full changemap handoff (the same path GM `.teleport` and the recalls use). `arrival`
		// null = the destination map's default spawn.
		Task TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival);

		// A SAME-MAP trip: relocate in place, with no loading screen - the wire path the revive teleport already uses.
		Task RelocateOnSameMap(INetworkClient client, Vector3 arrival);

		// #635: the baked terrain height at (x, z), or null when no terrain is loaded for that map. A
		// `{teleportplayer}` directive may state only X and Z - Wagoner Polus's `{teleportplayer 349 564 1000}` does -
		// and dropping the whole arrival for a missing height would send the player to the map's default spawn
		// instead of where the NPC said. Through the port because the terrain lives on the simulation, not here.
		float? GroundHeight(uint mapId, float x, float z);
	}
}
