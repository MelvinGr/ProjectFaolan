using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// RegenIIR_c (0x28D0AC99) - the highest-frequency V1 type (1045 captured). ReadSubClass (FUN_0079dea0) reads
	// three u32 into the entity; observed as [0][0][value], value tracking a health-like number. Grammar recovered
	// from the client reader (docs/re/v1-iir-readers.md); the FieldA/FieldB/Value split is byte-exact, the exact
	// vitals meaning (hp vs mana vs stamina regen) is the remaining semantic step.
	public record RegenCodec(uint Handle, byte Env, uint FieldA, uint FieldB, uint Value) : ICodec<RegenCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.RegenIIR;

		public static RegenCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var a = s.ReadUInt32();
			var b = s.ReadUInt32();
			return new RegenCodec(handle, env, a, b, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteUInt32(FieldA)
				.WriteUInt32(FieldB)
				.WriteUInt32(Value));
		}

		public static byte[] Build(uint handle, uint value, uint fieldA = 0, uint fieldB = 0, byte env = 0x00)
		{
			return new RegenCodec(handle, env, fieldA, fieldB, value).ToArray();
		}

		public static RegenCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
