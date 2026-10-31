using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// One item - a thing, not an address. The template it is a copy of, where it currently sits, and every
	// per-instance fact that has to survive being carried around: the stack count, the crafter, and the gems socketed
	// into it (ItemInstanceSocket).
	//
	// The table holds items; it does not address them. A slot-keyed shape - a row per (CharacterId, Container, Slot),
	// or one wide row of per-slot item FK columns - makes a per-instance property something the code has to carry by
	// hand through every move, and leaves a worn item nowhere to keep one at all. That is survivable while the only
	// per-instance facts are a count and a creator name, and not once an item can be modified: gems (see
	// ItemInstanceSocket), durability, per-instance crafted stats and timed items are all the same shape.
	//
	// Three decisions, each of which will otherwise be "fixed" later:
	//
	//  1. **The identity key is correct here**, despite the model convention forbidding an autoincrement surrogate
	//     (see AGENTS.md "Model conventions"). That rule is about tables the importer delete+reinserts, where a
	//     surrogate is reassigned on every re-harvest and churns faolan.sql. This is player state: it FKs Characters,
	//     so SqlSeedWriter.PlayerStateTables excludes it from the seed exactly like the Characters row itself, and
	//     DatabaseConventionTests only gates the content tables.
	//     The id is also what the wire wants. Retail's ProtoInventoryGameEntry.item_id is a 0x20D82B63 epoch in the
	//     high 32 bits plus a server allocation counter in the low 32 - and this is that counter
	//     (FullCharacterRoots.ItemId). It is safe to put a real identity there because item_id is write-only: the
	//     client addresses items by (container, slot) and never quotes the id back (MoveInventoryItemIIR and
	//     DeleteItemIIR both carry container + pos + template only).
	//
	//  2. Location lives on the instance, not the other way round. A move is an update of two columns on one row, so
	//     the instance and everything hanging off it survives by construction. The unique
	//     (OwnerCharacterId, Container, Slot) index is what keeps two items out of one cell.
	//
	//  3. A nullable owner is what will let this same table hold mail attachments, AH listings and loot-bag contents,
	//     with Container as the discriminator instead of a table each. Those keep their own tables for now - do not
	//     design the nullable owner out.
	//
	// Splitting a stack mints a new instance: a stack of 20 Copper is one row with Count = 20.
	public class ItemInstance
	{
		// The server allocation counter, and the low half of the wire's item_id (see the class comment). Store-
		// generated: this is the one player-state table that mints ids.
		[Key]
		public uint Id { get; set; }

		// Which container this item sits in. Equipped (101) is the worn set - the slot is then a
		// CharacterEquipmentSlot, which is what makes a worn item just an instance like any other - a worn item keeps
		// its crafter and its sockets for free.
		[ConstrainToEnumValues]
		public ContainerKind Container { get; set; }

		// Stack size (1 for non-stackable gear).
		public uint Count { get; set; } = 1;

		// Whether this instance has bound to its owner. Deliberately one bit.
		//
		// On the wire it is bit 26 of the entry's `Flags` stat (stat 0), or-ed onto the template's own `Item.Flags`
		// word by the emitters. There is no enum here on purpose: the state is binary - what varies between items is
		// the rule (per-template, `Item.BindsOnEquip`), not the state.
		//
		// Two ways it becomes true, and only one of them is stored:
		//   * bind on pickup  the template's own Flags already carries bit 26, so every copy is born bound. Nothing is
		//                     stored - `Item.Flags` says it, and the emitters send that word whatever this column is.
		//   * bind on equip   the item binds the first time it is worn (`Item.BindsOnEquip`). That is a change to this
		//                     copy and nothing in the template records it, so it is stored here.
		//
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00 - the same item's
		//  stat 0 goes 0x00000001 in the bag to 0x04000001 in the equip slot, with the player narrating both]
		//
		// It is one-way: nothing in the corpus or the client's method table unbinds an item, and the client has a
		// `CanNotBeUnbound` tooltip token implying unbinding exists for some items, so clearing it is left alone
		// rather than guessed at.
		public bool Bound { get; set; }

		// The name of the player who crafted this item, or null when it was not player-made - the client's
		// "Created by <name>" tooltip line, carried on the wire as ProtoInventoryGameEntry.creator_name (field 5, a
		// plain Latin1 string) and not as a stat - the ItemOrigin stat (0x554) does not carry it. It rides both the
		// world-entry snapshot (620 sightings across the dumps, 5 distinct crafters)
		// and the live DispatchAddItem. [V2: NecroL80_..._annotated_5 fr17401]
		[MaxLength(128)]
		public string? CreatorName { get; set; }

		// Null = the item is not in a character's possession: a guild bank item is owned by the guild, not by any
		// member (see OwnerGuildId).
		public uint? OwnerCharacterId { get; set; }

		[ForeignKey(nameof(OwnerCharacterId))]
		public virtual Character? OwnerCharacter { get; set; }

		// The guild that owns this item, for the guild-bank vault - the container whose instance on the wire
		// is the guild id rather than a character id, which is what decision 3 above was left open for.
		//
		// Exactly one of OwnerCharacterId / OwnerGuildId is set. A guild-bank item belongs to no member: it
		// survives the member who deposited it leaving the guild, which is the whole point of a shared vault, so
		// hanging it off a character would delete it with them (Character -> Items cascades).
		//
		// It gets its own unique (OwnerGuildId, Container, Slot) index for the same reason the character one exists -
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
