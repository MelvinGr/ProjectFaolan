using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.Core;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Faolan.GameServer;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Faolan.GameServer.Wire
{
	// The GameServer tier's SENDER endpoints - the `sender` half of every RpcHeader this realm writes. They used to be
	// statics on the listener, which made every service that frames its own send reach back into the listener for a
	// constant (#928 item 1). A constant is not listener behaviour; it lives here.
	public static class GameEndpoints
	{
		// The world-conversation sender shared by every world-entry / world-event packet (formerly the WorldFraming
		// class): sender = the world-system conversation (family prefix hash("GameAgent") + sub-id 0x58); the receiver
		// is the recipient's GameInterface endpoint addressed to its client instance. Confirmed from the Strie login
		// capture - keeping every world packet on this single conversation is what lets the client tie the spawns to the
		// loaded playfield and render them. Public because the composed collaborators (WorldBroadcaster, CombatService,
		// the economy services, VitalsPacket) frame their own world sends against it.
		// Instance 0x58 from the Strie capture; retail's per-session value varies (hoxxling 0x08, randochar 0x0b).
		// Tested 0x08 (2026-06-13) to clear the client's "n3ToClientQuit sent from 0 -> e_Impossible" warning - it did
		// NOT clear it, so the warning is benign (our despawn body is byte-identical to retail's). Reverted to 0x58.
		public static readonly ProtoEndpoint WorldSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x58);

		internal static readonly ProtoEndpoint ExistingCharacterPostReadyReportServerIdSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x58);

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		// Internal (not private) because the composed collaborators frame their own GameCharInterface sends
		// against it, the same reason WorldSenderEndpoint above is reachable - see Combat/CastService.cs.
		internal static readonly ProtoEndpoint LoginWorld201CSenderEndpoint = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58);

		// The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
		// GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), NOT the 0x58 of WorldSenderEndpoint that
		// normal world/NPC-combat broadcasts use. The client appears to gate the player's own dead-state transition on
		// the 0x59 endpoint - sending the full sequence from 0x58 left the avatar alive with the release UI shut (#214).
		internal static readonly ProtoEndpoint PlayerDeathSender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59);
	}
}
