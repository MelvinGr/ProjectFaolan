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
		// GameServer object-class type ids (ProtoEndpoint.type, fixed32).
		public const uint GamePlayerType = 0x6C67C147; // the player character object (instance = character id)
		public const uint GameWorldType = 0xB171CE13; // the world / zone object (instance = zone id)
		public const uint GameSpawnCallerType = 0xA9ECB95D; // 0x201C spawn-handshake caller
		public const uint GameSpawnEndpointType = 0x8B87F791; // 0x201C spawn-handshake endpoint
		public const uint GamePostReadyType = 0x5B9756FC; // post-ready / ready-for-play family
		public const uint GameServerIdType = 0x28377648; // report-server-id family

		public static ProtoEndpoint Endpoint(uint type, uint instance) => new() { Type = type, Instance = instance };

		// The serialized [0x0D <type LE32> 0x10 <instance varint>] bytes, for the byte[]-based WriteHeader overload
		// and other call sites that still pass raw endpoint blobs.
		public static byte[] EndpointBytes(uint type, uint instance) => Endpoint(type, instance).ToProtobufBytes();
	}
}
