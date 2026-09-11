using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     Value factories for the FullChar snapshot's protobuf root and its inventory containers - what
	///     <c>FullPlayerCharacterCodec.Build</c> is handed, built entirely from the character's DB items.
	/// </summary>
	/// <remarks>
	///     Not a codec: it produces typed protobuf messages rather than bytes, which is why it is named for what it makes.
	///     <para>
	///         Layout facts from every captured snapshot (docs/re/player-spawn-generation.md, "FullChar"): the root
	///         carries exactly four <c>ProtoInventoryGame</c> containers, kind order Equipped=101, Bag=104, Resource=112,
	///         Quest=113, with <c>inventory_id = { type: kind, instance: the character's client instance }</c>, max_size
	///         1028/24/48/48 for a fresh character and disabled_slots_count 0/0/12/0, always serialized (0 included). The
	///         Bag max_size is dynamic: 24 base plus the equipped bag's <c>Item.BagExtraSlots</c>; the rest are constant.
	///     </para>
	///     <para>
	///         Entry ids: retail itemIds are the 0x20D82B63 epoch in the high 32 bits plus a server allocation counter,
	///         and record ids (stat 23) another counter. The counter half of the itemId is the item's own
	///         <c>ItemInstance.Id</c>, so an entry's identity is the item's and survives every move; see <c>ItemId</c>.
	///     </para>
	/// </remarks>
	public static class FullCharacterRoots
	{
		// The server's account-entitlement policy: every account gets the full expansion and subscription set. These are the
		// root's f4/f5 - account bitfields the client reads to gate expansion content, not per-character state - and the
		// captured TestAccount snapshots carry exactly these, so authoring them reproduces those roots byte-identically.
		// Other captured accounts carry fewer: presence is per-account.
		public const uint FullExpansionFlags = 0x000C0001;

		public const uint FullSubscriptionFlags = 0x00018181;

		// Backpack sizing: the base backpack is 24 slots, and the bag equipped in the bag slot (equip slot 3) adds its
		// Item.BagExtraSlots (RDB field 104) on top - Burlap Sack +24 (=48), Bag of Holding +20 (=44), Veteran's Bag +35
		// (=59). Confirmed against fresh and level-80 snapshots.
		public const uint BaseBackpackSlots = 24;

		// Resource-container sizing, the sibling of the backpack: base 48 slots, and the resource bag equipped in the
		// resource-bag slot (equip slot 26) adds its Item.ResourceBagExtraSlots (RDB field 1331) - Max Resource Bag +102.
		public const uint BaseResourceSlots = 48;
		private const uint ResourceDisabledSlots = 12;
		private const ulong ItemIdEpoch = 0x20D82B63UL << 32;

		// Builds a single inventory container (ProtoInventoryGame) of the given kind from item instances - used for the
		// on-demand bank delivery (kind 105), which is not one of the four login-snapshot containers. Same item codec as the
		// snapshot (SnapshotItems.ListEntry). Every instance renders, because every item has a token: harvested when a
		// capture carried one, minted otherwise (AcgHash.ForWire).
		public static ProtoInventoryGame Container(ContainerKind kind, uint capacity, uint disabledSlots, uint instance, IReadOnlyCollection<ItemInstance> items)
		{
			var container = NewContainer(kind, capacity, disabledSlots, instance);
			foreach (var item in (items ?? []).OrderBy(i => i.Slot))
				container.Items.Add(SnapshotItems.ListEntry(item, ItemId(item.Id), RecordId(instance, item.Container, (ulong)item.Slot)));

			return container;
		}

		// The login snapshot's four containers, built from the character's item instances, worn items included. A worn item
		// is an instance in container 101 and the starter item is an instance in the bag, so there is one source for every
		// entry and no cell that two sources can both claim.
		public static List<ProtoInventoryGame> Containers(uint instance, IReadOnlyCollection<ItemInstance> items)
		{
			var held = items ?? [];
			var worn = held.Where(i => i.Container == ContainerKind.Equipped).ToDictionary(i => i.Slot);

			// The backpack grows by the bag equipped in the bag slot (slot 3): base 24 plus its BagExtraSlots.
			var backpackSlots = BaseBackpackSlots + (worn.TryGetValue((int)CharacterEquipmentSlot.Bag, out var bagItem)
				? bagItem.Template.BagExtraSlots ?? 0
				: 0);

			// The resource container grows by the resource bag equipped in the resource-bag slot (slot 26): base 48 plus its
			// ResourceBagExtraSlots - the sibling of the backpack sizing above.
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

			// Captured containers list entries in ascending pos, so the rows are ordered once here rather than sorted back into
			// place afterwards.
			var byKind = containers.ToDictionary(c => (ContainerKind)c.InventoryId.Type);
			foreach (var item in held.OrderBy(i => i.Slot))
			{
				if (byKind.TryGetValue(item.Container, out var container))
					container.Items.Add(SnapshotItems.ListEntry(item, ItemId(item.Id), RecordId(instance, item.Container, (ulong)item.Slot)));
			}

			return containers;
		}

		// Authors the whole ProtoFullCharacterIIR root from typed inputs: f1 character_tag (numeric tag ids, empty on a
		// fresh beach character), f4 expansion_flags, f5 subscription_flags, f6 the DB containers, f7 has_new_claim_items,
		// f8 has_spellbook. f2/f3 (free_feat_respecs / feat_resets) are absent on every captured snapshot, so they stay
		// unset. The flags are nullable so an absent field stays absent - presence is per-account - with a null argument
		// emitting nothing and a value emitting the field, false or 0 included. Reproduces any captured root byte-identically
		// when fed that root's own field values (FullCharIirRoundTripTests).
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

		// The wire's entry identity: retail's item_id is the 0x20D82B63 epoch in the high 32 bits plus a server allocation
		// counter in the low 32, and ItemInstance.Id is that counter. The id the client is handed therefore names the actual
		// item, survives every move, and is unique across characters.
		//
		// Putting a real id here is free because item_id is write-only: the client addresses items by (container, slot) and
		// never quotes the id back - MoveInventoryItemIIR carries { source_container, source_pos, dest_container, dest_pos }
		// and DeleteItemIIR carries { container, pos, template }.
		public static ulong ItemId(uint instanceId)
		{
			return ItemIdEpoch | instanceId;
		}

		// The inventory record id (stat 23), retail's "another counter". Derived from the address: nothing keys on it, and
		// unlike item_id there is no measurement saying what it counts.
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
