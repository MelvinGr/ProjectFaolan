using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// HateListUpdateIIR (0x8A01E5A1) - a hate/aggro entry: a flag byte then three u32s (FUN_007a93a0); in the capture
	// the trio is [0xC350 marker][target handle][count]. The carrier handle is the hater (the NPC); Target is who it
	// hates.
	public static class HateListCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000Opcodes.HateListUpdateIIR;

		public static byte[] Build(uint handle, uint target, uint count = 1, byte flag = 0x00, byte env = 0x00)
		{
			return new Message(handle, env, flag, EntityClass.Character, target, count).ToArray();
		}

		public static Message Parse(byte[] body)
		{
			return Message.Read(new FaolanStream(body));
		}

		public sealed record Message(uint Handle, byte Env, byte Flag, uint Marker, uint Target, uint Count) : ICodec<Message>
		{
			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32();
				s.ReadUInt32();
				s.ReadUInt32();
				var handle = s.ReadUInt32();
				var env = s.ReadByte();
				var flag = s.ReadByte();
				return new Message(handle, env, flag, s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32());
			}

			public void Write(FaolanStream s)
			{
				s.WriteArrayUInt32Length(body => body
					.WriteUInt32(SubOpcode)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(Handle)
					.WriteByte(Env)
					.WriteByte(Flag)
					.WriteUInt32(Marker)
					.WriteUInt32(Target)
					.WriteUInt32(Count));
			}
		}
	}
}
