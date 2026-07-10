using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ChainKillIIR_t (0x0EED7068) - a kill-chain/streak count (30 captured). ReadSubClass (FUN_0079c0b0) reads one
	// u32 (observed a small count). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record ChainKillCodec(uint Handle, byte Env, uint Count) : ICodec<ChainKillCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ChainKillIIR;

		public static ChainKillCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new ChainKillCodec(handle, env, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteUInt32(Count));
		}

		public static byte[] Build(uint handle, uint count, byte env = 0x00)
		{
			return new ChainKillCodec(handle, env, count).ToArray();
		}

		public static ChainKillCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
