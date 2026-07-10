using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// LockedToChangedIIR_t (0x6B0AEC58) - a target-lock change (58 captured). ReadSubClass (FUN_007a0630) reads two
	// u32 = the locked-to entity ref [class][instance] (observed 0xC350 = Character + the target handle). Grammar
	// from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record LockedToChangedCodec(uint Handle, byte Env, uint TargetClass, uint TargetInstance) : ICodec<LockedToChangedCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LockedToChangedIIR;

		public static LockedToChangedCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var targetClass = s.ReadUInt32();
			return new LockedToChangedCodec(handle, env, targetClass, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteUInt32(TargetClass)
				.WriteUInt32(TargetInstance));
		}

		public static byte[] Build(uint handle, uint targetInstance, uint targetClass = EntityClass.Character, byte env = 0x00)
		{
			return new LockedToChangedCodec(handle, env, targetClass, targetInstance).ToArray();
		}

		public static LockedToChangedCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
