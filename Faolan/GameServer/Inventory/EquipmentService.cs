using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Inventory
{
	// Persists the player's equip/unequip/move/drop/split actions to the DB so gear/bag changes survive logout.
	// The world-burst replay then reflects the saved item instances on next login. Extracted out of
	// GameServerListener as a composed collaborator (like CombatService): pure persistence, no packet sends.
	//
	// ⚠️ Every one of these five is a MOVE, and a move goes through IDatabaseRepository.MoveCharacterItem - two column
	// UPDATEs on existing rows, never a re-mint. That is what keeps the item's crafter, its stack count and its
	// socketed gems attached to it (#750). Before item instances, an "equip" was: read the source cell's template id,
	// write it into a slot column, clear the source cell - i.e. the item was RE-CREATED at its destination from
	// whatever fields the code remembered to copy across. Every per-instance property therefore had to be listed by
	// hand at each of these call sites, a worn item had nowhere to keep one at all, and anything not listed was
	// silently dropped. Gems were exactly that. Do not reintroduce a "read the template id, write it elsewhere" pair.
	//
	// Nothing here catches. Each of these five used to run inside `try { ... } catch { LogWarning }`, which is the worst
	// possible answer for a persistence path: the CLIENT has already moved the item, so a swallowed write leaves the
	// screen and the database disagreeing, and the player finds out at the next relog when gear is missing or
	// duplicated - with nothing in the log tying it to the action. It also swallowed the deliberate throws inside these
	// same methods. Letting it throw surfaces the failure at the packet loop, which logs it against the packet that
	// caused it and keeps the connection.
	// The Inbound handlers (Handle0x2000) call into it.
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
		// in the bag is the one now worn - which is what gives #733 (the crafter on a worn item) and the socketed gems
		// on worn gear their answer for free. `itemHandle` is the template the client says it dragged; it is checked
		// against the source cell rather than used to build anything.
		public async Task PersistEquip(INetworkClient client, CharacterEquipmentSlot targetSlot, uint itemHandle, int fromBagSlot)
		{
			var character = client.Session.Character;

			var source = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bag, fromBagSlot);
			if (source == null || source.TemplateId != itemHandle)
			{
				_logger.LogInformation("Equip not persisted: bag[{BagSlot}] does not hold template {Handle} (captured-only entry or stale client state).", fromBagSlot, itemHandle);
				return;
			}

			// A swap, when the target slot is already worn: the previously worn piece lands in the bag cell the newly
			// equipped one came from, which is what the client shows.
			await _database.MoveCharacterItem(character.Id, ContainerKind.Bag, fromBagSlot, ContainerKind.Equipped, (int)targetSlot);

			_logger.LogInformation("Persisted EQUIP: {Name} ({Id}) bag[{BagSlot}] -> {Slot} for character {CharacterId}.", source.Template.Name, source.TemplateId, fromBagSlot, targetSlot,
				character.Id);
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
		// Splitting is the one inventory action that legitimately MINTS: the new half is a new item, and both halves
		// keep the crafter because both are the same stack of the same crafted thing (#727).
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

			await _database.SetCharacterItemCount(character.Id, ContainerKind.Bag, sourceSlot, src.Count - splitSize);
			await _database.SetCharacterItemSlot(character.Id, ContainerKind.Bag, targetSlot, src.TemplateId, splitSize, src.CreatorName);
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
	}
}
