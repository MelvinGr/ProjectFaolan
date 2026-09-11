using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The wire form of an RPC endpoint: turning a (type, instance) pair into the bytes a packet header carries.
	/// </summary>
	/// <remarks>
	///     The class ids themselves live in <c>Faolan.Core.Constants.RpcInterfaces</c>, which also carries each class's
	///     name and its <c>*Methods</c> enum; callers name that owner directly
	///     (<c>RpcInterfaces.GameCharAgentType</c>) rather than a forwarding constant here.
	///     <para>
	///         A Conan packet header is the client's RpcHeader. Its <c>caller_id</c> (field 1, our "sender") and
	///         <c>endpoint_id</c> (field 2, our "receiver") are each a
	///         <c>ProtoEndpoint { fixed32 type = 1; uint32 instance = 2; uint32 token = 3; }</c>, where
	///         <c>type</c> is the object-class id, <c>instance</c> is the specific object (the character id for the
	///         player, the zone id for the world) and <c>token</c> is never present on the wire in any capture.
	///     </para>
	///     <para>
	///         A serialized endpoint is exactly <c>[0x0D &lt;type LE32&gt; 0x10 &lt;instance varint&gt;]</c>. Build them
	///         through <c>ProtoEndpoint</c> rather than as byte literals, so a wrong type/instance pairing is
	///         impossible by construction. The type ids come from the client schema and are verified against 109k
	///         captured endpoints.
	///     </para>
	/// </remarks>
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

		/// <summary>
		///     The serialized <c>[0x0D &lt;type LE32&gt; 0x10 &lt;instance varint&gt;]</c> bytes, for the byte[]-based
		///     <c>WriteHeader</c> overload and other call sites that pass raw endpoint blobs.
		/// </summary>
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
