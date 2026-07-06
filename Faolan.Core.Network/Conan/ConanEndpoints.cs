using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Conan
{
	// A Conan packet header is the client's RpcHeader (recovered from AgeOfConan.exe). Its caller_id (field 1,
	// our "sender") and endpoint_id (field 2, our "receiver") are each a ProtoEndpoint:
	//
	//   message ProtoEndpoint { fixed32 type = 1; uint32 instance = 2; uint32 token = 3; }
	//
	//   - type     = the object-CLASS id (a per-subsystem constant, listed below)
	//   - instance = the specific object: the character id for the player, the zone id for the world
	//   - token    = never present on the wire in any capture
	//
	// On the wire a serialized endpoint is exactly [0x0D <type LE32> 0x10 <instance varint>] - the same bytes the
	// codebase used to hand-roll as byte[] literals. Building them through ProtoEndpoint makes a wrong type/instance
	// (the create-editor receiver-mismatch bug) impossible by construction. The type ids below were recovered from
	// the client schema and verified against 109k captured endpoints.
	public static class ConanEndpoints
	{
		public const uint CSPlayerAgentType = 0x6DD55614; // = hash("CSPlayerAgent")     (CsPlayerAgent reply endpoint)
		public const uint CSPlayerInterfaceType = 0xBBD560C8; // = hash("CSPlayerInterface") (CsPlayerAgent reply caller)
		public const uint GameAgentType = 0xB171CE13; // = hash("GameAgent")
		public const uint GameCharAgentProjectType = 0x5B9756FC; // = hash("GameCharAgentProject")     (post-ready family)
		public const uint GameCharAgentType = 0xA9ECB95D; // = hash("GameCharAgent")      (0x201C spawn-handshake caller)
		public const uint GameCharInterfaceProjectType = 0x28377648; // = hash("GameCharInterfaceProject")  (report-server-id family)

		public const uint GameCharInterfaceType = 0x8B87F791; // = hash("GameCharInterface")  (0x201C spawn-handshake endpoint)

		// GameServer object-class type ids (ProtoEndpoint.type, fixed32). Each id = BKDR-31 hash of the client's
		// interface/agent class name (h = h*31 + c; == Java String.hashCode), cracked by RE'ing AgeOfConan.exe -
		// see docs/archive/opcode-re-plan.md + docs/aoc-opcode-ids.md. The C# constant names are kept for compatibility, but
		// the REAL client class name is noted per line (our originals were guesses). "Agent" = client->server proxy
		// side, "Interface" = server->client dispatch side.
		public const uint GameInterfaceType = 0x6C67C147; // = hash("GameInterface")

		// Tradepost (auction house + mail) rides the GameServer RPC layer. TradepostAgent is the SERVER object the
		// client calls (client->server); TradepostInterface is the CLIENT object the server dispatches to (s2c).
		public const uint TradepostAgentType = 0x3D744981; // = hash("TradepostAgent")     (client -> server endpoint)
		public const uint TradepostInterfaceType = 0xF2C2D5B5; // = hash("TradepostInterface") (server -> client endpoint)

		public static ProtoEndpoint Endpoint(uint type, uint instance)
		{
			return new ProtoEndpoint
			{
				Type = type,
				Instance = instance
			};
		}

		// The serialized [0x0D <type LE32> 0x10 <instance varint>] bytes, for the byte[]-based WriteHeader overload
		// and other call sites that still pass raw endpoint blobs.
		public static byte[] EndpointBytes(uint type, uint instance)
		{
			return Endpoint(type, instance).ToProtobuf();
		}
	}
}
