using Faolan.Core.Constants;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.Wire
{
	/// <summary>
	///     The GameServer tier's sender endpoints - the <c>sender</c> half of every RpcHeader this realm writes.
	/// </summary>
	public static class GameEndpoints
	{
		// The world-conversation sender shared by every world-entry / world-event packet: sender = the world-system
		// conversation (family prefix hash("GameAgent") + sub-id 0x58); the receiver is the recipient's GameInterface
		// endpoint addressed to its client instance. Keeping every world packet on this single conversation is what
		// lets the client tie the spawns to the loaded playfield and render them. Public because the composed
		// collaborators (WorldBroadcaster, CombatService, the economy services, VitalsPacket) frame their own world
		// sends against it.
		// Instance 0x58; retail's per-session value varies (hoxxling 0x08, randochar 0x0b). The client's
		// "n3ToClientQuit sent from 0 -> e_Impossible" warning is benign and independent of this value - the despawn
		// body is byte-identical to retail's.
		public static readonly ProtoEndpoint WorldSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x58);

		internal static readonly ProtoEndpoint ExistingCharacterPostReadyReportServerIdSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x58);

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		// Internal (not private) because the composed collaborators frame their own GameCharInterface sends
		// against it, the same reason WorldSenderEndpoint above is reachable - see Combat/CastService.cs.
		internal static readonly ProtoEndpoint LoginWorld201CSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58);

		// The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
		// GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), not the 0x58 of WorldSenderEndpoint
		// that normal world/NPC-combat broadcasts use. The client gates the player's own dead-state transition on the
		// 0x59 endpoint: sending the full sequence from 0x58 leaves the avatar alive with the release UI shut.
		internal static readonly ProtoEndpoint PlayerDeathSender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59);
	}
}
