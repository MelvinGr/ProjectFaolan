using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One auction-house (Tradepost) listing - a row in the DispatchTradepostSearchResults grid (S→C 0x2005).
	// Decoded from the necrol80 auctionhouse capture (Captures/scripts/decode_tradepost.py, docs/tradepost-protocol.md);
	// the on-the-wire row is built byte-exact by TradepostListingCodec. The listing carries a concrete item
	// INSTANCE (its own Data1..4 quad + template), not a template reference - the AH sells the specific item a
	// seller posted, so the identity lives here, not via the Items table (the row's ItemId search key and the
	// instance's Template can differ). Keyed by SaleId (the unique listing id the BuyItem targets); SellerName is
	// the stable seller identity (handles are session-volatile).
	[Index(nameof(SaleId), IsUnique = true)]
	[Index(nameof(ItemId))]
	public class TradepostListing : BaseEntity
	{
		// The unique listing id; the buy targets this, and it doubles as the entry's ProtoInventoryGameEntry.ItemId.
		public ulong SaleId { get; set; }

		// The listing's entity handle on the wire (session-volatile; kept for byte-exact rebuilds).
		public uint Handle { get; set; }

		[MaxLength(64)]
		public string SellerName { get; set; }

		// The seller's origin BANK slot the item was listed from. The seller addresses their own listing by this
		// slot (TradepostChangeItemPrice / TradepostCancelSellItem carry nBankItemSlot, not the saleId), and a cancel
		// returns the escrowed item to it. 0 for importer-seeded listings (not player-managed). Keyed with SellerName.
		public uint BankSlot { get; set; }

		// Asking price in the wallet wire unit (tin). u64 on the wire; also mirrored into entry stat 258.
		public ulong Price { get; set; }

		public uint Quantity { get; set; }

		// The catalog item (template id) the listing is filed/searched under (the row's itemId field). FK to Items:
		// the filed item MUST exist - this guards against the 30000-placeholder import bug (#212) where every listing
		// shared one bogus ItemId and the client collapsed them to a single "Torn Rags" row. NB this is the SEARCH
		// key, still distinct from the listed instance's own Template/Data1..4 below (which is the specific item sold).
		public uint ItemId { get; set; }

		// Navigation to the filed catalog item (FK ItemId -> Items.Id, Restrict). Load with Include when needed.
		// virtual so the lazy-loading-proxy DbContext used in tests (EquipmentPersistenceTests etc.) can build its
		// model - UseLazyLoadingProxies requires every navigation to be virtual (the live app doesn't enable proxies).
		public virtual Item Item { get; set; }

		// --- undecoded-but-preserved framing scalars (verbatim, like ShopItem.Stat356) ----------------------
		// Row field after itemId: a u64 (0 in the capture - buyout / reserved) and a u32 (an item-handle-like value).
		public ulong InstanceField { get; set; }
		public uint ItemHandle { get; set; }

		// Entry stat 0 (a flag word, 0x421 in the capture) and stat 265 (a timestamp/expiry-like value).
		public uint EntryFlags { get; set; }
		public uint Timestamp { get; set; }

		// --- the listed item INSTANCE identity (the entry's ProtoAcgItem) ------------------------------------
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }

		// ProtoAcgItem Template1/Template2 (equal in the capture) and the Level/Hash tail.
		public uint Template { get; set; }
		public uint ItemLevel { get; set; }
		public uint ItemHash { get; set; }
	}
}
