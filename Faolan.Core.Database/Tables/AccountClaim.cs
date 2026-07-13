using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One redeemable item claim on an account's Claims/Redeem window (docs/subsystems/item-claims.md). Account-scoped
	// (Age of Conan claims - item-shop purchases, veteran/pre-order rewards - apply to the whole account, not one
	// character; the on-wire batch guid is constant across a response), delivered to whichever character redeems it
	// via the CoreClaimIIR list (CoreClaimListCodec, sub 0x0001) and granted to the backpack on redeem (sub 0x0003).
	//
	// The wire row also carries a 16-byte opaque token and a per-account batch guid; both are display-only (redeem
	// targets the 1-based list position, never the token), so they are generated deterministically at send time from
	// (AccountId, Id) rather than stored. IsNew drives the "new claims available" glow (CoreClaimCodec, sub 0x0009):
	// true until the account first opens the list, then cleared - matching retail (glow only for unseen claims).
	[Index(nameof(AccountId))]
	public class AccountClaim : BaseEntity
	{
		public uint AccountId { get; set; }

		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; } = null!;

		// The item template granted on redeem (an Items row, same as ShopItem.ItemTemplateId).
		public uint ItemTemplateId { get; set; }

		[ForeignKey(nameof(ItemTemplateId))]
		public virtual Item Item { get; set; } = null!;

		// The item's LEVEL as shown in the window (the u64 after the template on the wire). NOT the stack count: the
		// capture's redeem delivered ONE item despite Level 5 (see ClaimEntry). Cosmetic server-side; sent verbatim.
		public ulong Level { get; set; } // TODO: Just use item.level?

		// Remaining charges - the "Amount" column. 0 = a single-use item claim (shown as ∞, delivered once then the
		// row is dropped). A multi-charge claim ("Double XP", 20) delivers ONE per redeem and decrements this until it
		// hits 0, when the row is removed. On the wire it is the first of the tail pair [remaining][max].
		public uint Quantity { get; set; }

		// Max charges - the second tail word [remaining][MAX]. Equals Quantity when the claim is fresh (so the initial
		// list has remaining == max); stays fixed as Quantity decrements, so the sub-0x05 redeem ack sends [remaining][max].
		public uint MaxCharges { get; set; }

		[MaxLength(4)]
		public string Code { get; set; } = "";

		// Wire claim type: 6 = plain item claim; 4 = account-currency / point grant.
		public uint ClaimType { get; set; } = 6; // TODO: make Enum

		// Whether this claim is still "new" (unseen) - lights the client's claims button until the list is opened.
		public bool IsNew { get; set; } = true;
	}
}
