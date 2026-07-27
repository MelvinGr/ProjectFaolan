using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ChainKillIIR_t (0x0EED7068) - a kill-chain/streak count (30 captured). ReadSubClass (FUN_0079c0b0) reads one
	// u32 (observed a small count). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record ChainKill(uint Handle, byte Env, uint Count) : ICodec<ChainKill>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ChainKillIIR;

		public static ChainKill Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // EntityClass.Character
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new ChainKill(handle, env, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteUInt32(Count));
		}

		public static byte[] Build(uint handle, uint count, byte env = 0x00)
		{
			return new ChainKill(handle, env, count).ToArray();
		}

		public static ChainKill Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
