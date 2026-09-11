using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One item attached to a Mail. On the wire the attachment block appends to DispatchMailMessage after the footer:
	//   [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer:12 bytes]
	// The entry is the same ProtoInventoryGameEntry the snapshot / shop / AH-search / live-add use (item_id =
	// itemInstanceId, stat_list = {26: qty}, item_data = the item's ProtoAcgItem). Identity fields mirror
	// TradepostListing (the AH delivers the listed instance via mail). Decoded from the NecroL80 get-mail capture
	// (packet 1607, 754 B vs the 629 B no-attachment base); MailMessageCodec builds it byte-exact.
	//
	// Keyed naturally on (MailId, ItemInstanceId), not by a store-generated surrogate. The model conventions
	// forbid one on a table the importer delete+reinserts, and the pair is the wire's own identity for the block - the
	// entry repeats `ItemInstanceId` as its `ProtoInventoryGameEntry.ItemId`, and one mail cannot carry the same
	// instance twice.
	[PrimaryKey(nameof(MailId), nameof(ItemInstanceId))]
	public class MailAttachment
	{
		// --- the item instance identity (the entry's ProtoAcgItem)
		// The item's TEA key is per-template and lives once in Items; this attachment references it by Template (hard
		// FK to Items) and the codec reads Key1..4 through the Item nav. ItemHash stays a column: on the wire the
		// mail/AH entry carries the captured hash (0 for AH-delivered items), which is not the template's Items.Hash.
		[ForeignKey(nameof(Template))]
		public virtual Item Item { get; set; } = null!;

		// The item instance id (repeated as the entry's ProtoInventoryGameEntry.ItemId). Kept as ItemInstanceId, not
		// ItemId: it is the per-instance id, distinct from the Template FK / the Item nav on this same row, so the
		// longer name is the clearer one.
		public ulong ItemInstanceId { get; set; }

		// The actual item row this attachment escrows, when there is one.
		//
		// Without it a mail destroys per-instance state: an attachment carrying only a template id makes the take mint
		// a fresh item, losing the crafter and every socketed gem. With it, a take hands the row itself over.
		//
		// Nullable, and the two cases are real:
		//   * a purchase mail from an importer-seeded listing has no backing row (those listings have no seller
		//     character and never had an item), so its take still mints from the template as before;
		//   * every mail a player causes carries one.
		//
		// The Template/Quantity/ItemHash columns beside it are the wire fields and stay: they are what the mail entry
		// carries, they are the only source when Escrowed is null, and `ItemHash` is genuinely not the template's (an
		// AH-delivered item sends 0). Where Escrowed is set it is the authority and those columns mirror it.
		//
		// `ItemLevel` is not among them any more: the level is per-template - no template in 8,644 captured
		// sightings carries two - so it is read through the `Item` nav rather than copied per attachment.
		public uint? EscrowedInstanceId { get; set; }

		[ForeignKey(nameof(EscrowedInstanceId))]
		public virtual ItemInstance? Escrowed { get; set; }

		// FK -> Mail.MailId, the wire id, which is Mail's primary key. Same name, same number, same type as the
		// mail's own id - deliberately, see the Mail class comment.
		public ulong MailId { get; set; }

		[ForeignKey(nameof(MailId))]
		public virtual Mail Mail { get; set; } = null!;

		public uint ItemHash { get; set; }

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
