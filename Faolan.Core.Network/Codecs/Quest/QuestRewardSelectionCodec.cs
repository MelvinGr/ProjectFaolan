using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestRewardSelectionIIR_c (0x80138D7B) - the client half of the pick-one-of-N quest reward exchange (#815): which
	// of the offered rewards the player chose. Decoded byte-exact from xxx_mage-necromancer_login_play3 frame 2910,
	// and complete - the body is 21 bytes with nothing unaccounted for.
	//
	//   [u32 sub][u32 0xC350 Character][u32 clientInstance][u8 env][u32 quest][u32 index]
	//
	// The index is 0-BASED into the list the QuestRewardNotifyIIR offer sent: the capture's pick of 2 against three
	// options was answered with template 3394679 "Acolyte's Staff", the third of them. See QuestRewardNotifyCodec for
	// the exchange in full.
	public static class QuestRewardSelectionCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestRewardSelectionIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestRewardSelectionIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint clientInstance, uint questId, uint index)
		{
			return Build(new QuestRewardSelectionIIR(clientInstance, 0x01, questId, index));
		}

		public static QuestRewardSelectionIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestRewardSelectionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestRewardSelectionIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestRewardSelectionIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var clientInstance = r.ReadU32Be();
			var env = r.ReadU8();
			return new QuestRewardSelectionIIR(clientInstance, env, r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, QuestRewardSelectionIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.ClientInstance)
				.WriteByte(v.Env)
				.WriteUInt32(v.QuestId)
				.WriteUInt32(v.Index);
		}
	}

	// QuestRewardSelectionIIR_c - the reward the player picked. Index is 0-based into the offered list.
	public sealed record QuestRewardSelectionIIR(uint ClientInstance, byte Env, uint QuestId, uint Index);
}
