using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// ONE ITEM - a thing, not an address (#750). The template it is a copy of, where it currently sits, and every
	// per-instance fact that has to survive being carried around: the stack count, the crafter, and the gems socketed
	// into it (ItemInstanceSocket).
	//
	// It replaces the two shapes that came before, both of which addressed items rather than holding them:
	// CharacterInventory keyed (CharacterId, Container, Slot), and CharacterEquipment - ONE WIDE ROW with 26 per-slot
	// item FK columns. Under those, a per-instance property had to be carried by hand through every move and a WORN
	// item had nowhere to keep one at all. That was fine while the only per-instance facts were a count and a creator
	// name; it stopped being fine when an item could be MODIFIED. Gems are the case that forced it (see
	// ItemInstanceSocket); durability, per-instance crafted stats and timed items are the same shape.
	//
	// ⚠️ THREE DECISIONS, each of which will otherwise be "fixed" later:
	//
	//  1. **The identity key is CORRECT here**, despite the model convention forbidding an AUTOINCREMENT surrogate
	//     (see CLAUDE.md "Model conventions"). That rule is about tables the IMPORTER delete+reinserts, where a
	//     surrogate is reassigned on every re-harvest and churns faolan.sql. This is PLAYER STATE: it FKs Characters,
	//     so SqlSeedWriter.PlayerStateTables excludes it from the seed exactly like the Characters row itself, and
	//     DatabaseConventionTests only gates the content tables.
	//     The id is also what the WIRE wants. Retail's ProtoInventoryGameEntry.item_id is a 0x20D82B63 epoch in the
	//     high 32 bits plus a server allocation COUNTER in the low 32 - and this is that counter
	//     (FullCharacterRoots.ItemId). It is safe to put a real identity there because item_id is WRITE-ONLY: the
	//     client addresses items by (container, slot) and never quotes the id back (MoveInventoryItemIIR and
	//     DeleteItemIIR both carry container + pos + template only).
	//
	//  2. **Location lives ON the instance**, not the other way round. A move is an UPDATE of two columns on one row,
	//     so the instance and everything hanging off it survives by construction - which is the whole point of the
	//     change. The UNIQUE (OwnerCharacterId, Container, Slot) index is what keeps two items out of one cell.
	//
	//  3. **A nullable owner** is what will let this same table hold mail attachments, AH listings and loot-bag
	//     contents later, with Container as the discriminator instead of a table each. Those keep their own tables for
	//     now (#750 §8) - do not design the nullable owner out.
	//
	// Splitting a stack MINTS A NEW INSTANCE: a stack of 20 Copper is ONE row with Count = 20.
	public class ItemInstance
	{
		// The server allocation counter, and the low half of the wire's item_id (see the class comment). Store-
		// generated: this is the one player-state table that mints ids.
		[Key]
		public uint Id { get; set; }

		// Which container this item sits in. Equipped (101) is the WORN set - the slot is then a
		// CharacterEquipmentSlot, which is what makes a worn item just an instance like any other and is what gives
		// #733 (the crafter on a worn item) its home for free.
		[ConstrainToEnumValues]
		public ContainerKind Container { get; set; }

		// Stack size (1 for non-stackable gear).
		public uint Count { get; set; } = 1;

		// Whether this instance has BOUND to its owner - the per-instance half of #756, and deliberately one BIT.
		//
		// On the wire it is bit 26 of the entry's `Flags` stat (stat 0), OR-ed onto the template's own `Item.Flags`
		// word by the emitters. There is no enum here on purpose: `ItemBindingType` used to be a three-valued column
		// and the decode says the state is binary - what VARIES between items is the RULE (per-template), not the
		// state.
		//
		// Two ways it becomes true, and only one of them is stored:
		//   * BIND ON PICKUP  the TEMPLATE's own Flags already carries bit 26, so every copy is born bound. Nothing is
		//                     stored - `Item.Flags` says it, and the emitters send that word whatever this column is.
		//   * BIND ON EQUIP   the item binds the first time it is worn (`Item.BindsOnEquip`). That is a change to THIS
		//                     copy and nothing in the template records it, so it is stored here.
		//
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00 - the same item's
		//  stat 0 goes 0x00000001 in the bag to 0x04000001 in the equip slot, with the player narrating both]
		//
		// It is one-way: nothing in the corpus or the client's method table unbinds an item, and the client has a
		// `CanNotBeUnbound` tooltip token implying unbinding exists for SOME items, so clearing it is left alone
		// rather than guessed at.
		public bool Bound { get; set; }

		// The name of the player who CRAFTED this item, or null when it was not player-made - the client's
		// "Created by <name>" tooltip line, carried on the wire as ProtoInventoryGameEntry.creator_name (field 5, a
		// plain Latin1 string) and NOT as a stat. #727 predicted the carrier was the ItemOrigin stat (0x554) holding
		// the crafter's character id, on the reasoning that a u32 stat cannot hold a name; the crafting capture
		// refutes that. It rides both the world-entry snapshot (620 sightings across the dumps, 5 distinct crafters)
		// and the live DispatchAddItem. [V2: NecroL80_..._annotated_5 fr17401]
		[MaxLength(128)]
		public string? CreatorName { get; set; }

		// Null = the item is not in a character's possession - which since #654 is a state something actually
		// reaches: a GUILD BANK item is owned by the guild, not by any member (see OwnerGuildId).
		public uint? OwnerCharacterId { get; set; }

		[ForeignKey(nameof(OwnerCharacterId))]
		public virtual Character? OwnerCharacter { get; set; }

		// The GUILD that owns this item, for the guild-bank vault (#654) - the container whose instance on the wire
		// is the GUILD id rather than a character id, which is what decision 3 above was left open for.
		//
		// ⚠️ Exactly ONE of OwnerCharacterId / OwnerGuildId is set. A guild-bank item belongs to no member: it
		// survives the member who deposited it leaving the guild, which is the whole point of a shared vault, so
		// hanging it off a character would delete it with them (Character -> Items cascades).
		//
		// It gets its own UNIQUE (OwnerGuildId, Container, Slot) index for the same reason the character one exists -
		// two items must not share a cell. The two indexes cannot be one: SQLite treats NULLs as distinct, so the
		// character index does not constrain guild-owned rows at all.
		public uint? OwnerGuildId { get; set; }

		[ForeignKey(nameof(OwnerGuildId))]
		public virtual Guild? OwnerGuild { get; set; }

		// Position within the container. For Equipped it is the wire equip slot id (CharacterEquipmentSlot, vanity
		// slots included at 1000 + the base slot).
		public int Slot { get; set; }

		// The gems socketed into this item, if any.
		public virtual ICollection<ItemInstanceSocket> Sockets { get; set; } = [];

		[ForeignKey(nameof(TemplateId))]
		public virtual Item Template { get; set; } = null!;

		// The catalog item this is a copy of.
		public uint TemplateId { get; set; }
	}
}
