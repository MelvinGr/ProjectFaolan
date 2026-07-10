using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// FeedbackIIR_t (0xBA1FD222) - NOT the FormatFeedback FIFO banner (0x8D5A8B6B); a plain feedback record.
	// ReadSubClass (FUN_0079f8e0) reads three u32 then keys off the env byte. Body observed as
	// [u32 a=0][u32 value][u32 source-handle][u8 flag]. Grammar from the client reader (docs/re/v1-iir-readers.md),
	// byte-exact.
	public record FeedbackIirCodec(uint Handle, byte Env, uint FieldA, uint Value, uint Source, byte Flag) : ICodec<FeedbackIirCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FeedbackIIR;

		public static FeedbackIirCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var a = s.ReadUInt32();
			var value = s.ReadUInt32();
			var source = s.ReadUInt32();
			return new FeedbackIirCodec(handle, env, a, value, source, s.ReadByte());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(Handle)
				.WriteByte(Env)
				.WriteUInt32(FieldA)
				.WriteUInt32(Value)
				.WriteUInt32(Source)
				.WriteByte(Flag));
		}

		public static FeedbackIirCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
