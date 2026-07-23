using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// UpdateInventoryIIR_t (0xA74D2814) - a live inventory delta. The slot-8 subclass reader is a stub because the
	// client parses the body as PROTOBUF, not fixed fields (docs/re/v1-iir-readers.md). Frame:
	//   [handle][env][u32 pbLength][protobuf = ProtoInventoryGame]
	// The protobuf is the same item schema as the snapshot - repeated ProtoAcgItem entries, each carrying 3x
	// ProtoDecryptionKey (the item's 4x fixed32 TEA keys). This codec constructs the frame and carries the item
	// protobuf verbatim (deserialize with ProtoInventoryGame); byte-exact against the captured bodies.
	public record UpdateInventoryCodec(uint Handle, byte Env, byte[] InventoryProtobuf) : ICodec<UpdateInventoryCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.UpdateInventoryIIR;

		public static UpdateInventoryCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var pbLength = s.ReadUInt32();
			return new UpdateInventoryCodec(handle, env, s.ReadArray(pbLength));
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteArrayUInt32Length(InventoryProtobuf));
		}

		public static UpdateInventoryCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
