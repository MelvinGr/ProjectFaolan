using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Inventory
{
	// Persists the player's equip, unequip, move, drop and split actions to the database so gear and bag changes
	// survive a logout; the world-burst replay then reflects the saved item instances on the next login. Pure
	// persistence: nothing here sends a packet, and the Inbound handlers (Handle0x2000) call into it.
	//
	// Every one of these five is a move, and a move goes through ICharacterRepository.MoveCharacterItem - two column
	// updates on existing rows, never a re-mint. That is what keeps the item's crafter, its stack count and its
	// socketed gems attached to it. Never reintroduce a "read the template id, write it elsewhere" pair: re-creating
	// the item at its destination means every per-instance property has to be listed by hand at each call site, a worn
	// item has nowhere to keep one at all, and anything not listed is silently dropped.
	//
	// Nothing here catches, deliberately. The client has already moved the item, so a swallowed write leaves the screen
	// and the database disagreeing and the player finds out at the next relog, with nothing in the log tying it to the
	// action; a catch would also swallow the deliberate throws inside these methods. Letting it throw surfaces the
	// failure at the packet loop, which logs it against the packet that caused it and keeps the connection.
	public sealed class EquipmentService
	{
		private readonly ICharacterInventoryRepository _database;
		private readonly ILogger _logger;

		public EquipmentService(ICharacterInventoryRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// Persists an item drop (DropItem - the client sends only the source side of ProtoMoveInventoryItemIIR) by
		// clearing the source cell, so the dropped item does not reappear in the bag on relog. (Whether a ground
		// loot bag spawns is a separate world-sim concern; this only removes it from the player's persisted bag.)
		public async Task PersistDropItem(INetworkClient client, ProtoDeleteItemIIR drop)
		{
			var character = client.Session.Character;
			if (drop.ContainerId == null)
				return;

			var kind = (ContainerKind)drop.ContainerId.Type;
			var pos = drop.ItemPos;
			if (kind == ContainerKind.Equipped)
				return; // equipped items are unequipped, not dropped, through this path

			await _database.SetCharacterItemSlot(character.Id, kind, pos, null);
			_logger.LogInformation("Persisted DROP: {Kind}[{Pos}] for character {CharacterId}.", kind, pos, character.Id);
		}

		// Player equipped an item: bag slot `fromBagSlot` -> equip slot `targetSlot`. A move, so the instance that was
		// in the bag is the one now worn, which is what keeps the crafter and the socketed gems on worn gear.
		// `itemHandle` is the template the client says it dragged; it is checked against the source cell rather than
		// used to build anything.
		//
		// Returns the instance only if this equip bound it, so the caller can send the stat-0 update; null when nothing
		// bound - the item does not bind on equip, it was already bound, or the equip was not persisted at all. A
		// returned value therefore always means "something changed that the client has not been told about".
		public async Task<ItemInstance?> PersistEquip(INetworkClient client, CharacterEquipmentSlot targetSlot, uint itemHandle, int fromBagSlot)
		{
			var character = client.Session.Character;

			var source = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bag, fromBagSlot);
			if (source == null || source.TemplateId != itemHandle)
			{
				_logger.LogInformation("Equip not persisted: bag[{BagSlot}] does not hold template {Handle} (captured-only entry or stale client state).", fromBagSlot, itemHandle);
				return null;
			}

			// A swap, when the target slot is already worn: the previously worn piece lands in the bag cell the newly
			// equipped one came from, which is what the client shows.
			await _database.MoveCharacterItem(character.Id, ContainerKind.Bag, fromBagSlot, ContainerKind.Equipped, (int)targetSlot);

			_logger.LogInformation("Persisted EQUIP: {Name} ({Id}) bag[{BagSlot}] -> {Slot} for character {CharacterId}.", source.Template.Name, source.TemplateId, fromBagSlot, targetSlot,
				character.Id);

			// Bind on equip: wearing an item whose template carries the rule binds this copy to this character
			// permanently, and the server tells the client by re-sending the cell's stat 0 with bit 26 set. The caller
			// sends that - this class is persistence only - which is why the outcome is returned rather than announced.
			//
			// A bind-on-pickup item needs nothing here: its template's own Flags word already carries the bit, so every
			// copy is born bound and the emitters send it whatever this row says.
			if (!source.Template.BindsOnEquip || source.Bound || !await _database.BindCharacterItem(source.Id))
				return null;

			_logger.LogInformation("BOUND on equip: {Name} ({Id}) is now bound to character {CharacterId}.", source.Template.Name, source.TemplateId, character.Id);

			return source;
		}

		// Persists a within-inventory item move (MoveItemInInventory / ProtoMoveInventoryItemIIR) so bag slot
		// positions survive logout - the world-entry snapshot rebuilds the containers from these instances
		// (FullCharacterRoots.Containers), so without this the client's re-arrangement reverts on relog.
		// Handles the non-equipped grid containers (bag 104 / resource 112 / quest 113); equip/unequip moves go
		// through MoveEquippedToFromInventory (PersistEquip/PersistUnequip). Swaps when the destination cell is
		// occupied.
		public async Task PersistInventoryMove(INetworkClient client, ProtoMoveInventoryItemIIR move)
		{
			var character = client.Session.Character;
			if (move?.SourceContainerId == null || move.DestinationContainerId == null)
				return;

			var sKind = (ContainerKind)move.SourceContainerId.Type;
			var dKind = (ContainerKind)move.DestinationContainerId.Type;
			if (sKind == ContainerKind.Equipped || dKind == ContainerKind.Equipped)
				return; // equip/unequip is persisted via the MoveEquippedToFromInventory path

			if (!await _database.MoveCharacterItem(character.Id, sKind, move.SourceItemPos, dKind, move.DestinationItemPos))
			{
				_logger.LogInformation("Inventory move not persisted: no item at {Kind}[{Pos}] (captured-only or empty).", sKind, move.SourceItemPos);
				return;
			}

			_logger.LogInformation("Persisted MOVE: {SKind}[{SPos}] -> {DKind}[{DPos}] for character {CharacterId}.", sKind, move.SourceItemPos, dKind, move.DestinationItemPos, character.Id);
		}

		// Persists a stack split (SplitStack): the bag stack at `sourceSlot` is reduced by `splitSize` and a new
		// stack of `splitSize` is placed at `targetSlot`. Bag container only (the packet carries no container id;
		// stackable items live in the bag). A no-op when the source isn't a splittable stack (count <= splitSize).
		//
		// Splitting is the one inventory action that legitimately mints: the new half is a new item, and both halves
		// keep the crafter because both are the same stack of the same crafted thing.
		public async Task PersistSplitStack(INetworkClient client, int sourceSlot, int targetSlot, uint splitSize)
		{
			var character = client.Session.Character;
			if (splitSize == 0)
				return;

			var src = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bag, sourceSlot);
			if (src == null || src.Count <= splitSize)
			{
				_logger.LogInformation("Split not persisted: bag[{Slot}] is not a splittable stack (count <= split size).", sourceSlot);
				return;
			}

			// The new half first: a mint refuses an occupied cell, and the source keeps its count unless the half exists.
			if (await _database.SetCharacterItemSlot(character.Id, ContainerKind.Bag, targetSlot, src.TemplateId, splitSize, src.CreatorName) == null)
			{
				_logger.LogWarning("Split not persisted: bag[{Slot}] is occupied.", targetSlot);
				return;
			}

			await _database.SetCharacterItemCount(character.Id, ContainerKind.Bag, sourceSlot, src.Count - splitSize);
			_logger.LogInformation("Persisted SPLIT: bag[{Src}] {Total} -> bag[{Src}] {Remaining} + bag[{Dst}] {Split} for character {CharacterId}.", sourceSlot, src.Count, sourceSlot,
				src.Count - splitSize, targetSlot, splitSize, character.Id);
		}

		// Player unequipped the item in equip slot `fromSlot` into bag cell `targetBagSlot`. The mirror of
		// PersistEquip, and a move for the same reason.
		public async Task PersistUnequip(INetworkClient client, CharacterEquipmentSlot fromSlot, int targetBagSlot)
		{
			var character = client.Session.Character;

			var current = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Equipped, (int)fromSlot);
			if (current == null)
			{
				_logger.LogInformation("Unequip not persisted: equip slot {Slot} holds no DB item (captured-only).", fromSlot);
				return;
			}

			await _database.MoveCharacterItem(character.Id, ContainerKind.Equipped, (int)fromSlot, ContainerKind.Bag, targetBagSlot);

			_logger.LogInformation("Persisted UNEQUIP: {Name} ({Id}) from {Slot} -> bag[{BagSlot}] for character {CharacterId}.", current.Template.Name, current.TemplateId, fromSlot,
				targetBagSlot, character.Id);
		}

		// Bulk unequip: every worn item into the backpack, in slot order, stopping when the bag runs out.
		// The easy half of the pair - each item's source slot is already known, so no resolver is involved.
		//
		// Returns what actually moved, in the order it moved, so the caller can send the wire for each. Same contract
		// as `PersistEquip`: this class persists, the caller announces.
		public async Task<IReadOnlyList<EquipmentMove>> UnequipAll(INetworkClient client, int backpackCapacity)
		{
			var characterId = client.Session.Character.Id;
			var worn = await _database.GetCharacterItems(characterId, ContainerKind.Equipped);
			var free = await FreeBagSlots(characterId, backpackCapacity);

			var moves = new List<EquipmentMove>();
			foreach (var item in worn.OrderBy(i => i.Slot))
			{
				if (free.Count == 0)
					break;

				var target = free[0];
				if (!await _database.MoveCharacterItem(characterId, ContainerKind.Equipped, item.Slot, ContainerKind.Bag, target))
					continue;

				free.RemoveAt(0);
				moves.Add(new EquipmentMove(item.TemplateId, item.Template?.Name, ContainerKind.Equipped, item.Slot, ContainerKind.Bag, target));
			}

			_logger.LogInformation("Bulk UNEQUIP for character {CharacterId}: {Moved} of {Worn} worn item(s) moved to the backpack.", characterId, moves.Count, worn.Length);
			return moves;
		}

		// Bulk equip: walk the backpack and wear whatever fits, using the client's own template fields to pick the slot
		// (`EquipSlotResolver`). An item that is not equippable, or whose every candidate slot is already taken, is
		// left in the bag - this fills the empty slots rather than deciding which of two swords is better, which is not
		// a question the template data answers.
		//
		// Bag order is the tie-break, so the outcome is reproducible: the first shirt in the bag is the one worn.
		public async Task<IReadOnlyList<EquipmentMove>> EquipAll(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var occupied = (await _database.GetCharacterItems(characterId, ContainerKind.Equipped))
				.Select(i => (CharacterEquipmentSlot)i.Slot)
				.ToHashSet();

			var moves = new List<EquipmentMove>();
			foreach (var item in (await _database.GetCharacterItems(characterId, ContainerKind.Bag)).OrderBy(i => i.Slot))
			{
				if (item.Template == null)
					continue;

				var slot = EquipSlotResolver.ResolveFreeSlot(item.Template, occupied);
				if (slot == null)
					continue;

				if (!await _database.MoveCharacterItem(characterId, ContainerKind.Bag, item.Slot, ContainerKind.Equipped, (int)slot.Value))
					continue;

				// The chosen slot and everything it consumes - a two-hander blocks the off hand of the same weapon set,
				// so nothing else may land there.
				occupied.Add(slot.Value);
				foreach (var also in EquipSlotResolver.Occupies(item.Template, slot.Value))
					occupied.Add(also);

				moves.Add(new EquipmentMove(item.TemplateId, item.Template.Name, ContainerKind.Bag, item.Slot, ContainerKind.Equipped, (int)slot.Value));
			}

			_logger.LogInformation("Bulk EQUIP for character {CharacterId}: {Moved} item(s) worn from the backpack.", characterId, moves.Count);
			return moves;
		}

		// The backpack cells with nothing in them, ascending. The capacity is the caller's to supply - it is the
		// equipped bag's business (24 base + `Item.BagExtraSlots`) and `EconomyOutbound` already answers it.
		private async Task<List<int>> FreeBagSlots(uint characterId, int capacity)
		{
			var taken = (await _database.GetCharacterItems(characterId, ContainerKind.Bag)).Select(i => i.Slot).ToHashSet();
			return Enumerable.Range(0, capacity).Where(slot => !taken.Contains(slot)).ToList();
		}
	}

	/// <summary>
	///     One item that a bulk equip / unequip actually moved. The caller sends the wire for each (a
	///     <c>DispatchRemoveItem</c> on the source cell and a <c>DispatchAddItem</c> on the destination).
	/// </summary>
	public sealed record EquipmentMove(uint TemplateId, string? Name, ContainerKind From, int FromSlot, ContainerKind To, int ToSlot);
}
