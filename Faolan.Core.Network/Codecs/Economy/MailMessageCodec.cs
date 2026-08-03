using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchMailMessage (S->C 0x2002) - one in-game mail, optionally with the bought item appended as an attachment.
	// Decoded byte-exact 2026-06-13 from the NecroL80 buy_from_autionhouse_get_mail capture (629 B base #1604/#1611,
	// 754 B with attachment #1607; docs/subsystems/tradepost-protocol.md). Wire grammar (integers BE):
	//   [mailId:u64]
	//   [recipientClass:u32][nameLen:u16][recipient ascii]
	//   [senderClass:u32][nameLen:u16][sender ascii]        # name EMPTY for an AH/system mail
	//   [xmlLen:u16][remoteformat XML]
	//   [footerWord:u32][money:u64][ts1:u32][ts2:u32]
	//   [attachCount:u32]
	//   attachCount x { [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer:u32 word,u32 class,u32 0] }
	// The attachment entry is the SAME ProtoInventoryGameEntry shape the snapshot / shop / AH-search / live-add use,
	// so the one item codec serves them all (the base mail just carries attachCount=0).
	//
	// `Parse` used to return the `Mail` DB ROW, which meant the codec's value was an EF entity and a decoded attachment
	// had to be handed a transient `Item` just so it could be re-encoded. The value is now a plain record and the row
	// mapping is `MailColumns` next door (#530 wave 7 / #533).
	public static class MailMessageCodec
	{
		public const uint Method = (uint)TradepostInterfaceMethods.DispatchMailMessage;

		// The attachment entry's only stat (matches the capture).
		public const uint StatQuantity = 26;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(MailMessage value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		// A stored mail. `includeAttachments` mirrors retail: the base mail (GetAllMails / poll) carries no items
		// (attachCount=0); the client then fetches the item with GetMailItem and the server replies with the same mail
		// PLUS the attachment appended (629 B -> 754 B in the capture).
		public static byte[] Build(Mail mail, bool includeAttachments)
		{
			return Build(MailColumns.ToMessage(mail, includeAttachments));
		}

		public static MailMessage Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MailMessageCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static MailMessage Read(ref SpanReader r)
		{
			var mailId = r.ReadU64Be();
			var recipientClass = r.ReadU32Be();
			var recipient = r.ReadStringU16Length();
			var senderClass = r.ReadU32Be();
			var sender = r.ReadStringU16Length();
			var bodyXml = r.ReadStringU16Length();
			var footerWord = r.ReadU32Be();
			var money = r.ReadU64Be();
			var ts1 = r.ReadU32Be();
			var ts2 = r.ReadU32Be();

			var count = r.ReadU32Be();
			var attachments = new List<MailAttachmentEntry>((int)count);
			for (var i = 0; i < count; i++)
			{
				var instanceId = r.ReadU64Be();
				var entry = r.ReadProtobufU32Length<ProtoInventoryGameEntry>();
				attachments.Add(new MailAttachmentEntry(instanceId, entry, r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be()));
			}

			return new MailMessage(mailId, recipientClass, recipient, senderClass, sender, bodyXml, footerWord, money, ts1, ts2, attachments);
		}

		internal static void Write(FaolanStream s, MailMessage v)
		{
			s
				.WriteUInt64(v.MailId)
				.WriteUInt32(v.RecipientClass)
				.WriteString(v.RecipientName)
				.WriteUInt32(v.SenderClass)
				.WriteString(v.SenderName)
				.WriteString(v.BodyXml)
				.WriteUInt32(v.FooterWord)
				.WriteUInt64(v.Money)
				.WriteUInt32(v.Timestamp1)
				.WriteUInt32(v.Timestamp2)
				.WriteUInt32(v.Attachments.Count);

			foreach (var a in v.Attachments)
			{
				s
					.WriteUInt64(a.ItemInstanceId)
					.WriteArrayUInt32Length(a.Entry.ToProtobuf())
					.WriteUInt32(a.TrailerWord)
					.WriteUInt32(a.TrailerClass)
					.WriteUInt32(a.TrailerTail);
			}
		}
	}

	// DispatchMailMessage (0x2002).
	public sealed record MailMessage(
		ulong MailId,
		uint RecipientClass,
		string RecipientName,
		uint SenderClass,
		string SenderName,
		string BodyXml,
		uint FooterWord,
		ulong Money,
		uint Timestamp1,
		uint Timestamp2,
		IReadOnlyList<MailAttachmentEntry> Attachments);

	// One attached item: the escrowed instance id, the wire entry, and the three trailing words the capture carries.
	public sealed record MailAttachmentEntry(ulong ItemInstanceId, ProtoInventoryGameEntry Entry, uint TrailerWord, uint TrailerClass, uint TrailerTail);

	// The `Mail` / `MailAttachment` row <-> `MailMessage` mapping. A MAPPER, not a codec (#533): the wire has one item
	// shape while the DB splits it across the attachment row and the per-TEMPLATE `Items` row that holds the TEA key.
	public static class MailColumns
	{
		// The AH-buy system mail's BODY. The listener should not know that a TradePostGUI_MailItemBought template
		// exists - it asks for "the item-bought body" and the mail layer composes it (#539). `itemNameEscaped` is left
		// empty for a live buy: the per-item RDB name key is unsynthesizable (see TradepostMailBodyCodec), so the item
		// still delivers and only its NAME in the mail text degrades.
		public static string ItemBoughtBody(string buyer, ulong saleId, string seller, string priceDisplay, string itemNameEscaped = "")
		{
			return TradepostMailBodyCodec.Build(new TradepostMailBody(buyer, saleId, itemNameEscaped, seller, priceDisplay));
		}

		public static MailMessage ToMessage(Mail mail, bool includeAttachments)
		{
			ArgumentNullException.ThrowIfNull(mail);

			var attachments = includeAttachments && mail.Attachments != null
				? mail.Attachments.Select(ToEntry).ToList()
				: [];

			return new MailMessage(mail.MailId, mail.RecipientClass, mail.RecipientName ?? "", mail.SenderClass, mail.SenderName ?? "", mail.BodyXml ?? "", mail.FooterWord, mail.Money,
				mail.Timestamp1, mail.Timestamp2, attachments);
		}

		// The attachment as the wire ProtoInventoryGameEntry (item_id = the instance id, stat_list = {26: qty},
		// item_data = the instance's ProtoAcgItem). The item's TEA key is per-TEMPLATE and lives in Items, reached
		// through the Item nav (FK on Template) - Include it before building. The Template/Level/ItemHash tail still
		// comes off the row.
		public static MailAttachmentEntry ToEntry(MailAttachment a)
		{
			ArgumentNullException.ThrowIfNull(a);
			var key = (a.Item ?? throw new InvalidOperationException($"MailAttachment (template {a.Template}): the Item navigation is not loaded - Include it before building the entry.")).Key;

			var entry = new ProtoInventoryGameEntry
			{
				ItemId = a.ItemInstanceId,
				ItemData = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = a.Template,
					Template2 = a.Template,
					Level = a.ItemLevel,
					Hash = a.ItemHash
				}
			};
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = MailMessageCodec.StatQuantity,
				Value = a.Quantity
			});

			return new MailAttachmentEntry(a.ItemInstanceId, entry, a.TrailerWord, a.TrailerClass, a.TrailerTail);
		}

		// A decoded mail back into rows - the importer/oracle direction. The per-template key is attached as a
		// TRANSIENT `Item` so a decoded attachment round-trips through ToEntry without a DB; it is never tracked or
		// saved (callers that persist a mail set only the Template FK column, and the row resolves its real Items row
		// on reload).
		public static Mail ToRow(MailMessage message)
		{
			ArgumentNullException.ThrowIfNull(message);
			var mail = new Mail
			{
				MailId = message.MailId,
				RecipientClass = message.RecipientClass,
				RecipientName = message.RecipientName,
				SenderClass = message.SenderClass,
				SenderName = message.SenderName,
				BodyXml = message.BodyXml,
				FooterWord = message.FooterWord,
				Money = message.Money,
				Timestamp1 = message.Timestamp1,
				Timestamp2 = message.Timestamp2
			};

			foreach (var a in message.Attachments)
				mail.Attachments.Add(ToRow(a));

			return mail;
		}

		public static MailAttachment ToRow(MailAttachmentEntry a)
		{
			ArgumentNullException.ThrowIfNull(a);
			var item = a.Entry.ItemData ?? new ProtoAcgItem();
			var key = item.Key1 ?? new ProtoDecryptionKey();

			uint qty = 0;
			foreach (var stat in a.Entry.StatLists)
			{
				if (stat.Stat == MailMessageCodec.StatQuantity)
					qty = stat.Value;
			}

			return new MailAttachment
			{
				ItemInstanceId = a.ItemInstanceId,
				Quantity = qty,
				Item = new Item
				{
					Key = key
				},
				Template = item.Template1,
				ItemLevel = item.Level,
				ItemHash = item.Hash,
				TrailerWord = a.TrailerWord,
				TrailerClass = a.TrailerClass,
				TrailerTail = a.TrailerTail
			};
		}
	}
}
