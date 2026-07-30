using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	// The wire side of local travel (#506) - what TravelService needs from the listener. Narrow port rather than the
	// listener itself, same pattern as IWorldOutbound / IEconomyOutbound / IEnvironmentOutbound.
	public interface ITravelOutbound
	{
		// The scry confirmation box naming the destination MAP (DispatchScryShowMessageBox 0x205E). `threadCtxId` and
		// `cookie` come straight back in the player's answer, which is what correlates the reply to the pending trip.
		Task SendTravelConfirm(INetworkClient client, uint destinationMapId, uint threadCtxId, ulong cookie);

		// Tell the player why the NPC will not take them (criminal, or too poor).
		Task SendTravelRefusal(INetworkClient client, string reason);

		// Push the new wallet balance after the fee is taken.
		Task SendTravelFeeCharged(INetworkClient client, ulong balanceTin);

		// A CROSS-MAP trip: the full changemap handoff (the same path GM `.teleport` and the recalls use). `arrival`
		// null = the destination map's default spawn.
		Task TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival);

		// A SAME-MAP trip: relocate in place, with no loading screen - the wire path the revive teleport already uses.
		Task RelocateOnSameMap(INetworkClient client, Vector3 arrival);
	}
}
