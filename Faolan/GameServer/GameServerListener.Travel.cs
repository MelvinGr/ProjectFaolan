using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	// The wire side of local travel (#506) - ITravelOutbound. The decisions live in World/TravelService.cs.
	public partial class GameServerListener
	{
		Task ITravelOutbound.SendTravelRefusal(INetworkClient client, string reason)
		{
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", reason));
		}

		Task ITravelOutbound.SendTravelFeeCharged(INetworkClient client, ulong balanceTin)
		{
			return SendUpdateCash(client, balanceTin, UpdateCashReason.Spend);
		}

		// A cross-map trip is the ordinary changemap handoff - the same path GM .teleport and the recalls take, so
		// travel gets the persisted transform, the loading screen and the re-entry latch for free.
		Task ITravelOutbound.TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival)
		{
			return DoTeleport(characterId, destinationMapId, arrival);
		}

		// A SAME-MAP trip: relocate in place with no loading screen - retail's `Local=1` trip. The wire pair is the one
		// the revive already uses (IntendedTeleport on the live playfield token, then n3Teleport carrying the
		// destination), which is exactly what the captured wagoner trip sends at t=781.5, so it is reused rather than
		// running a changemap the client would answer with a full reload.
		//
		// ⚠️ Retail ALSO precedes the pair with a GameActionListIIR carrying "xpos=..;ypos=..;zpos=..;PlayFieldID=..;
		// Local=1" and a "Racer" buff. We send neither: the n3Teleport already carries the position (the revive path
		// is in-client verified), and the GameActionList's string form is a movement instruction we have no builder
		// for. That is a fidelity gap, not a guess - tracked on #514.
		Task ITravelOutbound.RelocateOnSameMap(INetworkClient client, Vector3 arrival)
		{
			return _simulation.RelocateSameMap(client, arrival);
		}
	}
}
