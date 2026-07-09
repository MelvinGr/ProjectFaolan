using System;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// StatDamageIIR_t (0xE9F24184) - the buff/debuff stat effect on the target (FUN_008d57c0 + the FUN_00897b60 tail).
	// The 17-u32 long form is DECODED: it is the SAME record CombatEffectBuilder.BuildStatDamage field-names and the
	// combat FSM sends at runtime -
	//   [statValue][0][0][0][0x1b][2][i32 delta][statId][0xC350 marker][caster][idx][0][spellBaseId][1][0][0][0]  (#274)
	// This codec keeps them as raw words ONLY for its own byte-exact round-trip test; the runtime path constructs from
	// the named fields via CombatEffectBuilder, so nothing ships verbatim here.
	public static class StatDamageCodec
	{
		public const int WordCount = 17;
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StatDamageIIR;

		public static byte[] Build(uint handle, uint[] words, byte env = 0x00)
		{
			if (words.Length != WordCount)
				throw new ArgumentException($"StatDamage long form is {WordCount} words", nameof(words));

			return new Message(handle, env, words).ToArray();
		}

		public static Message Parse(byte[] body)
		{
			return Message.Read(new FaolanStream(body));
		}

		public sealed record Message(uint Handle, byte Env, uint[] Words) : ICodec<Message>
		{
			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32();
				s.ReadUInt32();
				s.ReadUInt32();
				var handle = s.ReadUInt32();
				var env = s.ReadByte();
				var words = new uint[WordCount];
				for (var i = 0; i < WordCount; i++)
					words[i] = s.ReadUInt32();
				return new Message(handle, env, words);
			}

			public void Write(FaolanStream s)
			{
				s.WriteArrayUInt32Length(c =>
				{
					c.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env);
					foreach (var w in Words)
						c.WriteUInt32(w);
				});
			}
		}
	}
}
