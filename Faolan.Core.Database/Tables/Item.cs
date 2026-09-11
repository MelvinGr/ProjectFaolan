using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     An item template: the stable identity (Data quad + Hash) the client needs to realize an item, keyed by the
	///     template id (<see cref="BaseEntity.Id" />). Sourced from the client's RDB item templates, captured inventories
	///     and AH listings - see the per-field notes (the Data quad vs Hash distinction is the crux of what renders how).
	/// </summary>
	public class Item : BaseEntity
	{
		[ForeignKey(nameof(OnUseSpell))]
		public virtual Spell? OnUseSpellDefinition { get; set; }

		/// <summary>
		///     The stat bonuses this item grants when equipped - the whole tooltip stat list (attributes, vitals and
		///     ratings), one row per (statId, amount). Decoded from the RDB item-template stat-modifier region
		///     (<c>RdbItemImporter</c> -&gt; <c>ItemTemplateStatModifiers</c>, <c>docs/rdb/item-template-stats.md</c>)
		///     plus the field-972 primary attribute. <c>MaxStatAggregator</c> sums these by wire stat id across a
		///     character's equipped gear so worn gear raises the character's derived MaxHealth/MaxStamina/MaxMana (and
		///     attributes).
		/// </summary>
		public virtual ICollection<ItemStatModifier> StatModifiers { get; set; } = [];

		/// <summary>
		///     The item's stable identity - the per-template 128-bit TEA key (4 words) that unlocks the item's encrypted
		///     RDB template. On the wire it is exactly <c>ProtoAcgItem.key1</c>, repeated in the inventory-snapshot tail
		///     and the add-item packet; the client rejects an item whose key is not a template it knows (the add/remove
		///     stall). Stored as an EF owned type -&gt; flat columns Key1..Key4. <c>virtual</c> for the lazy-loading-proxy
		///     test context (owned types load eagerly regardless; virtual only satisfies the proxy's
		///     all-navigations-virtual rule). See <c>docs/re/spawn-dissection.md</c> Phase 5.
		/// </summary>
		public virtual ProtoDecryptionKey Key { get; set; } = new ProtoDecryptionKey();

		/// <summary>
		///     The item's second template and its own key - the high endpoint of a level range. <c>ProtoAcgItem</c>
		///     carries <c>template1</c>/<c>template2</c> with a key each, and <c>AcgItemData_t::GetInterpolatedItem</c>
		///     takes both plus a level: the two templates are the low and high endpoints of a scaling item and the
		///     entry's own <c>level</c> says where between them this copy sits. Measured over 13,368 captured
		///     ProtoAcgItems (344 distinct pairs), the three that differ differ in nothing but level fields -
		///     <c>Cabochon Azurite</c> 50 -&gt; 80, <c>Sanguine Radiant Sapphire</c> 75 -&gt; 80, <c>Alchemist</c>
		///     40 -&gt; 49 - same display name, same everything else.
		/// </summary>
		/// <remarks>
		///     Null means "no second endpoint has been seen", and the emitters then send <c>template2 = template1</c>.
		///     It is not derivable: <c>template2 == template1 + 1</c> on all three, but 341 of the 344 pairs have
		///     <c>template2 == template1</c>, so +1 would be wrong far more often than right. Only a capture can tell
		///     the two apart.
		///     <para>
		///         <see cref="PairKey" /> is what decrypts the second template. Sending <see cref="TemplateId2" />
		///         without it would be worse than flattening, because the client would have a template it cannot
		///         read. The two travel together or not at all, which is why an all-zero <see cref="PairKey" /> and a
		///         null <see cref="TemplateId2" /> mean the same thing. Cross-pairing the keys yields garbage headers,
		///         so it is genuinely its own key. Named <c>PairKey</c> rather than <c>Key2</c> because
		///         <see cref="Key" /> is an owned quad whose second word already maps to a column called <c>Key2</c>;
		///         its own columns are <c>PairKey1..4</c>.
		///     </para>
		///     <para>
		///         Three rows carry one, which is a capture limit rather than a game limit: any level-scaled item has a
		///         pair.
		///     </para>
		/// </remarks>
		public uint? TemplateId2 { get; set; }

		public virtual ProtoDecryptionKey PairKey { get; set; } = new ProtoDecryptionKey();

		/// <summary>
		///     The pair as the wire wants it: the second endpoint when we have one, else the first repeated. One place,
		///     because three emitters need the same answer (the entry's own identity, a socketed gem's, and the
		///     ItemSlot&lt;N&gt;ACG1/ACG2 stat pair that restates the socket).
		/// </summary>
		[NotMapped]
		public uint WireTemplate2 => TemplateId2 ?? Id;

		[NotMapped]
		public ProtoDecryptionKey WireKey2 => TemplateId2 == null
			? Key
			: PairKey;

		/// <summary>
		///     The item's quality/rarity tier (Mundane..Legendary) = RDB item-template property field 499 (the client's
		///     own tier id; see <see cref="ItemQuality" />). Extracted by <c>RdbItemImporter</c> with the same
		///     strictly-decreasing property-table parse as <see cref="Value" /> (field 74). Null when the template
		///     carries no field 499 (or the item was never RDB-imported) - the generated check admits null (a closed
		///     enum, so the values are constrained; unset stays null).
		/// </summary>
		[ConstrainToEnumValues]
		public ItemQuality? Quality { get; set; }

		/// <summary>
		///     Normal / Quest / Resource, set at import from the container an item was captured in (default Normal).
		/// </summary>
		[ConstrainToEnumValues]
		public ItemType Type { get; set; }

		/// <summary>
		///     Keeps the item out of random loot rolls (the per-map pool sample + the all-items fallback). The
		///     <see cref="Type" /> filter already excludes Quest/Resource, but some quest items are typed Normal (only
		///     ever seen in a loot bag, never a quest container) and unique items (e.g. Ape King's Treasure) are typed
		///     Normal too with no wire-derivable "unique" flag - this is the explicit lever for those. Seeded at import
		///     (non-Normal types + a curated unique denylist) and settable live via the <c>.noloot</c> GM command; the
		///     importer only ever sets it, so live/manual flags survive a re-import. Does not stop an item being
		///     granted/quest-dropped/spawned - only random drops.
		/// </summary>
		public bool ExcludeFromRandomLoot { get; set; }
		// Id (BaseEntity) is the item template id (== ProtoAcgItem.Template1/Template2 on the wire).

		/// <summary>
		///     The item level, and it is exactly what the wire carries: across all 8,479 captured snapshot entries the
		///     entry's <c>level</c> field equals this column for its template, zero exceptions. So this is the one
		///     source for it, and <c>SnapshotItems.Identity</c> reads it here rather than taking it from a caller.
		///     4,107 of the 4,669 shipped items carry a real value; the 562 zeroes are templates no source has given a
		///     level yet, not a general gap.
		/// </summary>
		public byte Level { get; set; }

		/// <summary>
		///     The template's <c>Flags</c> word - RDB item-template property field 0, the client's own name for it -
		///     and it is the item's birth <c>stat_list</c> stat-0 value, not a derived column.
		///     <c>InventoryGame_c::AddTemplateItem</c> copies this dword straight into a newly created item's stat 0,
		///     and retail's server does the same: of the 179 captured DispatchAddItem arrivals, 174 carry exactly this
		///     value, and four of the five differences are only bit 4 (0x10, a delivery-path bit the client itself ORs
		///     in on mail/AH hand-overs).
		/// </summary>
		/// <remarks>
		///     Bit 26 (0x04000000) is bound. A template carrying it produces items that exist bound from the moment
		///     they are created - which is what the client's tooltip calls "Binds when Picked Up" (1,267 of 4,741
		///     templates). Bind-on-equip is a separate, computed rule over fields 177/499/30 and is not in this word.
		///     [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00 catch one binding:
		///     stat 0 goes 0x00000001 -&gt; 0x04000001 across an equip]
		///     <para>
		///         Read with <c>ItemTemplateProperties</c>, not <c>TryReadPropertyField</c> - field 0 sits in the
		///         property table's first descending run and that reader takes the longest run and rejects fieldId 0,
		///         so it cannot see this at all. See <c>docs/rdb/item-template-stats.md</c>.
		///     </para>
		///     <para>
		///         Nullable, and that is load-bearing: 65 templates genuinely carry <c>Flags = 0</c>, so 0 cannot
		///         double as "no template has been read for this item". 61 of the shipped items have no
		///         readable/decryptable RDB entry at all; they get null and the emitter falls back (see
		///         <see cref="WireFlags" />) rather than claiming the template said zero.
		///     </para>
		/// </remarks>
		public uint? Flags { get; set; }

		/// <summary>
		///     The two other template property fields the client's binding rules read - stored raw, exactly like
		///     <see cref="Flags" />, so both rules stay derived from what the template says rather than from a
		///     conclusion drawn at import:
		///     <code>
		///     177  "More Flags"   bit 5 alone makes an item bind on equip
		///      30  "Can"          bit 2, gated by Quality (field 499) being better than 1
		///     </code>
		/// </summary>
		/// <remarks>
		///     Non-nullable, unlike <see cref="Flags" />, and for a reason rather than by omission: the client's
		///     <c>getProp</c> returns 0 for a field the template does not carry, so 0 is the client's answer for absent
		///     and there is nothing to distinguish. <see cref="Flags" /> is nullable only because its absent case falls
		///     back to 1 rather than to 0.
		/// </remarks>
		public uint MoreFlags { get; set; }

		public uint Can { get; set; }

		/// <summary>
		///     How many gem sockets this item has - template property field 114, which the client's own stat table
		///     names <c>NumGemSlots</c> and its tooltip reads directly (the GemSlots line is gated on
		///     <c>getProp(0x72) &gt; 0</c>). The bound is four: there are four <c>GemSlot&lt;N&gt;</c> fields, four
		///     <c>GemSlot&lt;N&gt;Color</c> fields and four <c>ItemSlot&lt;N&gt;ACG</c> stat pairs. 647 templates
		///     declare one: 612 with a single socket, 14 with two, 12 with three, 9 with four - the four-slot ones
		///     being high-end weapons. The per-slot colours corroborate the count independently (a 4-slot weapon
		///     declares four distinct colours, a 3-slot item three and a zero).
		/// </summary>
		/// <remarks>
		///     An item can legitimately hold more gems than this. Three of the five templates the dumps show sockets on
		///     carry three gems while declaring one: retail items left over from an older version whose slot counts
		///     were later reduced - the client has a <c>GemOverflow</c> token, a <c>GemOverflowInfo</c> explainer and
		///     a clickable <c>&lt;a href="gemoverflow"&gt;</c> link for exactly that state. So this bounds what may be
		///     added; it does not describe what an existing item holds.
		/// </remarks>
		public uint GemSlots { get; set; }

		/// <summary>
		///     Whether wearing this item binds it to the character - the client's own predicate, transcribed from
		///     <c>ItemInfoView</c>'s <c>FUN_014d51a0</c>:
		///     <code>
		///     MoreFlags(177) &amp; 0x20  ||  (PowerLevel(499) &gt; 1 &amp;&amp; Can(30) &amp; 0x04)
		///     </code>
		///     2,014 of the 4,741 readable templates satisfy it. The MoreFlags clause fires on none of them, and within
		///     the other the <c>Can</c> bit is near-universal (3,854 templates) so <c>Quality &gt; 1</c> is what
		///     actually discriminates - the rule is "better than Common, and flagged equippable".
		/// </summary>
		/// <remarks>
		///     <see cref="Quality" /> is field 499 (its enum values are the field's own 0-indexed tiers), which is why
		///     the rule reads it here instead of a fourth raw column - and it is also the better reader for it.
		///     <c>ItemTemplateProperties</c>, which fills MoreFlags/Can, only walks the property table's head; 25
		///     templates carry 499 beyond the display-name string where only Quality's lenient whole-record scan
		///     reaches it, and since <c>Quality &gt; 1</c> is the clause that discriminates, those 25 would otherwise
		///     silently stop binding. <c>--report item-binding</c> prints the gap (2,035 via Quality vs 2,014 via the
		///     head table).
		/// </remarks>
		[NotMapped]
		public bool BindsOnEquip => (MoreFlags & 0x20) != 0 || ((uint?)Quality > 1 && (Can & 0x04) != 0);

		/// <summary>
		///     Whether this item binds the moment it is acquired - the client's <c>FUN_014d5210</c>, which is bit 26 of
		///     the template's own <see cref="Flags" /> word. It mints bound: every copy carries the bit from the moment
		///     it exists, which is what "Binds when Picked Up" means.
		/// </summary>
		/// <remarks>
		///     <c>Flags is { } f</c>, not <c>(Flags &amp; Bit) != 0</c>: a lifted <c>!= 0</c> over a <c>uint?</c> is
		///     true for null, so the short form would call every item with no readable template bound.
		/// </remarks>
		[NotMapped]
		public bool BindsOnPickup => Flags is { } f && (f & BoundOnTheWire) != 0;

		/// <summary>
		///     The <see cref="Flags" /> word to put on the wire for a copy of this template that has not bound - i.e.
		///     <see cref="Flags" /> with the unknown-template case resolved. Null means no RDB template was readable
		///     (61 of the shipped rows), and the fallback is 1: the one bit 4,676 of the 4,741 readable templates
		///     carry. It is not a decoded default - an item that reaches it renders unbound whatever its template says.
		/// </summary>
		/// <remarks>
		///     Here rather than at each emitter because there are two of them (the snapshot entry and the live add)
		///     plus the bind message, and a bare <c>?? 1</c> repeated three times is how they stop agreeing.
		/// </remarks>
		[NotMapped]
		public uint WireFlags => Flags ?? UnreadTemplateFlags;

		private const uint UnreadTemplateFlags = 1;

		/// <summary>
		///     Bit 26 of the entry's <c>Flags</c> stat (stat 0) - "this item is bound to whoever holds it". The client
		///     tests it with <c>vt+0xA0(0x04000000)</c> before printing "Character Bound"; the server sets it on the
		///     equipped cell when a bind-on-equip item goes on. One name for it, shared by the template's word and the
		///     instance's.
		/// </summary>
		public const uint BoundOnTheWire = 1u << 26;

		/// <summary>
		///     The weapon's attack range in world units x100 - RDB item-template property field 287, which the client's
		///     own stat table names <c>Attack Range</c> (<see cref="CharacterStatId.AttackRange" /> = 0x011F). Measured
		///     across the NPC weapons: every melee one reads 200 (2.0 units) and every bow 1750 (17.5), so this is what
		///     separates a creature that walks up from one that shoots. Null for a non-weapon.
		/// </summary>
		public int? AttackRange { get; set; }

		public int? MaxDamage { get; set; }

		/// <summary>
		///     The weapon's inherent min/max damage - RDB item-template property fields 286 (0x11E) / 285 (0x11D),
		///     decoded from the client's <c>CoreCombatManager::GUIGetMinDamage()</c>/<c>GUIGetMaxDamage()</c>
		///     (<c>docs/rdb/item-template-stats.md</c>). Null for non-weapons (and weapons whose fields sit outside the
		///     strict property run). The client layers attribute/rating bonuses on top for display; these are just the
		///     weapon base, factored into melee by <c>CombatResolver</c>. Raw template values (scale provisional).
		/// </summary>
		public int? MinDamage { get; set; }

		[MaxLength(256)]
		public string Name { get; set; } = null!;

		/// <summary>
		///     The tooltip description, from LDB category 50201 keyed by the item template id - e.g. 8757
		///     "Animated Dagger" -&gt; "Deals piercing damage over time.". Null when the LDB has none for this template,
		///     or when the RDB-backed <c>rdb-ldb-names</c> module has not been run (it is opt-in, needing
		///     <c>--Content:RdbDir</c>).
		/// </summary>
		[MaxLength(2048)]
		public string? Description { get; set; }

		/// <summary>
		///     The token the client needs (alongside the Data quad) to fully realize/render the item from its RDB. On
		///     the wire it is <c>ProtoAcgItem.hash</c> (and the FullChar snapshot tail's f8 field) - <c>hash</c> is
		///     the client's own name for it: the schema in <c>aoc-client-protos.proto</c> is reverse-engineered from
		///     the FileDescriptorProto blobs embedded in AgeOfConan.exe, so the field names are authoritative.
		/// </summary>
		/// <remarks>
		///     <para>
		///         What it is not, measured rather than assumed: not derivable from anything we hold (over the 3,982
		///         items that have one, <c>Id</c>, <c>crc32(name)</c>, <c>bkdr31(name)</c>, <c>Key1</c>,
		///         <c>crc32(id)</c>, <c>crc32(key quad)</c>, and the xor and sum of the key quad all match 0); not
		///         colliding (all 3,993 values are distinct); not carried as a stat on an inventory entry (0 of 165
		///         captured DispatchAddItem entries carry stat 0x2BC). The client's stat table has an
		///         <c>ACGItemSeed</c> (stat 0x2BC) beside <c>ACGItemLevel</c>/<c>TemplateID</c>/<c>TemplateID2</c>/
		///         <c>CategoryID</c>, but the stat set and the proto field set are not the same tuple, so "the only
		///         unmatched pair" is an artifact of listing a subset; whether <c>hash</c> is the ACG seed is unresolved.
		///     </para>
		///     <para>
		///         It can only be harvested, never computed - and it rides the messages that describe a real item
		///         instance, not the ones that merely advertise a template:
		///         <code>
		///         world-entry snapshot   8,479 / 8,479  carry it
		///         NPC shop window        2,734 / 2,734  carry it   (browsing a vendor is enough, no purchase needed)
		///         live AddItem           carries it
		///         AH search listings         0 / 328    carry it
		///         mail attachments           0 / 2      carry it
		///         Claims/Redeem list         0          carries it
		///         </code>
		///         So the cheapest way to fill a missing one is to open a vendor that stocks the item. Traced end to
		///         end for template 163118 (Feywater) through the AH buy capture: listing 0, mail attachment 0 (twice),
		///         then the live add that delivers it carries 909133648 and every later snapshot carries the same.
		///     </para>
		///     <para>
		///         0 means not harvested, not "has none". This column is the provenance record - the token a capture
		///         actually carried - and 232 of the 4,225 shipped items legitimately sit at 0 because no capture ever
		///         showed them entering an inventory or a shop window.
		///     </para>
		///     <para>
		///         It is not what goes on the wire: read <c>AcgHash.ForWire(Id, Hash)</c> for that, which mints a token
		///         when this is 0. Nothing renders on <c>Hash != 0</c>: the client carries the token opaquely - it
		///         validates the four characters and resolves nothing through them (established in-client), so a minted
		///         one is indistinguishable from a retail one.
		///     </para>
		/// </remarks>
		public uint Hash { get; set; }

		/// <summary>
		///     The auction-house search category id the client queries by (e.g. 30000/30030, not the template id) -
		///     the first u32 of the decrypted RDB item-template body (template[12..16]). Decoded from the client RDB
		///     (<c>RdbItemImporter</c>), validated byte-for-byte against 170 captured AH listings. 0 when unknown (not
		///     RDB-imported). Used to file a player's listing under the right item-type search node.
		/// </summary>
		public uint SearchCategory { get; set; }

		/// <summary>
		///     The item's base value = RDB item-template property field 74 (the property table is [fieldId][value]
		///     pairs after the body header; <c>RdbItemImporter</c>). The vendor sell price is round(Value / 10) per
		///     unit, validated against the captured sell anchors (e.g. 190 -&gt; 19, 920 -&gt; 92, 560 -&gt; 56 tin). 0
		///     when unknown (not RDB-imported, or a template whose property table does not carry field 74) - callers
		///     fall back to the provisional level-based price.
		/// </summary>
		public uint Value { get; set; }

		/// <summary>
		///     For a bag item (equipped in the bag slot, equip slot 3): the extra backpack slots it adds - the "+N
		///     slots" the client tooltip shows. The backpack container's max_size = 24 (base) + this. RDB item-template
		///     property field 104 (= the Bag container kind, 0x68); validated: Burlap Sack 24 (24 -&gt; 48), Bag of
		///     Holding 20 (-&gt; 44), Veteran's Bag 35 (-&gt; 59). Null = not a bag. Extracted by
		///     <c>RdbItemImporter</c>; see <c>FullCharacterRoots</c>.
		/// </summary>
		public uint? BagExtraSlots { get; set; }

		/// <summary>
		///     Where the item can be worn, from the client's own item-template property table. Three fields, all three
		///     named by the client's stat-name table (<c>docs/re/aoc-client-stat-names.tsv</c>), all three present on
		///     every one of the 5,071 decrypted templates:
		///     <code>
		///     Placement    (field 298, stat 0x12A) - a bitmask of CharacterEquipmentSlot ids the item may go into.
		///                  0 means not equippable (1,049 of 5,071: keys, notes, pelts, crates).
		///     DefaultPos   (field  88, stat 0x58)  - the preferred slot when the mask offers several.
		///     OccupySlots  (field 148, stat 0x94)  - every slot the item consumes once worn. Bits beyond the chosen
		///                  slot are additionally blocked, which is the two-hander rule.
		///     </code>
		///     Validated against every worn item the corpus records (72 template/slot pairs, <c>ItemInstances</c>
		///     container 101): a ring is <c>Placement</c> = 1536 = bits 9|10 (either ring), a one-hander 320 = bits
		///     6|8 (either hand), a two-hander 64 = bit 6 alone with <c>OccupySlots</c> = 256 = bit 8 (the off hand
		///     goes too - 280 templates, every one in a documented two-handed weapon class: 30012/30014/30016/30044/
		///     30019). <c>DefaultPos</c> is not derivable from <c>Placement</c>: 45 off-hand focus items (category
		///     30009 - tomes, folios, trinkets) carry the one-hander mask 320 but default to 8.
		/// </summary>
		/// <remarks>
		///     Bits 6 and 8 mean "a main hand" and "an off hand", not slots 6 and 8 specifically. Slots 20/21 are
		///     weapon set 2 and are set-neutral - either set holds any weapons - so which set a hand bit resolves to is
		///     the wearer's choice, not a property of the item. <c>EquipSlotResolver</c> owns that decision.
		/// </remarks>
		public uint Placement { get; set; }

		public uint DefaultPos { get; set; }

		public uint OccupySlots { get; set; }

		/// <summary>
		///     The self-buff spell this item applies when used (right-clicked) - a consumable's on-use effect
		///     (Health/Mana potion, food, drink, Double XP potion, ...). The base spell id (<c>Spells.Id</c>); the wire
		///     ApplySpell id is <c>base &lt;&lt; 8</c>. Null = not a use-a-spell consumable. Harvested from captures
		///     (<c>CaptureItemUseImporter</c> pairs the item's TemplateActionIIR with the adjacent ApplySpell). Drives
		///     <c>UseInventoryItem</c> (data, not a per-id switch).
		/// </summary>
		/// <remarks>
		///     The RDB carries the same fact as a <c>CastNano</c> GameAction element in the template's tail: the
		///     10-word stat-modifier/GameAction record <c>ItemTemplateStatModifiers</c> parses -
		///     <c>[kind][0][4][0][1][0][2][9][payload][...]</c> - with kind 53051 = <c>e_Type_GC_GameAction_CastNano</c>
		///     (key 39; "nano" is Funcom's Anarchy Online word for a spell) and the payload a <c>Spells.Id</c>. It sits
		///     past the display-name string where <c>ItemTemplateProperties</c> stops, at a different offset per
		///     template:
		///     <code>
		///     Raw Beets  @209:  02 00 00 00 | 09 00 00 00 | 28 b9 01 00   -> 112936  "Hearty Meal"
		///     Moonspill  @253:  02 00 00 00 | 09 00 00 00 | 27 c0 02 00   -> 180263  "Mana Potion Effect 10"
		///     </code>
		///     Measured over 26 dumped templates, 12 with a captured on-use spell and 14 without: 8 of 12 recovered
		///     exactly, 0 false positives across the 14, and the 4 misses are explained (both loot caches carry no
		///     spell in the template at all, Totem of Origins carries 3763119 where the capture recorded 3981764
		///     "Degeneration", and Moonspill carries its under a different prefix). A spell id sits above the
		///     <c>statId &lt; 0x1000</c> bound <c>Parse</c> applies, so these records land in <c>ParseRejected</c>.
		///     <para>
		///         Not yet imported from the RDB: <c>ItemTemplateProperties</c> is a prefix read that stops at the
		///         display-name string, and the fix is to teach it the tail's record grammar rather than to grep for
		///         a key. Open work tracked in Gitea #762.
		///         [V0: the item-template tail's full record grammar - what the 53xxx keys enumerate]
		///     </para>
		/// </remarks>
		public uint? OnUseSpell { get; set; }

		/// <summary>
		///     The spell line using this item locks, and for how long: the template's
		///     <c>CoreLockSpellLineTemplate</c> element (kind 53124, keys 39 = the line, 66 = the duration). It is what
		///     gives potions their shared cooldown - the line groups by the resource restored, so health <c>80000</c>,
		///     stamina <c>80100</c> and mana <c>80200</c> lock independently and drinking a health potion does not lock
		///     mana.
		/// </summary>
		/// <remarks>
		///     The wire mechanism is <c>DispatchLockTemplate</c>, not a refusal: the client greys the thing on the bar
		///     when told to lock it. Captured on a consumable in <c>NecroL80_..._login_use-consumables</c> - using the
		///     firework item 3765539 locks that item template and the two spells it casts (3510737/3510738), and the
		///     locks are re-sent at login carrying the time remaining (23.23 h on the item, 79.23 h on the spells), so
		///     a long cooldown survives a relog.
		///     <para>
		///         The duration's unit is inferred. Every observed potion states 60, and seconds is both the obvious
		///         reading and consistent with the wire's own unit (the captured lock floats are seconds), but nothing
		///         in the template says so and no capture shows a second potion being refused.
		///         [V0: the CoreLockSpellLineTemplate duration's unit]
		///     </para>
		/// </remarks>
		public uint? OnUseLockSpellLine { get; set; }

		public uint? OnUseLockSeconds { get; set; }

		/// <summary>
		///     For a resource bag item (equipped in the resource-bag slot, equip slot 26): the extra resource-container
		///     slots it adds - the sibling of <see cref="BagExtraSlots" /> for the backpack. The resource container's
		///     max_size = 48 (base) + this. RDB item-template property field 1331 (unlike the normal bag's field 104 =
		///     bag kind, resource bags carry their capacity here - live-confirmed: Max Resource Bag = [1331]=102,
		///     matching its "(102 slots)" name). Sits in the same non-monotonic tail as field 104. Null = not a
		///     resource bag. Extracted by <c>RdbItemImporter</c>; see <c>FullCharacterRoots</c>.
		/// </summary>
		public uint? ResourceBagExtraSlots { get; set; }
	}
}
