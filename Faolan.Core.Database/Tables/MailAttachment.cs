using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One item attached to a Mail. On the wire the attachment block appends to DispatchMailMessage after the footer:
	//   [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer:12 bytes]
	// The entry is the SAME ProtoInventoryGameEntry the snapshot / shop / AH-search / live-add use (item_id =
	// itemInstanceId, stat_list = {26: qty}, item_data = the item's ProtoAcgItem). Identity fields mirror
	// TradepostListing (the AH delivers the listed instance via mail). Decoded from the necrol80 get-mail capture
	// (#1607, 754 B vs the 629 B no-attachment base); MailMessageCodec builds it byte-exact.
	public class MailAttachment : BaseEntity
	{
		// The item instance id, repeated as the entry's ProtoInventoryGameEntry.ItemId.
		public ulong ItemInstanceId { get; set; }

		public uint MailId { get; set; } // FK -> Mail.Id (the row key, not the wire MailId)

		[ForeignKey(nameof(MailId))]
		public virtual Mail Mail { get; set; }

		// --- the item INSTANCE identity (the entry's ProtoAcgItem) -------------------------------------------
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }
		public uint ItemHash { get; set; }
		public uint ItemLevel { get; set; }

		public uint Quantity { get; set; } // entry stat 26
		public uint Template { get; set; }
		public uint TrailerClass { get; set; }
		public uint TrailerTail { get; set; }

		// --- the per-attachment trailer (preserved verbatim) ------------------------------------------------
		// 12 bytes after the entry in the capture: [u32 0x72][u32 0x08006F31][u32 0]. The 0x08006F31 word matches
		// the recipient/sender endpoint class constant; semantics loose, kept verbatim per the magic-constant rule.
		public uint TrailerWord { get; set; }
	}
}
