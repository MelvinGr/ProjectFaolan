using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One in-game mail (the Tradepost agent carries mail alongside the auction house). Decoded byte-exact from the
	// necrol80 buy_from_autionhouse_get_mail capture (docs/subsystems/tradepost-protocol.md "Buy -> mail delivery"); the on-wire
	// DispatchMailMessage (S->C 0x2002) is built byte-exact by MailMessageCodec. An auction-house delivery is a
	// SYSTEM mail: the recipient is the buyer, the sender NAME is empty. The bought item rides as an attachment
	// (MailAttachment) fetched on demand. Keyed by MailId (the wire id the client's GetMailByID/GetMailItem target).
	[Index(nameof(MailId), IsUnique = true), Index(nameof(RecipientName))]
	public class Mail : BaseEntity
	{
		// The realm this mail belongs to (#324) - the recipient's realm; no cross-realm mail, so a realm process only
		// reads its own inbox rows. Backfilled from the recipient character on migration.
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; }

		// The unique wire mail id (the client's GetMailByID / GetMailItem target).
		public ulong MailId { get; set; }

		public virtual ICollection<MailAttachment> Attachments { get; set; } = new List<MailAttachment>();

		// DB-only: whether the recipient has read the mail (MarkMailRead). Not on this body.
		public bool IsRead { get; set; }

		// The mail body, a remoteformat XML string (length-prefixed u16 on the wire). For an AH delivery it is the
		// TradePostGUI_MailItemBought token with the buyer + item parameters.
		[MaxLength(4096)]
		public string BodyXml { get; set; }

		[MaxLength(64)]
		public string RecipientName { get; set; }

		[MaxLength(64)]
		public string SenderName { get; set; }

		// --- footer scalars (preserved verbatim; semantics partly loose, like ShopItem.Stat356) ----------------
		// A constant word (0x0B in the capture) before the money/timestamps.
		public uint FooterWord { get; set; }

		// Recipient endpoint = [class:u32][nameLen:u16][name]. Class is the constant 0x08006F31 in the capture
		// (the TradepostInterface object id), name = the buyer.
		public uint RecipientClass { get; set; }

		// DB-only: for a PLAYER-composed mail with an attachment, the sender's origin BANK slot. The attached item
		// stays in the sender's bank with a "mail-pending" overlay until the recipient takes it (then it leaves the
		// sender's bank) or the sender cancels (overlay cleared). 0 / no attachment for AH system mail (escrowed).
		public uint SenderBankSlot { get; set; }

		// Sender endpoint, same shape. For an AH/system mail the NAME is empty (the class constant is still present).
		public uint SenderClass { get; set; }

		// Two timestamp-like u32s (sent / expiry, undecoded exact meaning - preserved).
		public uint Timestamp1 { get; set; }

		public uint Timestamp2 { get; set; }

		// Attached cash (COD) in the wallet wire unit (tin). u64 on the wire; 0 in the capture.
		public ulong Money { get; set; }
	}
}
