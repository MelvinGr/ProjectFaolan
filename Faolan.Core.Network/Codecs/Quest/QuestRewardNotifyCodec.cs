using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestRewardNotifyIIR_c (0x8BB2799C) - the server half of the pick-one-of-N quest reward exchange (#815), decoded
	// byte-exact from xxx_mage-necromancer_login_play3, quest 10928 "The Trainer of Mages" (frames 2864 and 2911).
	//
	// The OFFER and the CONFIRM are ONE grammar, not two: the option count word says which you are holding, exactly the
	// way the n3Teleport spawn blob's length does. An offer carries its options; the confirm carries none and echoes
	// the chosen index in the trailing block.
	//
	//   [u32 sub][u32 0xC350 Character][u32 clientInstance][u8 env]
	//   [u32 quest][u32 0][u32 count][count x QuestRewardEntry]
	//   [u32 t0][u32 t1][u32 t2][u32 t3][u32 t4]
	//
	// ⚠️ The five trailing words are NOT decoded and are carried verbatim [V0]. The offer sent (1,0,0,1,1) and the
	// confirm (0,0,0,2,0) - and 2 is the index the player picked, so t3 is *probably* the selection echo. That is one
	// observation of each message, so the codec preserves the block rather than modelling it; naming t3 "SelectedIndex"
	// off a single pair where the other value happens to be 1 would be inventing a field. What IS established is the
	// exchange, because the grant confirms it end to end:
	//
	//   329.0 f2864 s OFFER   quest 10928, 3 options: 27655 Dagger / 3145335 Longsword / 3394679 Staff
	//   335.8 f2910 c PICK    quest 10928, index 2                      (QuestRewardSelectionCodec)
	//   336.1 f2911 s CONFIRM quest 10928, no options, trailing t3 = 2
	//   336.1 f2911 s DispatchAddItem  slot 13, template 3394679 = "Acolyte's Staff"
	//
	// so the index is 0-BASED into the offered list. The offer arrives inside the quest-completion burst
	// ({resolvegoal} -> QuestGoalIIR -> QuestIIR state 4), not on a separate turn-in.
	public static class QuestRewardNotifyCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestRewardNotifyIIR;

		// The five undecoded trailing words an OFFER carries; see the type comment.
		public static readonly uint[] OfferTrailing = [1, 0, 0, 1, 1];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestRewardNotifyIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint clientInstance, uint questId, IReadOnlyList<QuestRewardEntry> options)
		{
			return Build(new QuestRewardNotifyIIR(clientInstance, 0x01, questId, options, OfferTrailing));
		}

		public static QuestRewardNotifyIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestRewardNotifyCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestRewardNotifyIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestRewardNotifyIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var clientInstance = r.ReadU32Be();
			var env = r.ReadU8();
			var questId = r.ReadU32Be();
			r.ExpectU32Be(0, "reward header word");

			var count = r.ReadU32Be();
			var options = new List<QuestRewardEntry>((int)count);
			for (var i = 0u; i < count; i++)
				options.Add(ReadOption(ref r));

			var trailing = new uint[QuestRewardNotifyIIR.TrailingWords];
			for (var i = 0; i < trailing.Length; i++)
				trailing[i] = r.ReadU32Be();

			return new QuestRewardNotifyIIR(clientInstance, env, questId, options, trailing);
		}

		internal static void Write(FaolanStream s, QuestRewardNotifyIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.ClientInstance)
				.WriteByte(v.Env)
				.WriteUInt32(v.QuestId)
				.WriteUInt32(0)
				.WriteUInt32(v.Options.Count);

			foreach (var option in v.Options)
				WriteOption(s, option);

			foreach (var word in v.Trailing)
				s.WriteUInt32(word);
		}

		private static QuestRewardEntry ReadOption(ref SpanReader r)
		{
			var template = r.ReadU32Be();
			r.ExpectU32Be(template, "the mirrored item-template id");
			var level = r.ReadU64Be();
			var code = r.ReadBytes(QuestRewardEntry.CodeLength).ToArray();
			var key = r.ReadBytes(QuestRewardEntry.KeyLength).ToArray();
			r.ReadBytes(QuestRewardEntry.KeyLength); // key (dup)
			var guid = r.ReadBytes(QuestRewardEntry.BatchGuidLength).ToArray();
			r.ReadBytes(QuestRewardEntry.BatchGuidLength); // guid (dup)
			return new QuestRewardEntry(template, level, code, key, guid, r.ReadU32Be());
		}

		private static void WriteOption(FaolanStream s, QuestRewardEntry option)
		{
			s.WriteUInt32(option.ItemTemplateId)
				.WriteUInt32(option.ItemTemplateId)
				.WriteUInt64(option.Level)
				.WriteArray(option.Code)
				.WriteArray(option.DecryptionKey)
				.WriteArray(option.DecryptionKey)
				.WriteArray(option.BatchGuid)
				.WriteArray(option.BatchGuid)
				.WriteUInt32(option.Trailing);
		}
	}

	// QuestRewardNotifyIIR_c - the reward offer (Options non-empty) or its confirm (Options empty). Trailing is the
	// five-word block the codec carries verbatim; see QuestRewardNotifyCodec for what is and is not known about it.
	public sealed record QuestRewardNotifyIIR(uint ClientInstance, byte Env, uint QuestId, IReadOnlyList<QuestRewardEntry> Options, IReadOnlyList<uint> Trailing)
	{
		public const int TrailingWords = 5;
	}
}
