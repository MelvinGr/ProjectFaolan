using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One auction-house (Tradepost) listing - a row in the DispatchTradepostSearchResults grid (sc 0x2005).
	// Decoded from the NecroL80 auctionhouse capture (tools/capture/decode_tradepost.py, docs/subsystems/tradepost-protocol.md);
	// the on-the-wire row is built byte-exact by TradepostListingCodec. The listing carries a concrete item
	// instance (its own Data1..4 quad + template), not a template reference - the AH sells the specific item a
	// seller posted, so the identity lives here, not via the Items table (the row's ItemId search key and the
	// instance's Template can differ). Keyed by SaleId (the unique listing id the BuyItem targets); SellerName is
	// the stable seller identity (handles are session-volatile).
	// SaleId is unique within a realm, not globally: the AH is realm-local, and two realms can independently
	// mint the same SaleId, so the unique key is the compound (RealmId, SaleId). The leading RealmId also serves the
	// realm-scoped FK, so a separate RealmId index is redundant.
	[Index(nameof(RealmId), nameof(SaleId), IsUnique = true), Index(nameof(ItemId))]
	public class TradepostListing : BaseEntity
	{
		// The catalog item (template id) the listing is filed/searched under (the row's itemId field). FK to Items:
		// the filed item must exist - this guards against the 30000-placeholder import bug where every listing
		// shared one bogus ItemId and the client collapsed them to a single "Torn Rags" row. NB this is the search
		// key, still distinct from the listed instance's own Template/Data1..4 below (which is the specific item sold).
		public uint ItemId { get; set; }

		// Navigation to the filed catalog item (FK ItemId -> Items.Id, Restrict). Load with Include when needed.
		// virtual so the lazy-loading-proxy DbContext used in tests (EquipmentPersistenceTests etc.) can build its
		// model - UseLazyLoadingProxies requires every navigation to be virtual (the live app doesn't enable proxies).
		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; } = null!;

		// The realm this listing belongs to - the seller's realm; the auction house is realm-local, so every
		// AH search/list is realm-scoped. Backfilled from the seller character on migration.
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; } = null!;

		// The unique listing id; the buy targets this, and it doubles as the entry's ProtoInventoryGameEntry.ItemId.
		public ulong SaleId { get; set; }

		// --- the listed item instance identity (the entry's ProtoAcgItem) ------------------------------------
		// The listed item's TEA key is per-template and lives once in Items; the listing references it by Template
		// (a second hard FK to Items, distinct from the ItemId search key above) and the codec reads Key1..4 through
		// this nav. Named TemplateItem to avoid colliding with the Item nav (the ItemId search-catalog item). ItemHash
		// stays a column: on the wire the AH row carries the captured hash (0 for AH search results), not Items.Hash.
		[ForeignKey(nameof(Template))]
		public virtual Item TemplateItem { get; set; } = null!;

		[MaxLength(64)]
		public string SellerName { get; set; } = null!;

		// The seller's origin bank slot the item was listed from. The seller addresses their own listing by this
		// slot (TradepostChangeItemPrice / TradepostCancelSellItem carry nBankItemSlot, not the saleId), and a cancel
		// returns the escrowed item to it. 0 for importer-seeded listings (not player-managed). Keyed with SellerName.
		public uint BankSlot { get; set; }

		// Entry stat 0 (a flag word, 0x421 in the capture) and stat 265 (a timestamp/expiry-like value).
		public uint EntryFlags { get; set; }

		// The listing's entity handle on the wire (session-volatile; kept for byte-exact rebuilds).
		public uint Handle { get; set; }
		public uint ItemHandle { get; set; }
		public uint ItemHash { get; set; }

		public uint Quantity { get; set; }

		// ProtoAcgItem Template1/Template2 (equal in the capture) and the Hash tail. The wire's `Level` is not here: it
		// is `Items.Level` for this template, read through TemplateItem. No template in 8,644 captured sightings
		// carries two different levels, so a per-listing copy stored nothing the template does not.
		public uint Template { get; set; }
		public uint Timestamp { get; set; }

		// --- undecoded-but-preserved framing scalars (verbatim, like ShopItem.Stat356) ----------------------
		// Row field after itemId: a u64 (0 in the capture - buyout / reserved) and a u32 (an item-handle-like value).
		public ulong InstanceField { get; set; }

		// Asking price in the wallet wire unit (tin). u64 on the wire; also mirrored into entry stat 258.
		public ulong Price { get; set; }
	}
}
