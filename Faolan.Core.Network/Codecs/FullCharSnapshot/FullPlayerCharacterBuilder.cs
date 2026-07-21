using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// Builds the FullChar (0x737A6DF9) snapshot's four inventory containers entirely from the character's DB
	// items - the generation counterpart of the older in-place entry swap. Layout facts from every captured
	// snapshot (docs/re/player-spawn-generation-plan.md "FullChar"):
	//   - the root carries exactly 4 ProtoInventoryGame containers, kind order Equipped=101, Bag=104,
	//     Resource=112, Quest=113; inventory_id = { type: kind, instance: the character's client instance };
	//     max_size 1028/24/48/48 for a fresh char and disabled_slots_count 0/0/12/0, always serialized (0 included).
	//     The Bag max_size is dynamic: 24 base + the equipped bag's Item.BagExtraSlots (#12); the rest are constant;
	//   - entry ids: retail itemIds are 0x20D82B63 (epoch) in the high 32 bits + a server allocation counter,
	//     record ids (stat 23) another counter. Generated ids derive deterministically from instance+slot so
	//     they are unique within the character and stable across logins (equip persistence keys on template
	//     handle + slot, never on these instance ids).
	public static class FullPlayerCharacterBuilder
	{
		public const ContainerKind BagKind = ContainerKind.Bag;
		public const ContainerKind BankKind = ContainerKind.Bank;
		public const ContainerKind EquippedKind = ContainerKind.Equipped;
		public const ContainerKind QuestKind = ContainerKind.Quest;
		public const ContainerKind ResourceKind = ContainerKind.Resource;

		// The server's account-entitlement policy: every account gets the full expansion + subscription set. These
		// are the root's f4/f5 (account bitfields the client reads to gate expansion content, NOT per-character
		// state); the captured faolan snapshots carry exactly these, so authoring them reproduces those roots
		// byte-identically (other captured accounts e.g. Strie carry fewer - presence is per-account).
		public const uint FullExpansionFlags = 0x000C0001;

		public const uint FullSubscriptionFlags = 0x00018181;

		// The FullChar snapshot IIR (0x737A6DF9); the listener assembles [subop][typeTag][instance][prefix][root][tails].
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR;

		// The first bag cell of a fresh character's snapshot is pos 1 (Strie's captured starter bag).
		private const uint BagFirstPos = 1;

		// Backpack sizing: the base backpack is 24 slots; the bag equipped in the bag slot (equip slot 3) adds its
		// Item.BagExtraSlots (RDB field 104) on top - Burlap Sack +24 (=48), Bag of Holding +20 (=44), Veteran's Bag
		// +35 (=59). Confirmed against fresh vs. lvl-80 snapshots. #12.
		private const uint BaseBackpackSlots = 24;

		// Resource-container sizing, the sibling of the backpack: base 48 slots + the resource bag equipped in the
		// resource-bag slot (equip slot 26) adds its Item.ResourceBagExtraSlots (RDB field 1331) - Max Resource Bag
		// +102. Mirrors the backpack's BaseBackpackSlots + BagExtraSlots.
		private const uint BaseResourceSlots = 48;
		private const uint ResourceDisabledSlots = 12;
		private const ulong BagEquipSlot = (ulong)CharacterEquipmentSlot.Bag;
		private const ulong ItemIdEpoch = 0x20D82B63UL << 32;
		private const ulong ResourceBagEquipSlot = (ulong)CharacterEquipmentSlot.ResourceBag;

		// Builds a SINGLE inventory container (ProtoInventoryGame) of the given kind from CharacterInventory rows -
		// used for the on-demand BANK delivery (kind 105), which isn't one of the 4 login-snapshot containers.
		// Same item codec as the snapshot (SnapshotItemBuilder.BuildListEntry). Includes hash-0 items too (they
		// render by template / Hash 0, like the live add) so an AH-bought item deposited to the bank still shows on
		// reopen - unlike the chat-critical world-entry snapshot, the BankIIR is its own channel, so a half-identified
		// item here can't break world entry. Rows without a resolved Item (no DB identity at all) are still skipped.
		public static ProtoInventoryGame BuildContainer(ContainerKind kind, uint capacity, uint disabledSlots, uint instance, IReadOnlyCollection<CharacterInventory> rows)
		{
			var container = NewContainer(kind, capacity, disabledSlots, instance);
			foreach (var row in (rows ?? []).Where(r => r.Item != null).OrderBy(r => r.Slot))
			{
				var slot = (ulong)row.Slot;
				container.Items.Add(SnapshotItemBuilder.BuildListEntry(slot, ItemId(instance, kind, slot), RecordId(instance, kind, slot), row.Count, row.Item, row.Item.Hash));
			}

			return container;
		}

		public static List<ProtoInventoryGame> BuildContainers(uint instance, IReadOnlyDictionary<ulong, (Item Item, uint Hash)> equipped, (Item Item, uint Hash)? bagFirst,
			IReadOnlyCollection<CharacterInventory> inventoryRows = null)
		{
			// The backpack grows by the bag equipped in the bag slot (slot 3): base 24 + its BagExtraSlots.
			var backpackSlots = BaseBackpackSlots + (equipped.TryGetValue(BagEquipSlot, out var bagItem)
				? bagItem.Item.BagExtraSlots ?? 0
				: 0);

			// The resource container grows by the resource bag equipped in the resource-bag slot (slot 26): base 48 +
			// its ResourceBagExtraSlots - the sibling of the backpack sizing above.
			var resourceSlots = BaseResourceSlots + (equipped.TryGetValue(ResourceBagEquipSlot, out var resItem)
				? resItem.Item.ResourceBagExtraSlots ?? 0
				: 0);

			var containers = new List<ProtoInventoryGame>
			{
				NewContainer(EquippedKind, 1028, 0, instance),
				NewContainer(BagKind, backpackSlots, 0, instance),
				NewContainer(ResourceKind, resourceSlots, ResourceDisabledSlots, instance),
				NewContainer(QuestKind, 48, 0, instance)
			};

			foreach (var (slot, resolved) in equipped.OrderBy(kv => kv.Key))
				containers[0].Items.Add(SnapshotItemBuilder.BuildListEntry(slot, ItemId(instance, EquippedKind, slot), RecordId(instance, EquippedKind, slot), 1, resolved.Item, resolved.Hash));

			// CharacterInventory rows fill their container by kind (bag rows from unequip persistence; resource/
			// quest rows once something writes them). Buildable items only - a row without real identity is
			// silently absent, like the equipped path.
			var byKind = containers.ToDictionary(c => (ContainerKind)c.InventoryId.Type);
			var bagSlotsTaken = new HashSet<ulong>();
			foreach (var row in (inventoryRows ?? []).Where(r => r.Item is { Hash: not 0 }).OrderBy(r => r.Slot))
			{
				if (!byKind.TryGetValue(row.Container, out var container))
					continue;

				var slot = (ulong)row.Slot;
				container.Items.Add(SnapshotItemBuilder.BuildListEntry(slot, ItemId(instance, row.Container, slot), RecordId(instance, row.Container, slot), row.Count, row.Item, row.Item.Hash));
				if ((ContainerKind)container.InventoryId.Type == BagKind)
					bagSlotsTaken.Add(slot);
			}

			// The seeded starter-bag item (legacy FirstPosBag nav) renders at the captured fresh-bag cell unless
			// a persisted row already occupies it.
			if (bagFirst != null && !bagSlotsTaken.Contains(BagFirstPos))
			{
				containers[1]
					.Items.Add(SnapshotItemBuilder.BuildListEntry(BagFirstPos, ItemId(instance, BagKind, BagFirstPos), RecordId(instance, BagKind, BagFirstPos), 1, bagFirst.Value.Item,
						bagFirst.Value.Hash));
			}

			// Captured containers list entries in ascending pos.
			foreach (var container in containers)
			{
				var sorted = container.Items.OrderBy(e => e.Pos).ToList();
				container.Items.Clear();
				container.Items.AddRange(sorted);
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
		public static ProtoFullCharacterIIR BuildRoot(IEnumerable<uint> characterTags, uint? expansionFlags, uint? subscriptionFlags, bool? hasNewClaimItems, bool? hasSpellbook,
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

		// Unique within the character, stable across logins: epoch | kind | slot | instance-low. Internal so the
		// live DispatchAddItem grant path (GrantItemToBackpack) computes the same itemId/recordId the next login's
		// snapshot will, keeping the live add and the snapshot in agreement on the entry's identity.
		public static ulong ItemId(uint instance, ContainerKind kind, ulong slot)
		{
			return ItemIdEpoch | (((uint)kind & 0xFFUL) << 24) | ((slot & 0xFFUL) << 16) | (instance & 0xFFFF);
		}

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
