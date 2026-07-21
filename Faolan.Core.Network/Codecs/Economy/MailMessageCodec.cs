using System;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// Parses/builds the DispatchMailMessage (S->C 0x2002) body - one in-game mail, optionally with the bought item
	// appended as an attachment. Decoded byte-exact 2026-06-13 from the necrol80 buy_from_autionhouse_get_mail capture
	// (629 B base #1604/#1611, 754 B with attachment #1607; docs/subsystems/tradepost-protocol.md). Wire grammar (integers BE):
	//   [mailId:u64]
	//   [recipientClass:u32][nameLen:u16][recipient ascii]
	//   [senderClass:u32][nameLen:u16][sender ascii]        # name EMPTY for an AH/system mail
	//   [xmlLen:u16][remoteformat XML]
	//   [footerWord:u32][money:u64][ts1:u32][ts2:u32]
	//   [attachCount:u32]
	//   attachCount x { [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer:u32 word,u32 class,u32 0] }
	// The attachment entry is the SAME ProtoInventoryGameEntry shape the snapshot / shop / AH-search / live-add use,
	// so the one item codec serves them all (the base mail just carries attachCount=0).
	public static class MailMessageCodec
	{
		public const uint StatQuantity = 26; // the attachment entry's only stat (matches the capture)

		// Builds the DispatchMailMessage body from a Mail row. `includeAttachments` mirrors retail: the base mail
		// (GetAllMails / poll) carries no items (attachCount=0); the client then fetches the item with GetMailItem,
		// and the server replies with the same mail PLUS the attachment appended (629 B -> 754 B in the capture).
		public static byte[] Build(Mail mail, bool includeAttachments)
		{
			var s = new FaolanStream();
			s.WriteUInt64(mail.MailId);

			s.WriteUInt32(mail.RecipientClass);
			s.WriteString(mail.RecipientName ?? "");
			s.WriteUInt32(mail.SenderClass);
			s.WriteString(mail.SenderName ?? "");

			s.WriteString(mail.BodyXml ?? "");

			s.WriteUInt32(mail.FooterWord);
			s.WriteUInt64(mail.Money);
			s.WriteUInt32(mail.Timestamp1);
			s.WriteUInt32(mail.Timestamp2);

			var attachments = includeAttachments && mail.Attachments != null
				? mail.Attachments
				: null;
			var count = attachments?.Count ?? 0;
			s.WriteUInt32(count);

			if (count > 0)
			{
				foreach (var a in attachments!)
				{
					s.WriteUInt64(a.ItemInstanceId);

					byte[] entryBytes;
					using (var ms = new MemoryStream())
					{
						Serializer.Serialize(ms, BuildEntry(a));
						entryBytes = ms.ToArray();
					}

					s.WriteArrayUInt32Length(entryBytes);

					s.WriteUInt32(a.TrailerWord);
					s.WriteUInt32(a.TrailerClass);
					s.WriteUInt32(a.TrailerTail);
				}
			}

			return s.ToArray();
		}

		// The attachment item as the wire ProtoInventoryGameEntry (item_id = the instance id, stat_list = {26: qty},
		// item_data = the instance's ProtoAcgItem). The item's TEA key is per-TEMPLATE and lives in Items, reached
		// through the Item nav (FK on Template) - Include it before building (Parse attaches a transient Item so a
		// decoded attachment round-trips without a DB). The Template/Level/ItemHash tail still comes off the row.
		public static ProtoInventoryGameEntry BuildEntry(MailAttachment a)
		{
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
				Stat = StatQuantity,
				Value = a.Quantity
			});
			return entry;
		}

		// Decodes a captured/served body back into a Mail row (+ attachments). The inverse of Build, so a captured
		// body round-trips byte-exact (the golden-test oracle, like TradepostListingCodec).
		public static Mail Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			var mail = new Mail
			{
				MailId = s.ReadUInt64(),
				RecipientClass = s.ReadUInt32(),
				RecipientName = s.ReadString(),
				SenderClass = s.ReadUInt32(),
				SenderName = s.ReadString(),
				BodyXml = s.ReadString(),
				FooterWord = s.ReadUInt32(),
				Money = s.ReadUInt64(),
				Timestamp1 = s.ReadUInt32(),
				Timestamp2 = s.ReadUInt32()
			};

			var count = s.ReadUInt32();
			for (var i = 0; i < count; i++)
			{
				var instanceId = s.ReadUInt64();
				var entryBytes = s.ReadArrayUInt32Length();
				using var ms = new MemoryStream(entryBytes);
				var entry = Serializer.Deserialize<ProtoInventoryGameEntry>(ms);
				var item = entry.ItemData ?? new ProtoAcgItem();
				var key = item.Key1 ?? new ProtoDecryptionKey();

				uint qty = 0;
				foreach (var stat in entry.StatLists)
				{
					if (stat.Stat == StatQuantity)
						qty = stat.Value;
				}

				mail.Attachments.Add(new MailAttachment
				{
					ItemInstanceId = instanceId,
					Quantity = qty,
					// The key is per-template and lives in Items; on a DB-backed attachment BuildEntry reads it through
					// the Item nav. Decoding here has no DB, so attach a TRANSIENT Item carrying just the key so the
					// decoded attachment round-trips (Build -> bytes). This Item is never tracked/saved - callers that
					// persist a mail set only the Template FK column (the row resolves its real Items row on reload).
					Item = new Item
					{
						Key = key
					},
					Template = item.Template1,
					ItemLevel = item.Level,
					ItemHash = item.Hash,
					TrailerWord = s.ReadUInt32(),
					TrailerClass = s.ReadUInt32(),
					TrailerTail = s.ReadUInt32()
				});
			}

			return mail;
		}
	}
}
