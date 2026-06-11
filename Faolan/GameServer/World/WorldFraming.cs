using Faolan.Core.Network;
using Faolan.Core.Network.Conan;

namespace Faolan.GameServer.World
{
	// The world-conversation framing shared by every world-entry / world-event packet: sender = the world-system
	// conversation (family prefix + sub-id 0x58), receiver = the recipient's GamePlayerType endpoint addressed to its
	// client instance. Confirmed from the Strie login capture - keeping every world packet on this single conversation
	// is what lets the client tie spawns to the loaded playfield and render them. Single source of truth, used by the
	// WorldBroadcaster and by the listener's login/create-flow senders (which delegate here).
	public static class WorldFraming
	{
		public static readonly byte[] Sender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];

		public static byte[] Receiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GamePlayerType, client.Session.Account.ClientInstance);
		}
	}
}
