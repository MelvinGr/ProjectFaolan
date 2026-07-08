using Faolan.Core.Network;
using Faolan.Core.Network.Constants;

namespace Faolan.GameServer.CreateFlow
{
	// Shared server->client framing for the character-creator 0x2000 conversation. Both the CreateFlowService
	// handshake methods and the listener's own SendWorldHandoff (the enter-world 0x2002) build packets with this
	// sender + per-session receiver, so it lives outside both rather than on either one.
	internal static class CreateEditorFraming
	{
		// Server->client conversation family for the creator 0x2000 traffic (GameAgent type, conversation id 0x0C).
		internal static readonly byte[] MorphAckSender = Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x0C);

		// The create-editor 0x2000 receiver: the player object (GameInterfaceType) addressed to this session's
		// client instance. Built as a typed ProtoEndpoint so the type+instance can't drift (the old hardcoded
		// receiver baked in 0x021662C8; see Endpoints / ProtoEndpointGoldenTests).
		internal static byte[] Receiver(INetworkClient client)
		{
			return client.Endpoint(Endpoints.GameInterfaceType);
		}
	}
}
