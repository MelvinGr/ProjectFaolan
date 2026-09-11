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
	// (AccountId, Id) rather than stored. The "new claims available" glow (sub 0x0009) is a single per-account flag (Account.ClaimsAvailable):
	// true until the account first opens the list, then cleared - matching retail (glow only for unseen claims).
	[Index(nameof(AccountId))]
	public class AccountClaim : BaseEntity
	{
		public uint AccountId { get; set; }

		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; } = null!;

		[ForeignKey(nameof(ItemTemplateId))]
		public virtual Item Item { get; set; } = null!;

		// The item template granted on redeem (an Items row, same as ShopItem.ItemTemplateId).
		public uint ItemTemplateId { get; set; }

		[MaxLength(4)]
		public string Code { get; set; } = "";

		// Wire claim type: 6 = plain item claim; 4 = account-currency / point grant.
		public uint ClaimType { get; set; } = 6;

		// Max charges - the second tail word [remaining][MAX]. Null (wire 0) = infinite. For a finite claim it equals
		// Quantity when fresh (so the initial list has remaining == max) and stays fixed as Quantity decrements, so the
		// sub-0x05 redeem ack sends [remaining][max].
		public uint? MaxCharges { get; set; }

		// Remaining charges - the "Amount" column. Null = infinite (shown as ∞; the claim is not consumed on redeem, so
		// an ordinary item claim can be collected any number of times). A finite value N is a multi-charge claim
		// ("Double XP", 20) that delivers one per redeem and decrements until it hits 0, when the row is removed. On the
		// wire (the first of the tail pair [remaining][max]) infinite is 0.
		public uint? Quantity { get; set; }

		// The item's level as shown in the window (the u64 after the template on the wire). Not the stack count: the
		// capture's redeem delivered one item despite Level 5 (see ClaimEntry). Cosmetic server-side; sent verbatim.
		public ulong Level { get; set; }
	}
}
