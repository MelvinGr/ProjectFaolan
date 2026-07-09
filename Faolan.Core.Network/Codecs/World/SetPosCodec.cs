using System.Numerics;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// SetPosIIR_c (0x04B80B24) - the teleport/snap record: position(3) + rotation(4) floats + a trailing bool
	// (FUN_00a33770; the client finite-checks the pos & rot).
	public static class SetPosCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000Opcodes.SetPosIIR;

		public static byte[] Build(uint handle, Vector3 position, float r0, float r1, float r2, float r3, byte flag = 0x00, byte env = 0x00)
		{
			var c = new FaolanStream();
			c
				.WriteUInt32(SubOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteByte(env)
				.WriteVector3(position)
				.WriteFloat(r0)
				.WriteFloat(r1)
				.WriteFloat(r2)
				.WriteFloat(r3)
				.WriteByte(flag);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var pos = s.ReadVector3();
			var (r0, r1, r2, r3) = (s.ReadFloat(), s.ReadFloat(), s.ReadFloat(), s.ReadFloat());
			return new Parsed(handle, env, pos, r0, r1, r2, r3, s.ReadByte());
		}

		public sealed record Parsed(uint Handle, byte Env, Vector3 Position, float R0, float R1, float R2, float R3, byte Flag);
	}
}
