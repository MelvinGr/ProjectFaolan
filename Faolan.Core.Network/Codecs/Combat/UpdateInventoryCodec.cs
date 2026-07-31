using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// UpdateInventoryIIR (0xA74D2814) - a live inventory delta. The slot-8 subclass reader is a stub because the
	// client parses the body as PROTOBUF, not fixed fields (docs/re/v1-iir-readers.md). Frame:
	//   [handle][env][u32 pbLength][protobuf = ProtoInventoryGame]
	// The protobuf is the same item schema as the snapshot - repeated ProtoAcgItem entries, each carrying 3x
	// ProtoDecryptionKey (the item's 4x fixed32 TEA keys). This codec constructs the frame and carries the item
	// protobuf verbatim (deserialize with ProtoInventoryGame); byte-exact against the captured bodies.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class UpdateInventoryCodec
	{
		public const uint Opcode = (uint)IirTypes.UpdateInventoryIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateInventoryIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static UpdateInventoryIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateInventoryCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static UpdateInventoryIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "UpdateInventoryIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new UpdateInventoryIIR(handle, env, r.ReadBytesU32Length().ToArray());
		}

		internal static void Write(FaolanStream s, UpdateInventoryIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteArrayUInt32Length(v.InventoryProtobuf);
		}
	}

	// UpdateInventoryIIR (0xA74D2814).
	public sealed record UpdateInventoryIIR(uint Handle, byte Env, byte[] InventoryProtobuf);
}
