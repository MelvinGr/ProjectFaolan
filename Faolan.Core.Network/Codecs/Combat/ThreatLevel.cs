using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ThreatLevelIIR_c (0x1745EF6C) - a threat/aggro level (u32) plus a bool (FUN_007a8fd0).
	public record ThreatLevel(uint Handle, byte Env, uint Threat, byte Flag) : ICodec<ThreatLevel>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ThreatLevelIIR;

		public static ThreatLevel Read(FaolanStream s)
		{
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var threat = s.ReadUInt32();
			return new ThreatLevel(handle, env, threat, s.ReadByte());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteUInt32(Threat).WriteByte(Flag));
		}

		public static byte[] Build(uint handle, uint threat, byte flag, byte env = 0x00)
		{
			return new ThreatLevel(handle, env, threat, flag).ToArray();
		}

		public static ThreatLevel Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
