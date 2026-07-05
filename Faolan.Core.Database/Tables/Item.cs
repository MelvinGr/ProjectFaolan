using System.ComponentModel.DataAnnotations;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	// An item TEMPLATE: the stable identity (Data quad + Hash) the client needs to realize an item, keyed by
	// the template id (BaseEntity.Id). Sourced from the old C++ `p_items` (Archive/), real captured inventories, and
	// AH listings - see the per-field notes (the Data quad vs Hash distinction is the crux of what renders how).
	public class Item : BaseEntity
	{
		// Keeps the item out of RANDOM loot rolls (the per-map pool sample + the all-items fallback, #273). The Type
		// filter already excludes Quest/Resource, but some quest items are typed Normal (only ever seen in a loot bag,
		// never a quest container) and UNIQUE items (e.g. Ape King's Treasure) are typed Normal too with no wire-derivable
		// "unique" flag - this is the explicit lever for those. Seeded at import (non-Normal types + a curated unique
		// denylist) and settable live via the .noloot GM command; the importer only ever SETS it, so live/manual flags
		// survive a re-import. Does NOT stop an item being granted/quest-dropped/spawned - only random drops.
		public bool ExcludeFromRandomLoot { get; set; }
		// Id (BaseEntity) is the item TEMPLATE id (== ProtoAcgItem.Template1/Template2 on the wire).

		// Level requirement / item level. byte on the wire; mostly 0 for capture-harvested items (the RDB carries
		// the real value), so level-appropriate filtering is limited until RDB item levels are imported.
		public byte Level { get; set; }

		// Normal / Quest / Resource, set at import from the container an item was captured in (default Normal).
		[ConstrainToEnumValues]
		public ItemType Type { get; set; }

		[MaxLength(256)]
		public string Name { get; set; }

		// The item's stable identity QUAD (Data1..Data4) - the per-template 16-byte ProtoDecryptionKey repeated in
		// the inventory snapshot tail and the add-item packet, and the TEA key for the item's encrypted RDB template.
		// The client REJECTS an item whose quad isn't a template it knows (the add/remove stall). Sourced from the
		// old C++ `p_items` (Archive/) AND harvested from real captured inventories (every item a player has held)
		// AND from AH listings (the listing carries the per-template quad). Data0 is a separate leading word (0 for
		// quad-only sources like AH listings). See docs/archive/spawn-dissection-plan.md Phase 5.
		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }

		// Per-TEMPLATE hash the client needs (alongside the Data quad) to fully realize/render the item from its RDB.
		// On the wire it is ProtoAcgItem.Hash (and the FullChar snapshot tail's f8 field). It is NOT in the old C++
		// `p_items`, so it can only be HARVESTED from a capture where the item sat in an inventory (a 0x737A6DF9
		// snapshot or a 0x2020 add-item) - it is absent from sources that only describe the template abstractly (AH
		// search-results carry Hash=0). An item is "fully buildable" (renders via the hash-gated world-entry
		// snapshot) iff Hash != 0 AND it has a Data quad; the more forgiving live DispatchAddItem (0x2020) path
		// renders an item by template even with Hash == 0 (used for AH-bought items we lack a hash for).
		public uint Hash { get; set; }

		// The auction-house SEARCH CATEGORY id the client queries by (e.g. 30000/30030, NOT the template id) - the
		// FIRST u32 of the decrypted RDB item-template body (template[12..16]). Decoded from the client RDB
		// (RdbItemImporter), validated byte-for-byte against 170 captured AH listings. 0 when unknown (not RDB-imported).
		// Used to file a player's listing under the right item-type search node (issue #181).
		public uint SearchCategory { get; set; }

		// The item's base VALUE = RDB item-template property field 74 (the property table is [fieldId][value] pairs
		// after the body header; RdbItemImporter). The vendor SELL price is round(Value / 10) per unit, validated
		// against the captured sell anchors (e.g. 190->19, 920->92, 560->56 tin; #137). 0 when unknown (not
		// RDB-imported, or a template whose property table doesn't carry field 74) - callers fall back to the
		// provisional level-based price.
		public uint Value { get; set; }
	}
}
