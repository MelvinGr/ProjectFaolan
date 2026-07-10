using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// TemplateActionIIR_t (0x6C300937). ReadSubClass (FUN_007a0b00) reads an item-template struct (FUN_005d5150,
	// 68 bytes on the wire) then six u32. The struct is item-identity data - a name/length region + item keys (the
	// B2 90 80 5C 29 E6 27 CA equip-identity pattern + TEA-key-like blocks), so it is carried verbatim; the six
	// trailing u32 are constructed. Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record TemplateActionCodec(uint Handle, byte Env, byte[] Template, uint F1, uint F2, uint F3, uint F4, uint F5, uint F6) : ICodec<TemplateActionCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.TemplateActionIIR;
		public const int TemplateBytes = 68;

		public static TemplateActionCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var template = s.ReadArray(TemplateBytes);
			return new TemplateActionCodec(handle, env, template,
				s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteArray(Template)
				.WriteUInt32(F1).WriteUInt32(F2).WriteUInt32(F3).WriteUInt32(F4).WriteUInt32(F5).WriteUInt32(F6));
		}

		public static TemplateActionCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
