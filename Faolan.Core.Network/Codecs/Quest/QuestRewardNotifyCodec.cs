using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     QuestRewardNotifyIIR_c - the server half of the pick-one-of-N quest reward exchange. Decoded byte-exact from
	///     xxx_mage-necromancer_login_play3, quest 10928 "The Trainer of Mages" (frames 2864 and 2911).
	/// </summary>
	/// <remarks>
	///     The offer and the confirm are one grammar: the option count word says which of the two you are holding. An offer
	///     carries its options; the confirm carries none and echoes the chosen index in the trailing block.
	///     <code>
	///     [u32 sub][u32 class][u32 clientInstance][u8 env]
	///     [u32 quest][u32 0][u32 count][count x QuestRewardEntry]
	///     [u32 t0][u32 t1][u32 t2][u32 t3][u32 t4]
	///     </code>
	///     <para>
	///         The five trailing words are not decoded and are carried verbatim. The offer sent (1,0,0,1,1) and the confirm
	///         (0,0,0,2,0), where 2 is the index the player picked, so t3 is probably the selection echo - but that is one
	///         observation of each message, so the codec preserves the block rather than modelling it. [V0]
	///     </para>
	///     <para>
	///         The exchange itself is established end to end by the grant that follows it: an offer of three options at
	///         f2864, the client's pick of index 2 at f2910 (<see cref="QuestRewardSelectionCodec" />), a confirm with no
	///         options and trailing t3 of 2 at f2911, and in the same frame a DispatchAddItem of template 3394679
	///         "Acolyte's Staff", the third option. The index is therefore 0-based into the offered list. The offer arrives
	///         inside the quest-completion burst rather than on a separate turn-in.
	///     </para>
	/// </remarks>
	public static class QuestRewardNotifyCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestRewardNotifyIIR;

		/// <summary>The five undecoded trailing words an offer carries.</summary>
		public static readonly uint[] OfferTrailing = [1, 0, 0, 1, 1];

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The QuestRewardNotifyIIR body: the reward offer when Options is non-empty, its confirm when empty. Trailing is
	///     the five-word block the codec carries verbatim; see <see cref="QuestRewardNotifyCodec" />.
	/// </summary>
	public sealed record QuestRewardNotifyIIR(uint ClientInstance, byte Env, uint QuestId, IReadOnlyList<QuestRewardEntry> Options, IReadOnlyList<uint> Trailing)
	{
		public const int TrailingWords = 5;
	}
}
