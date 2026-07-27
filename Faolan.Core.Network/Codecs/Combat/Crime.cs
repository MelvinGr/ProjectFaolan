using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// CrimeIIR_c (0xC7954504). ReadSubClass (FUN_0148c9f0) reads a gate byte and, when set, a u32. Body observed as
	// [u8 flag=1][u32 value]. Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record Crime(uint Handle, byte Env, byte Flag, uint Value) : ICodec<Crime>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CrimeIIR;

		public static Crime Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // EntityClass.Character
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var flag = s.ReadByte();
			return new Crime(handle, env, flag, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteByte(Flag).WriteUInt32(Value));
		}

		public static Crime Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
