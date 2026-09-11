using System.IO;

namespace Faolan.Core.Protobuf
{
	/// <summary>
	///     Hand-rolled reader for the RpcHeader endpoint fields, the counterpart to <c>ToProtobuf()</c> and locked
	///     byte-exact by <c>ProtoEndpointGoldenTests</c>.
	/// </summary>
	/// <remarks>
	///     <c>Rpc.Parse</c> reads the header's <c>caller_id</c> (field 1, tag 0x0A) then <c>endpoint_id</c> (field 2,
	///     tag 0x12) straight off the <see cref="FaolanStream" /> with this, rather than a generic protobuf tree walk
	///     plus a protobuf-net round-trip: the endpoint wire is fixed and tiny -
	///     <c>[tag][len][0x0D type(fixed32 LE)  0x10 instance(varint)  (0x18 token(varint))?]</c>.
	/// </remarks>
	public partial class ProtoEndpoint
	{
		public enum FieldTags : byte
		{
			Type = 0x0D, // field 1, wire type 5 (fixed32)
			Instance = 0x10, // field 2, wire type 0 (varint)
			Token = 0x18 // field 3, wire type 0 (varint)
		}

		/// <summary>
		///     Reads one length-delimited endpoint field (its outer tag and length, then the sub-message) from
		///     <paramref name="stream" />.
		/// </summary>
		public static ProtoEndpoint Read(FaolanStream stream)
		{
			stream.ReadVarint(); // outer field tag (caller_id 0x0A / endpoint_id 0x12), wire type 2
			var end = stream.Position + (uint)stream.ReadVarint();

			var endpoint = new ProtoEndpoint();
			while (stream.Position < end)
			{
				var tag = stream.ReadByte<FieldTags>();
				switch (tag)
				{
					case FieldTags.Type:
					{
						endpoint.Type = stream.ReadUInt32LittleEndian();
						break;
					}

					case FieldTags.Instance:
					{
						endpoint.Instance = (uint)stream.ReadVarint();
						break;
					}

					case FieldTags.Token:
					{
						endpoint.Token = (uint)stream.ReadVarint();
						break;
					}

					default:
					{
						throw new InvalidDataException($"Unexpected ProtoEndpoint field tag 0x{tag:X2} at offset {stream.Position}.");
					}
				}
			}

			return endpoint;
		}
	}
}
