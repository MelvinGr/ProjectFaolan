using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// VALUE FACTORIES for the FullChar (0x737A6DF9) snapshot's protobuf root and its inventory containers - what
	// FullPlayerCharacterCodec.Build is handed, built entirely from the character's DB items (the generation
	// counterpart of the older in-place entry swap). Not a codec: it produces typed protobuf messages, not bytes,
	// which is why it is named for what it makes rather than carrying a `*Builder`/`*Codec` suffix (#530 rule 1).
	//
	// Layout facts from every captured snapshot (docs/re/player-spawn-generation.md "FullChar"):
	//   - the root carries exactly 4 ProtoInventoryGame containers, kind order Equipped=101, Bag=104,
	//     Resource=112, Quest=113; inventory_id = { type: kind, instance: the character's client instance };
	//     max_size 1028/24/48/48 for a fresh char and disabled_slots_count 0/0/12/0, always serialized (0 included).
	//     The Bag max_size is dynamic: 24 base + the equipped bag's Item.BagExtraSlots (#12); the rest are constant;
	//   - entry ids: retail itemIds are 0x20D82B63 (epoch) in the high 32 bits + a server allocation counter, record
	//     ids (stat 23) another counter. The counter half of the itemId is the item's own ItemInstance.Id (#750), so
	//     an entry's identity is the ITEM's and survives every move; see ItemId.
	public static class FullCharacterRoots
	{
		// The server's account-entitlement policy: every account gets the full expansion + subscription set. These
		// are the root's f4/f5 (account bitfields the client reads to gate expansion content, NOT per-character
		// state); the captured TestAccount snapshots carry exactly these, so authoring them reproduces those roots
		// byte-identically (other captured accounts e.g. Strie carry fewer - presence is per-account).
		public const uint FullExpansionFlags = 0x000C0001;

		public const uint FullSubscriptionFlags = 0x00018181;

		// Backpack sizing: the base backpack is 24 slots; the bag equipped in the bag slot (equip slot 3) adds its
		// Item.BagExtraSlots (RDB field 104) on top - Burlap Sack +24 (=48), Bag of Holding +20 (=44), Veteran's Bag
		// +35 (=59). Confirmed against fresh vs. lvl-80 snapshots. #12.
		public const uint BaseBackpackSlots = 24;

		// Resource-container sizing, the sibling of the backpack: base 48 slots + the resource bag equipped in the
		// resource-bag slot (equip slot 26) adds its Item.ResourceBagExtraSlots (RDB field 1331) - Max Resource Bag
		// +102. Mirrors the backpack's BaseBackpackSlots + BagExtraSlots.
		public const uint BaseResourceSlots = 48;
		private const uint ResourceDisabledSlots = 12;
		private const ulong ItemIdEpoch = 0x20D82B63UL << 32;

		// Builds a SINGLE inventory container (ProtoInventoryGame) of the given kind from item instances - used for the
		// on-demand BANK delivery (kind 105), which isn't one of the 4 login-snapshot containers. Same item codec as the
		// snapshot (SnapshotItems.ListEntry). Every instance renders, because every item HAS a token - harvested when a
		// capture carried one, minted otherwise (AcgHash.ForWire, #723).
		public static ProtoInventoryGame Container(ContainerKind kind, uint capacity, uint disabledSlots, uint instance, IReadOnlyCollection<ItemInstance> items)
		{
			var container = NewContainer(kind, capacity, disabledSlots, instance);
			foreach (var item in (items ?? []).OrderBy(i => i.Slot))
				container.Items.Add(SnapshotItems.ListEntry(item, ItemId(item.Id), RecordId(instance, item.Container, (ulong)item.Slot)));

			return container;
		}

		// The login snapshot's FOUR containers, built from the character's item instances - ALL of them, worn included
		// (#750). The worn set used to arrive here as a separate `equipped` dictionary lifted off a wide CharacterEquipment
		// row, with a third `bagFirst` parameter for one legacy nav that auto-rendered an item at bag cell 1 without a row
		// behind it. Both are gone: a worn item is an instance in container 101 and the starter item is an instance in the
		// bag, so there is ONE source for every entry and no cell that two sources can both claim.
		public static List<ProtoInventoryGame> Containers(uint instance, IReadOnlyCollection<ItemInstance> items)
		{
			var held = items ?? [];
			var worn = held.Where(i => i.Container == ContainerKind.Equipped).ToDictionary(i => i.Slot);

			// The backpack grows by the bag equipped in the bag slot (slot 3): base 24 + its BagExtraSlots.
			var backpackSlots = BaseBackpackSlots + (worn.TryGetValue((int)CharacterEquipmentSlot.Bag, out var bagItem)
				? bagItem.Template.BagExtraSlots ?? 0
				: 0);

			// The resource container grows by the resource bag equipped in the resource-bag slot (slot 26): base 48 +
			// its ResourceBagExtraSlots - the sibling of the backpack sizing above.
			var resourceSlots = BaseResourceSlots + (worn.TryGetValue((int)CharacterEquipmentSlot.ResourceBag, out var resItem)
				? resItem.Template.ResourceBagExtraSlots ?? 0
				: 0);

			var containers = new List<ProtoInventoryGame>
			{
				NewContainer(ContainerKind.Equipped, 1028, 0, instance),
				NewContainer(ContainerKind.Bag, backpackSlots, 0, instance),
				NewContainer(ContainerKind.Resource, resourceSlots, ResourceDisabledSlots, instance),
				NewContainer(ContainerKind.Quest, 48, 0, instance)
			};

			// Captured containers list entries in ascending pos, so the rows are ordered once here rather than sorted
			// back into place afterwards.
			var byKind = containers.ToDictionary(c => (ContainerKind)c.InventoryId.Type);
			foreach (var item in held.OrderBy(i => i.Slot))
			{
				if (byKind.TryGetValue(item.Container, out var container))
					container.Items.Add(SnapshotItems.ListEntry(item, ItemId(item.Id), RecordId(instance, item.Container, (ulong)item.Slot)));
			}

			return containers;
		}

		// Authors the whole ProtoFullCharacterIIR root from typed inputs instead of carrying the captured root
		// (#57): f1 character_tag (numeric tag ids - empty on a fresh beach char), f4 expansion_flags, f5
		// subscription_flags, f6 the DB containers, f7 has_new_claim_items, f8 has_spellbook. f2/f3
		// (free_feat_respecs / feat_resets) are absent on every captured snapshot, so they stay unset. The flags
		// are nullable so an absent field stays absent (presence is per-account): a null arg emits nothing, a
		// value emits the field (false/0 included). Reproduces any captured root byte-identically when fed that
		// root's own field values (FullCharIirRoundTripTests) - the construction oracle.
		public static ProtoFullCharacterIIR ForCharacter(IEnumerable<uint> characterTags, uint? expansionFlags, uint? subscriptionFlags, bool? hasNewClaimItems, bool? hasSpellbook,
			IEnumerable<ProtoInventoryGame> containers)
		{
			var root = new ProtoFullCharacterIIR();

			if (characterTags?.ToArray() is { Length: > 0 } tags)
				root.CharacterTags = tags;
			if (expansionFlags is { } ef)
				root.ExpansionFlags = ef;
			if (subscriptionFlags is { } sf)
				root.SubscriptionFlags = sf;
			foreach (var container in containers ?? [])
				root.Inventories.Add(container);
			if (hasNewClaimItems is { } nc)
				root.HasNewClaimItems = nc;
			if (hasSpellbook is { } hs)
				root.HasSpellbook = hs;

			return root;
		}

		// The wire's entry identity, and it is now a REAL one: retail's item_id is the 0x20D82B63 epoch in the high 32
		// bits plus a server allocation COUNTER in the low 32, and ItemInstance.Id IS that counter (#750). So the id the
		// client is handed names the actual item, survives every move, and is unique across characters.
		//
		// It used to be DERIVED from (instance, kind, slot) - the address, not the thing - which was internally
		// consistent (the live DispatchAddItem grant computed the same value the next login's snapshot would) but meant
		// the "id" changed whenever the item was moved and collided across characters on the low 16 bits of the instance.
		// Putting a real id here is free because item_id is WRITE-ONLY: the client addresses items by (container, slot)
		// and never quotes the id back - MoveInventoryItemIIR carries { source_container, source_pos, dest_container,
		// dest_pos } and DeleteItemIIR carries { container, pos, template } (measured 2026-08-09).
		public static ulong ItemId(uint instanceId)
		{
			return ItemIdEpoch | instanceId;
		}

		// The inventory RECORD id (stat 23), retail's "another counter". Still derived from the address: nothing keys on
		// it, and unlike item_id there is no measurement telling us what it counts.
		public static ulong RecordId(uint instance, ContainerKind kind, ulong slot)
		{
			return (((uint)kind & 0xFFUL) << 24) | ((slot & 0xFFUL) << 16) | (instance & 0xFFFF);
		}

		private static ProtoInventoryGame NewContainer(ContainerKind kind, uint capacity, uint disabledSlots, uint instance)
		{
			return new ProtoInventoryGame
			{
				InventoryId = new ProtoID32
				{
					Type = (uint)kind,
					Instance = instance
				},
				MaxSize = capacity,
				DisabledSlotsCount = disabledSlots
			};
		}
	}
}
