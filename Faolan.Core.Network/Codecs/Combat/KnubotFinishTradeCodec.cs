using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// KnubotFinishTradeIIR_c (0x7446F3FA) - the vendor/NPC trade-close. ReadSubClass (FUN_009a7c20) runs the Knubot
	// base prefix (FUN_009a7e00, a u16 streaming version) then a target entity ref + trailer. Body observed as
	// [u16 version][u32 targetClass = 0xC350][u32 targetInstance][u64][u32]. Grammar from the client reader
	// (docs/re/v1-iir-readers.md), byte-exact.
	public record KnubotFinishTradeCodec(uint Handle, byte Env, ushort Version, uint TargetClass, uint TargetInstance, ulong Trailer64, uint Trailer32) : ICodec<KnubotFinishTradeCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.KnubotFinishTradeIIR;

		public static KnubotFinishTradeCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var version = s.ReadUInt16();
			var targetClass = s.ReadUInt32();
			var targetInstance = s.ReadUInt32();
			var trailer64 = s.ReadUInt64();
			return new KnubotFinishTradeCodec(handle, env, version, targetClass, targetInstance, trailer64, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteUInt16(Version)
				.WriteUInt32(TargetClass)
				.WriteUInt32(TargetInstance)
				.WriteUInt64(Trailer64)
				.WriteUInt32(Trailer32));
		}

		public static KnubotFinishTradeCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
