using Faolan.Core.Constants;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Constants
{
	// ⚠️ The class IDS live in `Faolan.Core.Constants.RpcInterfaces`, which also carries each class's NAME and its
	// `*Methods` enum. They were duplicated here as forwarding constants for a while; that is still a second name for
	// one thing, so callers name the owner directly (`RpcInterfaces.GameCharAgentType`). What is left here is this
	// type's actual job: the WIRE FORM - turning (type, instance) into the bytes a header carries.
	//
	// A Conan packet header is the client's RpcHeader (recovered from AgeOfConan.exe). Its caller_id (field 1,
	// our "sender") and endpoint_id (field 2, our "receiver") are each a ProtoEndpoint:
	//
	//   message ProtoEndpoint { fixed32 type = 1; uint32 instance = 2; uint32 token = 3; }
	//
	//   - type     = the object-CLASS id (`RpcInterfaces`)
	//   - instance = the specific object: the character id for the player, the zone id for the world
	//   - token    = never present on the wire in any capture
	//
	// On the wire a serialized endpoint is exactly [0x0D <type LE32> 0x10 <instance varint>] - the same bytes the
	// codebase used to hand-roll as byte[] literals. Building them through ProtoEndpoint makes a wrong type/instance
	// (the create-editor receiver-mismatch bug) impossible by construction. The type ids were recovered from the
	// client schema and verified against 109k captured endpoints.
	public static class Endpoints
	{

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

		public static ProtoEndpoint EndpointFromBytes(params byte[] subMessage)
		{
			using var stream = new FaolanStream(subMessage);
			var endpoint = new ProtoEndpoint();
			while (stream.Position < stream.Length)
			{
				switch (stream.ReadByte<ProtoEndpoint.FieldTags>())
				{
					case ProtoEndpoint.FieldTags.Type:
					{
						endpoint.Type = stream.ReadUInt32LittleEndian();
						break;
					}

					case ProtoEndpoint.FieldTags.Instance:
					{
						endpoint.Instance = (uint)stream.ReadVarint();
						break;
					}

					case ProtoEndpoint.FieldTags.Token:
					{
						endpoint.Token = (uint)stream.ReadVarint();
						break;
					}
				}
			}

			return endpoint;
		}
	}
}
