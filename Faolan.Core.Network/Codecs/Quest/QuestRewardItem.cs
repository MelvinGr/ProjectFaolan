using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One REWARD ITEM of a quest, as the `Quest_c` journal record carries it - sixteen u32 in one of the record's
	///     four reward lists (Gitea #464).
	/// </summary>
	/// <remarks>
	///     ⚠️ These lists were carried as <i>"16 raw u32s, unvalidated"</i> until the whole corpus was swept: they are
	///     empty for the beach starter, which is the one record the golden template covers, so nothing ever forced
	///     them open. Over the dumps they are populated on 7,347 elements across 3,741 journal records.
	///     <para>
	///         ⭐ <b>The item is an ACG item, exactly as the snapshot's inventory entries are.</b> Each element names
	///         the template TWICE - the <c>template1</c>/<c>template2</c> pair of <c>ProtoAcgItem</c> - each with its
	///         own per-template TEA key, and the two halves are identical on all 7,347 (the same "equal in the common
	///         case" the inventory entries show, 8,445 of 8,479).
	///     </para>
	///     <para>
	///         ⭐⭐ <b>The key is proven by DECRYPTION, not by shape</b> (`--report quest-rewards`): all 101 distinct
	///         templates open with the key the wire states beside them, each decrypting to a body that declares one of
	///         the catalog types and its own id - and all 101 FAIL with a bogus key, which is the control that makes
	///         the first number mean something.
	///     </para>
	/// </remarks>
	public sealed record QuestRewardItem
	{
		// The player's client instance - the same re-addressed word the @609 goal records carry, and 6 distinct
		// values over the corpus (one per captured character).
		public uint Instance { get; init; }

		// Echoes the owning record's own QuestId - 7,347 of 7,347.
		public uint QuestId { get; init; }

		// The RDB type of the template below: 1000620, the item-template catalog's RDBH variant, on every captured
		// element. Carried rather than assumed - the sibling 1000020 shares the id space.
		public uint TemplateType { get; init; }

		public uint TemplateId { get; init; }

		// The template's per-template TEA key - the same `Item.Data1..4` the item catalog is keyed on.
		public ProtoDecryptionKey Key { get; init; } = new ProtoDecryptionKey();

		// The ACG `template2` half: type, id and its own key. Identical to the first on every captured element, and
		// kept separate because ProtoAcgItem's two halves genuinely differ on some inventory entries.
		public uint TemplateType2 { get; init; }

		public uint TemplateId2 { get; init; }
		public ProtoDecryptionKey Key2 { get; init; } = new ProtoDecryptionKey();

		// The item's LEVEL - 80/1/20/60/70/21/25/26/23/5 across the corpus, the ACG level the reward rolls at.
		//
		// ⭐ Named from what it is ATTACHED to rather than from its values: it is constant per TEMPLATE on 101 of
		// 101, so it is a property of the item. (Values in that range would equally fit a quest level, which is why
		// the attachment is the test and not the range.)
		public uint Level { get; init; }

		// How many of it - 1 on the majority, up to 25.
		//
		// ⭐ Same test, one level down: constant per (quest, template) on 161 of 161 but NOT per template, so it is a
		// property of the REWARD rather than of the item.
		public uint Quantity { get; init; }
	}

	// Composed inside QuestcRecordCodec's four reward lists; never sent alone, so it declares no opcode.
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
