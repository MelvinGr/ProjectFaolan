using System.IO;

namespace Faolan.Core.Protobuf
{
	// Hand-rolled reader for the RpcHeader endpoint fields, the counterpart to ToProtobuf() (locked byte-exact by
	// ProtoEndpointGoldenTests). PacketParser reads the header's caller_id (field 1, tag 0x0A) then endpoint_id
	// (field 2, tag 0x12) straight off the FaolanStream with this, instead of a generic protobuf tree walk +
	// protobuf-net round-trip - the endpoint wire is fixed and tiny: [tag][len][ 0x0D type(fixed32 LE)  0x10
	// instance(varint)  (0x18 token(varint))? ].
	public partial class ProtoEndpoint
	{
		public enum FieldTags : byte
		{
			Type = 0x0D, // field 1, wire type 5 (fixed32)
			Instance = 0x10, // field 2, wire type 0 (varint)
			Token = 0x18 // field 3, wire type 0 (varint)
		}

		// Reads one length-delimited endpoint field (its outer tag + length, then the sub-message) from `stream`.
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
						endpoint.Type = stream.ReadUInt32LittleEndian();
						break;
					case FieldTags.Instance:
						endpoint.Instance = (uint)stream.ReadVarint();
						break;
					case FieldTags.Token:
						endpoint.Token = (uint)stream.ReadVarint();
						break;
					default:
						throw new InvalidDataException($"Unexpected ProtoEndpoint field tag 0x{tag:X2} at offset {stream.Position}.");
				}
			}

			return endpoint;
		}
	}
}
