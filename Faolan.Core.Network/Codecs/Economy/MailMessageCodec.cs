using System.IO;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs
{
	// Parses/builds the DispatchMailMessage (S->C 0x2002) body - one in-game mail, optionally with the bought item
	// appended as an attachment. Decoded byte-exact 2026-06-13 from the necrol80 buy_from_autionhouse_get_mail capture
	// (629 B base #1604/#1611, 754 B with attachment #1607; docs/tradepost-protocol.md). Wire grammar (integers BE):
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
			s.WriteArrayUInt16Length(Encoding.ASCII.GetBytes(mail.RecipientName ?? ""));
			s.WriteUInt32(mail.SenderClass);
			s.WriteArrayUInt16Length(Encoding.ASCII.GetBytes(mail.SenderName ?? ""));

			s.WriteArrayUInt16Length(Encoding.ASCII.GetBytes(mail.BodyXml ?? ""));

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
				foreach (var a in attachments)
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
		// item_data = the instance's ProtoAcgItem). The Data1..4 quad describes the concrete item, so it comes off
		// the attachment row, not the Items template table (same convention as TradepostListingCodec.BuildEntry).
		public static ProtoInventoryGameEntry BuildEntry(MailAttachment a)
		{
			var key = new ProtoDecryptionKey
			{
				Key1 = a.Data1,
				Key2 = a.Data2,
				Key3 = a.Data3,
				Key4 = a.Data4
			};
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
				RecipientName = Encoding.ASCII.GetString(s.ReadArrayUInt16Length()),
				SenderClass = s.ReadUInt32(),
				SenderName = Encoding.ASCII.GetString(s.ReadArrayUInt16Length()),
				BodyXml = Encoding.ASCII.GetString(s.ReadArrayUInt16Length()),
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
					Data1 = key.Key1,
					Data2 = key.Key2,
					Data3 = key.Key3,
					Data4 = key.Key4,
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
