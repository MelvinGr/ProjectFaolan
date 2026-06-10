using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	// Builds the FullChar (0x737A6DF9) snapshot's four inventory containers entirely from the character's DB
	// items - the generation counterpart of the older in-place entry swap. Layout facts from every captured
	// snapshot (docs/player-spawn-generation-plan.md "FullChar"):
	//   - the root carries exactly 4 ProtoInventoryGame containers, kind order Equipped=101, Bag=104,
	//     Resource=112, Quest=113; inventory_id = { type: kind, instance: the character's client instance };
	//     max_size 1028/24/48/48 (24 = the starter bag; upgrades grow it) and disabled_slots_count 0/0/12/0
	//     are constant and always serialized (0 included);
	//   - entry ids: retail itemIds are 0x20D82B63 (epoch) in the high 32 bits + a server allocation counter,
	//     record ids (stat 23) another counter. Generated ids derive deterministically from instance+slot so
	//     they are unique within the character and stable across logins (equip persistence keys on template
	//     handle + slot, never on these instance ids).
	public static class FullCharSnapshotBuilder
	{
		private const ulong ItemIdEpoch = 0x20D82B63UL << 32;

		public const uint EquippedKind = 101;
		public const uint BagKind = 104;
		public const uint ResourceKind = 112;
		public const uint QuestKind = 113;

		// The first bag cell of a fresh character's snapshot is pos 1 (Strie's captured starter bag).
		private const uint BagFirstPos = 1;

		public static List<ProtoInventoryGame> BuildContainers(uint instance, IReadOnlyDictionary<ulong, (Item Item, uint Archetype)> equipped, (Item Item, uint Archetype)? bagFirst,
			IReadOnlyCollection<CharacterInventory> inventoryRows = null)
		{
			var containers = new List<ProtoInventoryGame>
			{
				NewContainer(EquippedKind, 1028, 0, instance),
				NewContainer(BagKind, 24, 0, instance),
				NewContainer(ResourceKind, 48, 12, instance),
				NewContainer(QuestKind, 48, 0, instance)
			};

			foreach (var (slot, resolved) in equipped.OrderBy(kv => kv.Key))
				containers[0].Items.Add(SnapshotItemBuilder.BuildListEntry(slot, ItemId(instance, EquippedKind, slot), RecordId(instance, EquippedKind, slot), 1, resolved.Item, resolved.Archetype));

			// CharacterInventory rows fill their container by kind (bag rows from unequip persistence; resource/
			// quest rows once something writes them). Buildable items only - a row without real identity is
			// silently absent, like the equipped path.
			var byKind = containers.ToDictionary(c => c.InventoryId.Type);
			var bagSlotsTaken = new HashSet<ulong>();
			foreach (var row in (inventoryRows ?? []).Where(r => r.Item is { Archetype: not 0 }).OrderBy(r => r.Slot))
			{
				if (!byKind.TryGetValue((uint)row.Container, out var container))
					continue;

				var slot = (ulong)row.Slot;
				container.Items.Add(SnapshotItemBuilder.BuildListEntry(slot, ItemId(instance, (uint)row.Container, slot), RecordId(instance, (uint)row.Container, slot), row.Count, row.Item, row.Item.Archetype));
				if (container.InventoryId.Type == BagKind)
					bagSlotsTaken.Add(slot);
			}

			// The seeded starter-bag item (legacy FirstPosBag nav) renders at the captured fresh-bag cell unless
			// a persisted row already occupies it.
			if (bagFirst != null && !bagSlotsTaken.Contains(BagFirstPos))
				containers[1].Items.Add(SnapshotItemBuilder.BuildListEntry(BagFirstPos, ItemId(instance, BagKind, BagFirstPos), RecordId(instance, BagKind, BagFirstPos), 1, bagFirst.Value.Item, bagFirst.Value.Archetype));

			// Captured containers list entries in ascending pos.
			foreach (var container in containers)
			{
				var sorted = container.Items.OrderBy(e => e.Pos).ToList();
				container.Items.Clear();
				container.Items.AddRange(sorted);
			}

			return containers;
		}

		private static ProtoInventoryGame NewContainer(uint kind, uint capacity, uint disabledSlots, uint instance)
		{
			return new ProtoInventoryGame
			{
				InventoryId = new ProtoID32
				{
					Type = kind,
					Instance = instance
				},
				MaxSize = capacity,
				DisabledSlotsCount = disabledSlots
			};
		}

		// Unique within the character, stable across logins: epoch | kind | slot | instance-low.
		private static ulong ItemId(uint instance, uint kind, ulong slot)
		{
			return ItemIdEpoch | ((kind & 0xFFUL) << 24) | ((slot & 0xFFUL) << 16) | (instance & 0xFFFF);
		}

		private static ulong RecordId(uint instance, uint kind, ulong slot)
		{
			return ((kind & 0xFFUL) << 24) | ((slot & 0xFFUL) << 16) | (instance & 0xFFFF);
		}
	}
}
