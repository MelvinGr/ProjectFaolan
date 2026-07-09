using System.Numerics;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// StopIIR_c (0xD751EBB4) - the move-stop record: final position + a 4-float facing/orientation (FUN_00a34700,
	// 7 big-endian floats). The trailing rotation is a unit 2D heading embedded in 4 floats.
	public static class StopCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StopIIR;

		public static byte[] Build(uint handle, Vector3 position, float r0, float r1, float r2, float r3, byte env = 0x00)
		{
			var c = new FaolanStream();
			c.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteByte(env).WriteVector3(position).WriteFloat(r0).WriteFloat(r1).WriteFloat(r2).WriteFloat(r3);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32(); // len, subop, marker
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new Parsed(handle, env, s.ReadVector3(), s.ReadFloat(), s.ReadFloat(), s.ReadFloat(), s.ReadFloat());
		}

		public sealed record Parsed(uint Handle, byte Env, Vector3 Position, float R0, float R1, float R2, float R3);
	}
}
