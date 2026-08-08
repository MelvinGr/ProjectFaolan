using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	// An item TEMPLATE: the stable identity (Data quad + Hash) the client needs to realize an item, keyed by
	// the template id (BaseEntity.Id). Sourced from the old C++ `p_items` (Archive/), real captured inventories, and
	// AH listings - see the per-field notes (the Data quad vs Hash distinction is the crux of what renders how).
	public class Item : BaseEntity
	{
		[ForeignKey(nameof(OnUseSpell))]
		public virtual Spell? OnUseSpellDefinition { get; set; }

		// The stat bonuses this item grants when equipped - the whole tooltip stat list (attributes, vitals AND
		// ratings), one row per (statId, amount). Decoded from the RDB item-template stat-modifier region
		// (RdbItemImporter -> ItemTemplateStatModifiers, docs/rdb/item-template-stats.md, #3) PLUS the field-972 primary
		// attribute. MaxStatAggregator sums these by wire stat id across a character's equipped gear so worn gear raises
		// the character's derived MaxHealth/MaxStamina/MaxMana (and attributes). Replaced the old flattened
		// *Bonus columns (Str/Con/Int/Wis/Dex), which could only hold the primary attribute.
		public virtual ICollection<ItemStatModifier> StatModifiers { get; set; } = [];

		// The item's stable identity - the per-template 128-bit TEA key (4 words) that unlocks the item's encrypted
		// RDB template. On the wire it is exactly ProtoAcgItem.key1, repeated in the inventory-snapshot tail and the
		// add-item packet; the client REJECTS an item whose key isn't a template it knows (the add/remove stall).
		// Sourced from the old C++ `p_items` (Archive/), real captured inventories, and AH listings. Stored as an EF
		// owned type -> flat columns Key1..Key4. virtual for the lazy-loading-proxy test context (owned types load
		// eagerly regardless; virtual only satisfies the proxy's all-navigations-virtual rule). See docs/re/spawn-dissection.md Phase 5.
		public virtual ProtoDecryptionKey Key { get; set; } = new ProtoDecryptionKey();

		// The item's quality/rarity tier (Mundane..Legendary) = RDB item-template property field 499 (the client's own
		// tier id; see ItemQuality). Extracted by RdbItemImporter with the same strictly-decreasing property-table parse
		// as Value (field 74). NULL when the template carries no field 499 (or the item was never RDB-imported) - the
		// generated CHECK admits NULL (a closed enum, so the values are constrained; unset stays NULL).
		[ConstrainToEnumValues]
		public ItemQuality? Quality { get; set; }

		// Normal / Quest / Resource, set at import from the container an item was captured in (default Normal).
		[ConstrainToEnumValues]
		public ItemType Type { get; set; }

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
		public int? MaxDamage { get; set; }

		// The weapon's inherent MIN/MAX damage - RDB item-template property fields 286 (0x11E) / 285 (0x11D), decoded
		// from the client's CoreCombatManager::GUIGetMinDamage()/GUIGetMaxDamage() (docs/rdb/item-template-stats.md,
		// #395). NULL for non-weapons (and weapons whose fields sit outside the strict property run). The client layers
		// attribute/rating bonuses on top for display; these are just the weapon base, factored into melee by
		// CombatResolver. Raw template values (scale provisional).
		public int? MinDamage { get; set; }

		[MaxLength(256)]
		public string Name { get; set; } = null!;

		// The tooltip description, from LDB category 50201 keyed by the item template id (#414) - e.g. 8757
		// "Animated Dagger" -> "Deals piercing damage over time.". NULL when the LDB has none for this template, or
		// when the RDB-backed rdb-ldb-names module has not been run (it is opt-in, needing --Content:RdbDir).
		[MaxLength(2048)]
		public string? Description { get; set; }

		// The client needs this (alongside the Data quad) to fully realize/render the item from its RDB. On the wire
		// it is `ProtoAcgItem.Hash` (and the FullChar snapshot tail's f8 field).
		//
		// ⚠️ "Hash" is OUR name and it is misleading: the client calls this the item SEED. Its stat-name table
		// registers `ACGItemSeed` at stat id 0x2BC, immediately ahead of `ACGItemLevel` (0x2BD),
		// `ACGItemTemplateID` (0x2BE), `ACGItemTemplateID2` (0x2BF) and `ACGItemCategoryID` (0x2C0) - and
		// `ProtoAcgItem` carries exactly Level / Template1 / Template2 beside this field, so the one unmatched pair
		// on each side is Hash <-> Seed. ACG is the item-GENERATION system, so this is the seed its generated
		// properties come from, not a checksum of anything. Nothing derives it: over the 3,982 items that have one,
		// `Id`, `crc32(name)`, `bkdr31(name)`, `Key1`, `crc32(id)`, `crc32(key quad)`, XOR and sum of the key quad
		// all match 0. All 3,993 values are distinct, so it does not collide either. See Gitea #719.
		//
		// It can only be HARVESTED, and only from a capture where the item ENTERED AN INVENTORY - which is exactly
		// where retail first reveals it. Traced through the AH buy capture for template 163118 (Feywater): the
		// listing carries 0, the mail attachment carries 0 (twice), the live 0x2020 add that delivers it carries
		// 909133648, and every later snapshot carries the same. Corpus-wide, 0 of 328 search-result rows and 0 of 2
		// mail attachments have one, against 8,479 of 8,479 world-entry snapshot entries that do.
		//
		// An item is renderable by the world-entry snapshot iff Hash != 0 AND it has a Data quad; the snapshot drops
		// the rest, which is why nothing hash-less may be placed in an inventory (InventoryItemHashTests, #719).
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

		// For a BAG item (equipped in the bag slot, equip slot 3): the extra backpack slots it adds - the "+N slots"
		// the client tooltip shows. The backpack container's max_size = 24 (base) + this. RDB item-template property
		// field 104 (= the Bag container kind, 0x68); validated: Burlap Sack 24 (24->48), Bag of Holding 20 (->44),
		// Veteran's Bag 35 (->59). NULL = not a bag. Extracted by RdbItemImporter; see FullCharacterRoots.
		public uint? BagExtraSlots { get; set; }

		// The self-buff spell this item applies when USED (right-clicked) - a consumable's on-use effect (Health/Mana
		// potion, food, drink, Double XP potion, ...). The BASE spell id (Spells.Id); the wire ApplySpell id is
		// base<<8. NULL = not a use-a-spell consumable. Harvested from captures (CaptureItemUseImporter pairs the item's
		// TemplateActionIIR 0x6C300937 with the adjacent ApplySpell 0x6B87E316); the scalable long-term source is the
		// RDB item-template on-use field (undecoded). Drives GameServerListener.UseInventoryItem (data, not a per-id switch).
		public uint? OnUseSpell { get; set; }

		// For a RESOURCE BAG item (equipped in the resource-bag slot, equip slot 26): the extra RESOURCE-container slots
		// it adds - the sibling of BagExtraSlots for the backpack. The resource container's max_size = 48 (base) + this.
		// RDB item-template property field 1331 (unlike the normal bag's field 104 = Bag kind, resource bags carry their
		// capacity here - live-confirmed: Max Resource Bag = [1331]=102, matching its "(102 slots)" name). Sits in the
		// same non-monotonic tail as field 104. NULL = not a resource bag. Extracted by RdbItemImporter; see
		// FullCharacterRoots.
		public uint? ResourceBagExtraSlots { get; set; }
	}
}
