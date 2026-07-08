using Faolan.Core.Network;
using Faolan.Core.Network.Conan;

namespace Faolan.GameServer.World
{
	// The world-conversation framing shared by every world-entry / world-event packet: sender = the world-system
	// conversation (family prefix + sub-id 0x58), receiver = the recipient's GameInterfaceType endpoint addressed to its
	// client instance. Confirmed from the Strie login capture - keeping every world packet on this single conversation
	// is what lets the client tie spawns to the loaded playfield and render them. Single source of truth, used by the
	// WorldBroadcaster and by the listener's login/create-flow senders (which delegate here).
	public static class WorldFraming
	{
		// Instance 0x58 from the Strie capture; retail's per-session value varies (hoxxling 0x08, randochar 0x0b).
		// Tested 0x08 (2026-06-13) to clear the client's "n3ToClientQuit sent from 0 -> e_Impossible" warning - it did
		// NOT clear it, so the warning is benign (our despawn body is byte-identical to retail's; it's the client's
		// default IIR PollStatus log). Reverted to 0x58 (the proven value).
		public static readonly byte[] Sender = Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x58);

		public static byte[] Receiver(INetworkClient client)
		{
			return client.Endpoint(Endpoints.GameInterfaceType);
		}
	}
}
