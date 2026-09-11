using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One reward item of a quest, as the Quest_c journal record carries it: sixteen u32 in one of the record's four
	///     reward lists.
	/// </summary>
	/// <remarks>
	///     The lists are empty for the beach starter, which is the one record the golden template covers; over the dumps
	///     they are populated on 7,347 elements across 3,741 journal records.
	///     <para>
	///         The item is an ACG item, exactly as the snapshot's inventory entries are. Each element names the template
	///         twice - the <c>template1</c>/<c>template2</c> pair of <c>ProtoAcgItem</c> - each with its own per-template
	///         TEA key, and the two halves are identical on all 7,347, the same "equal in the common case" the inventory
	///         entries show (8,445 of 8,479).
	///     </para>
	///     <para>
	///         The key is proven by decryption rather than by shape (<c>--report quest-rewards</c>): all 101 distinct
	///         templates open with the key the wire states beside them, each decrypting to a body that declares one of the
	///         catalog types and its own id, and all 101 fail with a bogus key, which is the control that makes the first
	///         number mean something.
	///     </para>
	/// </remarks>
	public sealed record QuestRewardItem
	{
		/// <summary>
		///     The player's client instance - the same re-addressed word the @609 goal records carry, and six distinct values
		///     over the corpus, one per captured character.
		/// </summary>
		public uint Instance { get; init; }

		/// <summary>Echoes the owning record's own QuestId, on all 7,347 captured elements.</summary>
		public uint QuestId { get; init; }

		/// <summary>
		///     The RDB type of the template below: 1000620, the item-template catalog's RDBH variant, on every captured
		///     element. Carried rather than assumed, since the sibling 1000020 shares the id space.
		/// </summary>
		public uint TemplateType { get; init; }

		public uint TemplateId { get; init; }

		/// <summary>The template's per-template TEA key - the same Item.Data1..4 the item catalog is keyed on.</summary>
		public ProtoDecryptionKey Key { get; init; } = new ProtoDecryptionKey();

		/// <summary>
		///     The ACG <c>template2</c> half: type, id and its own key. Identical to the first on every captured element, and
		///     kept separate because ProtoAcgItem's two halves genuinely differ on some inventory entries.
		/// </summary>
		public uint TemplateType2 { get; init; }

		public uint TemplateId2 { get; init; }
		public ProtoDecryptionKey Key2 { get; init; } = new ProtoDecryptionKey();

		/// <summary>
		///     The item's level - the ACG level the reward rolls at; 80, 1, 20, 60, 70, 21, 25, 26, 23 and 5 across the
		///     corpus. It is named from what it is attached to rather than from its values: constant per template on 101 of
		///     101, so it is a property of the item. Values in that range would equally fit a quest level, which is why the
		///     attachment is the test.
		/// </summary>
		public uint Level { get; init; }

		/// <summary>
		///     How many of the item - 1 on the majority, up to 25. By the same attachment test one level down it is constant
		///     per (quest, template) on 161 of 161 but not per template, so it is a property of the reward rather than of the
		///     item.
		/// </summary>
		public uint Quantity { get; init; }
	}

	/// <summary>
	///     Composed inside <see cref="QuestcRecordCodec" />'s four reward lists; never sent alone, so it declares no opcode.
	/// </summary>
	public static class QuestRewardItemCodec
	{
		internal static QuestRewardItem Read(ref SpanReader r)
		{
			return new QuestRewardItem
			{
				Instance = r.ReadU32Be(),
				QuestId = r.ReadU32Be(),
				TemplateType = r.ReadU32Be(),
				TemplateId = r.ReadU32Be(),
				Key = ReadKey(ref r),
				TemplateType2 = r.ReadU32Be(),
				TemplateId2 = r.ReadU32Be(),
				Key2 = ReadKey(ref r),
				Level = r.ReadU32Be(),
				Quantity = r.ReadU32Be()
			};
		}

		internal static void Write(FaolanStream s, QuestRewardItem v)
		{
			s.WriteUInt32(v.Instance);
			s.WriteUInt32(v.QuestId);
			s.WriteUInt32(v.TemplateType);
			s.WriteUInt32(v.TemplateId);
			WriteKey(s, v.Key);
			s.WriteUInt32(v.TemplateType2);
			s.WriteUInt32(v.TemplateId2);
			WriteKey(s, v.Key2);
			s.WriteUInt32(v.Level);
			s.WriteUInt32(v.Quantity);
		}

		private static ProtoDecryptionKey ReadKey(ref SpanReader r)
		{
			return new ProtoDecryptionKey
			{
				Key1 = r.ReadU32Be(),
				Key2 = r.ReadU32Be(),
				Key3 = r.ReadU32Be(),
				Key4 = r.ReadU32Be()
			};
		}

		private static void WriteKey(FaolanStream s, ProtoDecryptionKey key)
		{
			s.WriteUInt32(key.Key1);
			s.WriteUInt32(key.Key2);
			s.WriteUInt32(key.Key3);
			s.WriteUInt32(key.Key4);
		}
	}
}
