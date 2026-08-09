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

		// The item level, and it is EXACTLY what the wire carries: across all 8,479 captured snapshot entries the
		// entry's `level` field equals this column for its template - 8,479 for 8,479, zero exceptions. So this is the
		// one source for it, and SnapshotItems.Identity reads it here rather than taking it from a caller (it used to
		// be a `level = 1` default parameter nobody passed, which shipped level 1 on every item we sent).
		//
		// 4,107 of the 4,669 shipped items carry a real value; the 562 zeroes are templates no source has given a level
		// yet, not a general gap. This comment used to claim the opposite ("mostly 0 for capture-harvested items"),
		// which is what stopped anyone looking at the column when the emitter was sending 1.
		public byte Level { get; set; }

		// The template's `Flags` word - RDB item-template property field **0**, the client's own name for it - and it
		// is the item's BIRTH `stat_list` stat-0 value, not a derived column. `InventoryGame_c::AddTemplateItem` copies
		// this dword straight into a newly created item's stat 0, and retail's server does the same: of the 179
		// captured DispatchAddItem arrivals, 174 carry EXACTLY this value, and four of the five differences are only
		// bit 4 (0x10, a delivery-path bit the client itself ORs in on mail/AH hand-overs).
		//
		// ⭐ **Bit 26 (0x04000000) is BOUND.** A template carrying it produces items that exist bound from the moment
		// they are created - which is what the client's tooltip calls "Binds when Picked Up" (1,267 of 4,741
		// templates). Bind-on-EQUIP is a separate, computed rule over fields 177/499/30 and is not in this word.
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00 catch one binding:
		//  stat 0 goes 0x00000001 -> 0x04000001 across an equip]
		//
		// ⚠️ Read with ItemTemplateProperties, NOT TryReadPropertyField - field 0 sits in the property table's FIRST
		// descending run and that reader takes the longest run and rejects fieldId 0, so it cannot see this at all.
		// See docs/rdb/item-template-stats.md.
		//
		// ⚠️ NULLABLE, and that is load-bearing: 65 templates genuinely carry `Flags = 0`, so 0 cannot double as
		// "we have not read a template for this item". 61 of the shipped items have no readable/decryptable RDB entry
		// at all; they get NULL and the emitter falls back (see SnapshotItems.DefaultFlags) rather than claiming the
		// template said zero.
		public uint? Flags { get; set; }

		// The two other template property fields the client's binding rules read - stored RAW, exactly like `Flags`,
		// so both rules stay derived from what the template says rather than from a conclusion we drew at import.
		//
		//   177  "More Flags"   bit 5 alone makes an item bind on equip
		//    30  "Can"          bit 2, gated by Quality (field 499) being better than 1
		//
		// ⚠️ Non-nullable, unlike `Flags`, and for a reason rather than by omission: the client's `getProp` returns 0
		// for a field the template does not carry, so 0 IS the client's answer for absent and there is nothing to
		// distinguish. `Flags` is nullable only because its absent case falls back to 1 rather than to 0.
		public uint MoreFlags { get; set; }

		public uint Can { get; set; }

		// Whether wearing this item BINDS it to the character - the client's own predicate, transcribed from
		// `ItemInfoView`'s FUN_014d51a0 (#756):
		//
		//     MoreFlags(177) & 0x20  ||  (PowerLevel(499) > 1 && Can(30) & 0x04)
		//
		// 2,014 of the 4,741 readable templates satisfy it. The MoreFlags clause fires on none of them today, and
		// within the other the `Can` bit is near-universal (3,854 templates) so `Quality > 1` is what actually
		// discriminates - the rule is "better than Common, and flagged equippable".
		//
		// `Quality` IS field 499 (its enum values are the field's own 0-indexed tiers), which is why the rule reads it
		// here instead of a fourth raw column - and it is also the BETTER reader for it. `ItemTemplateProperties`, which
		// fills MoreFlags/Can, only walks the property table's head; 25 templates carry 499 beyond the display-name
		// string where only Quality's lenient whole-record scan reaches it, and since `Quality > 1` is the clause that
		// discriminates, those 25 would otherwise silently stop binding. `--report item-binding` prints the gap
		// (2,035 via Quality vs 2,014 via the head table) so it stays a number rather than a mystery.
		[NotMapped]
		public bool BindsOnEquip => (MoreFlags & 0x20) != 0 || ((uint?)Quality > 1 && (Can & 0x04) != 0);

		// Whether this item binds the moment it is acquired - the client's `FUN_014d5210`, which is bit 26 of the
		// template's own `Flags` word. It mints BOUND: every copy carries the bit from the moment it exists, which is
		// what "Binds when Picked Up" means.
		//
		// ⚠️ `Flags is { } f`, not `(Flags & Bit) != 0`: a LIFTED `!= 0` over a `uint?` is TRUE for null, so the short
		// form would call every item with no readable template bound.
		[NotMapped]
		public bool BindsOnPickup => Flags is { } f && (f & BoundOnTheWire) != 0;

		// The `Flags` word to put on the wire for a copy of this template that has not bound - i.e. `Flags` with the
		// unknown-template case resolved. NULL means no RDB template was readable (61 of the shipped rows), and the
		// fallback is 1: what every item shipped before #756, and the one bit 4,676 of the 4,741 readable templates
		// carry. It is NOT a decoded default - an item that reaches it renders unbound whatever its template says.
		//
		// Here rather than at each emitter because there are two of them (the snapshot entry and the live add) plus
		// the bind message, and a bare `?? 1` repeated three times is how they stop agreeing.
		[NotMapped]
		public uint WireFlags => Flags ?? UnreadTemplateFlags;

		private const uint UnreadTemplateFlags = 1;

		// Bit 26 of the entry's `Flags` stat (stat 0) - "this item is bound to whoever holds it". The client tests it
		// with `vt+0xA0(0x04000000)` before printing "Character Bound"; the server sets it on the equipped cell when a
		// bind-on-equip item goes on. One name for it, shared by the template's word and the instance's.
		public const uint BoundOnTheWire = 1u << 26;

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
		// it is `ProtoAcgItem.hash` (and the FullChar snapshot tail's f8 field) - and `hash` is the CLIENT's own name
		// for it, not ours: the schema in `aoc-client-protos.proto` is reverse-engineered from the FileDescriptorProto
		// blobs embedded in AgeOfConan.exe, so the field names are authoritative.
		//
		// What it is NOT, measured rather than assumed (#719):
		//
		// - Not derivable from anything we hold. Over the 3,982 items that have one, `Id`, `crc32(name)`,
		//   `bkdr31(name)`, `Key1`, `crc32(id)`, `crc32(key quad)`, and the XOR and sum of the key quad all match 0.
		// - Not colliding: all 3,993 values are distinct.
		// - Not carried as a stat on an inventory entry (0 of 165 captured DispatchAddItem entries carry stat 0x2BC).
		//
		// ⚠️ The client's stat table has an `ACGItemSeed` (stat 0x2BC) sitting beside `ACGItemLevel`/`TemplateID`/
		// `TemplateID2`/`CategoryID`, and it is TEMPTING to read this field as that seed. Do not, on that basis alone:
		// the two name sets are not the same tuple (the stats include CategoryID and Nano1..4 with no proto field;
		// the message includes key1..3 and template3 with no stat), so "the only unmatched pair" is an artifact of
		// listing a subset. Whether `hash` IS the ACG seed is open - Gitea #723.
		//
		// It can only be HARVESTED, never computed - and it rides the messages that describe a real item INSTANCE,
		// not the ones that merely advertise a template:
		//
		//   world-entry snapshot   8,479 / 8,479  carry it
		//   NPC shop window        2,734 / 2,734  carry it   <- browsing a vendor is enough, no purchase needed
		//   live 0x2020 AddItem    carries it
		//   AH search listings         0 / 328    carry it
		//   mail attachments           0 / 2      carry it
		//   Claims/Redeem list         0          carries it
		//
		// So the cheapest way to fill a missing one is to open a VENDOR that stocks the item. Traced end to end for
		// template 163118 (Feywater) through the AH buy capture: listing 0, mail attachment 0 (twice), then the live
		// add that delivers it carries 909133648 and every later snapshot carries the same.
		//
		// ⚠️ 0 means NOT HARVESTED, not "has none". This column is the provenance record - the token a capture
		// actually carried - and 232 of the 4,225 shipped items legitimately sit at 0 because no capture ever showed
		// them entering an inventory or a shop window.
		//
		// It is NOT what goes on the wire: read `AcgHash.ForWire(Id, Hash)` for that, which mints a token when this
		// is 0. Nothing renders on `Hash != 0` any more. It used to - the world-entry snapshot dropped hash-less
		// entries, so a hash-less item could not be held at all, which made claims undeliverable and AH purchases
		// live-only - until #723 established in-client that the client carries the token opaquely: it validates the
		// four characters and resolves nothing through them, so a minted one is indistinguishable from a retail one.
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
