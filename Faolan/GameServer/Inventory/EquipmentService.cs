using Faolan.Core.Network.Protobuf;
using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Inventory
{
	// Persists the player's equip/unequip/move/drop/split actions to the DB so gear/bag changes survive logout.
	// The world-burst replay then reflects the saved CharacterInventory/CharacterEquipment on next login. Extracted
	// out of GameServerListener as a composed collaborator (like CombatService): pure persistence, no packet sends.
	// The Inbound handlers (Handle0x2000) call into it. NOTE: reflecting an item moved into an equip slot the
	// captured snapshot has no entry for (e.g. a ring) needs ADDING an equipped entry, not just the in-place replace
	// we do today - see docs/archive/spawn-dissection-plan.md.
	public sealed class EquipmentService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		public EquipmentService(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// Persists an item drop (DropItem - the client sends only the source side of ProtoMoveInventoryItemIIR) by
		// clearing the source cell, so the dropped item does not reappear in the bag on relog. (Whether a ground
		// loot bag spawns is a separate world-sim concern; this only removes it from the player's persisted bag.)
		public async Task PersistDropItem(INetworkClient client, ProtoMoveInventoryItemIIR move)
		{
			var character = client.Session.Character;
			if (character == null || move?.SourceContainerId == null)
				return;

			var kind = (int)move.SourceContainerId.Type;
			var pos = move.SourceItemPos;
			if (kind == (int)FullCharSnapshotBuilder.EquippedKind)
				return; // equipped items are unequipped, not dropped, through this path

			try
			{
				const int bag = (int)FullCharSnapshotBuilder.BagKind;
				const int firstBagPos = 1; // FullCharSnapshotBuilder.BagFirstPos

				// Dropping the legacy seeded FirstPosBag (no row backs it) clears the equipment nav instead.
				if (kind == bag && pos == firstBagPos)
				{
					var equipment = await _database.GetCharacterEquipment(character.Id);
					var rows = await _database.GetCharacterInventory(character.Id);
					if (equipment?.FirstPosBag != null && rows.All(r => !(r.Container == bag && r.Slot == firstBagPos)))
					{
						equipment.FirstPosBag = null;
						await _database.SaveCharacterEquipment(equipment);
						_logger.LogInformation("Persisted DROP: FirstPosBag (bag[{Pos}]) for character {CharacterId}.", pos, character.Id);
						return;
					}
				}

				await _database.SetCharacterInventorySlot(character.Id, kind, pos, null);
				_logger.LogInformation("Persisted DROP: {Kind}[{Pos}] for character {CharacterId}.", kind, pos, character.Id);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to persist drop {Kind}[{Pos}].", kind, pos);
			}
		}

		// Player equipped an item: bag slot `fromBagSlot` -> equip slot `targetSlot`, carrying the item's template
		// handle (the p_items/Items id). Persists targetSlot = item and consumes the bag source: the
		// CharacterInventory row at fromBagSlot when it holds this template, plus the legacy FirstPosBag nav.
		public async Task PersistEquip(INetworkClient client, CharacterEquipmentSlot targetSlot, uint itemHandle, int fromBagSlot)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			try
			{
				var item = await _database.GetItem(itemHandle);
				if (item == null)
				{
					_logger.LogInformation("Equip not persisted: item template {Handle} is not a DB item (captured-only).", itemHandle);
					return;
				}

				var equipment = await _database.GetCharacterEquipment(character.Id) ?? new CharacterEquipment
				{
					CharacterId = character.Id
				};

				equipment[targetSlot] = item;

				// The item left the bag: clear FirstPosBag if that is where it was.
				if (equipment.FirstPosBag != null && equipment.FirstPosBag.Id == item.Id)
					equipment.FirstPosBag = null;

				await _database.SaveCharacterEquipment(equipment);

				// Consume the bag row the item came from (no-op when the source was the seeded FirstPosBag or a
				// captured-only entry).
				var bagRows = await _database.GetCharacterInventory(character.Id);
				if (bagRows.Any(r => r.Container == (int)FullCharSnapshotBuilder.BagKind && r.Slot == fromBagSlot && r.ItemId == item.Id))
					await _database.SetCharacterInventorySlot(character.Id, (int)FullCharSnapshotBuilder.BagKind, fromBagSlot, null);

				_logger.LogInformation("Persisted EQUIP: {Name} ({Id}) bag[{BagSlot}] -> {Slot} for character {CharacterId}.", item.Name, item.Id, fromBagSlot, targetSlot, character.Id);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to persist equip of item {Handle} to {Slot}.", itemHandle, targetSlot);
			}
		}

		// Persists a within-inventory item move (MoveItemInInventory / ProtoMoveInventoryItemIIR) so bag slot
		// positions survive logout - the world-entry snapshot rebuilds the bag from these CharacterInventory rows
		// (FullCharSnapshotBuilder.BuildContainers), so without this the client's re-arrangement reverts on relog.
		// Handles the non-equipped grid containers (bag 104 / resource 112 / quest 113); equip/unequip moves go
		// through MoveEquippedToFromInventory (PersistEquip/PersistUnequip). Swaps when the destination cell is
		// occupied; a move onto an empty cell clears the source. The legacy seeded FirstPosBag (a CharacterEquipment
		// nav that auto-renders at bag slot 1) is materialized into rows the moment it is moved or displaced, so it
		// stops re-appearing at slot 1.
		public async Task PersistInventoryMove(INetworkClient client, ProtoMoveInventoryItemIIR move)
		{
			var character = client.Session.Character;
			if (character == null || move?.SourceContainerId == null || move.DestinationContainerId == null)
				return;

			var sKind = (int)move.SourceContainerId.Type;
			var dKind = (int)move.DestinationContainerId.Type;
			var sPos = move.SourceItemPos;
			var dPos = move.DestinationItemPos;

			const int equipped = (int)FullCharSnapshotBuilder.EquippedKind;
			if (sKind == equipped || dKind == equipped)
				return; // equip/unequip is persisted via the MoveEquippedToFromInventory path

			try
			{
				const int bag = (int)FullCharSnapshotBuilder.BagKind;
				const int firstBagPos = 1; // FullCharSnapshotBuilder.BagFirstPos - the legacy FirstPosBag cell

				var rows = await _database.GetCharacterInventory(character.Id);
				var equipment = await _database.GetCharacterEquipment(character.Id);

				// Resolve the DB item id + count at a (kind, pos), counting the legacy FirstPosBag at bag slot 1.
				(uint? Id, uint Count, bool IsFirstBag) Resolve(int kind, int pos)
				{
					var row = rows.FirstOrDefault(r => r.Container == kind && r.Slot == pos);
					if (row?.ItemId != null)
						return (row.ItemId, row.Count, false);
					if (kind == bag && pos == firstBagPos && equipment?.FirstPosBag != null)
						return (equipment.FirstPosBag.Id, 1, true);

					return (null, 1, false);
				}

				var (srcId, srcCount, srcIsFirst) = Resolve(sKind, sPos);
				if (srcId == null)
				{
					_logger.LogInformation("Inventory move not persisted: no DB item at {Kind}[{Pos}] (captured-only or empty).", sKind, sPos);
					return;
				}

				var (dstId, dstCount, dstIsFirst) = Resolve(dKind, dPos);

				// Materialize the legacy FirstPosBag into real rows when it is the moved item or the displaced one,
				// so it no longer auto-renders at slot 1 and the new positions stick.
				if ((srcIsFirst || dstIsFirst) && equipment != null)
				{
					equipment.FirstPosBag = null;
					await _database.SaveCharacterEquipment(equipment);
				}

				// Apply: item -> destination; the destination's previous occupant (if any) -> source (swap). A null
				// dstId means the destination was empty, which clears the source row.
				await _database.SetCharacterInventorySlot(character.Id, dKind, dPos, srcId, srcCount);
				await _database.SetCharacterInventorySlot(character.Id, sKind, sPos, dstId, dstCount);

				_logger.LogInformation("Persisted MOVE: item {Src} {SKind}[{SPos}] -> {DKind}[{DPos}]{Swap} for character {CharacterId}.",
					srcId,
					sKind,
					sPos,
					dKind,
					dPos,
					dstId.HasValue
						? $" (swap with {dstId})"
						: string.Empty,
					character.Id);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to persist inventory move {SKind}[{SPos}] -> {DKind}[{DPos}].", sKind, sPos, dKind, dPos);
			}
		}

		// Persists a stack split (SplitStack): the bag stack at `sourceSlot` is reduced by `splitSize` and a new
		// stack of `splitSize` is placed at `targetSlot`. Bag container only (the packet carries no container id;
		// stackable items live in the bag). A no-op when the source isn't a splittable stack (count <= splitSize).
		public async Task PersistSplitStack(INetworkClient client, int sourceSlot, int targetSlot, uint splitSize)
		{
			var character = client.Session.Character;
			if (character == null || splitSize == 0)
				return;

			try
			{
				const int bag = (int)FullCharSnapshotBuilder.BagKind;
				var rows = await _database.GetCharacterInventory(character.Id);
				var src = rows.FirstOrDefault(r => r.Container == bag && r.Slot == sourceSlot);
				if (src?.ItemId == null || src.Count <= splitSize)
				{
					_logger.LogInformation("Split not persisted: bag[{Slot}] is not a splittable stack (count <= split size).", sourceSlot);
					return;
				}

				await _database.SetCharacterInventorySlot(character.Id, bag, sourceSlot, src.ItemId, src.Count - splitSize);
				await _database.SetCharacterInventorySlot(character.Id, bag, targetSlot, src.ItemId, splitSize);
				_logger.LogInformation("Persisted SPLIT: bag[{Src}] {Total} -> bag[{Src}] {Remaining} + bag[{Dst}] {Split} for character {CharacterId}.",
					sourceSlot,
					src.Count,
					src.Count - splitSize,
					targetSlot,
					splitSize,
					character.Id);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to persist stack split bag[{Src}] -> bag[{Dst}].", sourceSlot, targetSlot);
			}
		}

		// Player unequipped the item in equip slot `fromSlot` into bag cell `targetBagSlot`. Persists fromSlot =
		// empty and parks the item as a CharacterInventory bag row at that cell, so multiple unequips coexist
		// (the old behavior parked everything into the single FirstPosBag nav - a second unequip lost the first).
		public async Task PersistUnequip(INetworkClient client, CharacterEquipmentSlot fromSlot, int targetBagSlot)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			try
			{
				var equipment = await _database.GetCharacterEquipment(character.Id);
				if (equipment == null)
					return;

				var current = equipment[fromSlot];
				if (current == null)
				{
					_logger.LogInformation("Unequip not persisted: equip slot {Slot} holds no DB item (captured-only).", fromSlot);
					return;
				}

				equipment[fromSlot] = null;
				await _database.SaveCharacterEquipment(equipment);
				await _database.SetCharacterInventorySlot(character.Id, (int)FullCharSnapshotBuilder.BagKind, targetBagSlot, current.Id);

				_logger.LogInformation("Persisted UNEQUIP: {Name} ({Id}) from {Slot} -> bag[{BagSlot}] for character {CharacterId}.", current.Name, current.Id, fromSlot, targetBagSlot, character.Id);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to persist unequip from {Slot}.", fromSlot);
			}
		}

	}
}
