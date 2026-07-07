using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Codecs.GameServer;
using Faolan.Core.Network.Protobuf;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// GameCharInterface.DispatchAddItem (s2c 0x2020) - the incremental "an item appeared in your inventory"
	// delivery. [[protocol_containeradditem]] had recorded "no server add-item packet; the snapshot is the only
	// delivery"; the RPC-opcode recovery disproved that (docs/archive/opcode-recovery-unblocks-handoff.md §1). This is
	// the live, mid-session counterpart to the world-entry FullChar snapshot, and the keystone for granting
	// loot / vendor-buy / quest-reward / mail items without a relog.
	//
	// Wire body (big-endian framing, decoded from the strickenbla questcomplete capture, golden-tested in
	// DispatchAddItemTests):
	//   i_cInventoryID : ID32   [u32 containerType][u32 charId]   (0x68 = backpack, 0x71 = equipped)
	//   i_nItemPos     : u32    slot within that container
	//   i_cEntry       : u32 len + ProtoInventoryGameEntry        (the bare InventoryListEntry.item, no Pos wrapper)
	//   i_nFeedback    : u32    (always 0 in the captures)
	public partial class GameServerListener
	{
		// Inventory container ID32 types seen in the captured DispatchAddItem inventoryID (these equal the ContainerKind
		// numbers: 0x68 = 104 Bag, 0x71 = 113 Quest). The Casilda-chain captures (casilda_quest_first_stage /
		// compeltefirstquest) deliver the quest key via a live add to 0x71 - the quest-items tab (#261).
		internal const uint InventoryContainerBackpack = 0x68;
		internal const uint InventoryContainerQuest = 0x71;

		// The first free cell (0..capacity-1) in `container`, skipping persisted rows. For the backpack it also skips the
		// legacy starter-bag cell (pos 1) while the FirstPosBag item still renders there. Null = container full.
		private async Task<int?> FindFreeContainerSlot(uint characterId, int container, int capacity)
		{
			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows.Where(r => r.Container == container).Select(r => r.Slot).ToHashSet();
			if (container == (int)FullCharSnapshotBuilder.BagKind)
			{
				var equipment = await Database.GetCharacterEquipment(characterId);
				if (equipment?.FirstPosBag is { Hash: not 0 })
					taken.Add(1);
			}

			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// Grants an item to the player's backpack: persists it to a free CharacterInventory bag cell (so it
		// survives relog via the world-entry snapshot) AND pushes the live DispatchAddItem so it appears
		// immediately. The entry is the proven snapshot entry (SnapshotItemBuilder.BuildListEntry().Item - the
		// 3-stat form that renders in-client), and itemId/recordId use FullCharSnapshotBuilder's deterministic
		// formula so the live add and the next login's snapshot agree on the entry's identity. Returns the bag
		// slot it landed in, or null when the item isn't snapshot-buildable or the bag is full.
		// `pushLiveAdd`: send the live DispatchAddItem so the item appears immediately. LOOT passes false: retail sends
		// NO inventory-add for normal loot - the client moves the looted item from the bag to the backpack LOCALLY
		// (confirmed by LootRemoveItems), so a server push would double it. Loot still PERSISTS here (DB write) so the
		// item survives relog via the world-entry snapshot.
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, bool pushLiveAdd = true)
		{
			var item = await Database.GetItem(itemTemplateId);
			if (item == null)
				return null;

			var characterId = client.Session.Character.Id;

			// Quest items (Type 113, e.g. the Scavenger's Key) route to the QUEST container, not the backpack, so they
			// land in the player's quest-items tab (#261). They are kept even with hash 0 (quest items often have
			// none); normal bag items still require an hash so the snapshot can render them.
			var isQuestItem = item.Type == ItemType.Quest;
			if (!isQuestItem && item.Hash == 0)
				return null;

			var kind = isQuestItem
				? FullCharSnapshotBuilder.QuestKind
				: FullCharSnapshotBuilder.BagKind;
			var capacity = isQuestItem
				? 48
				: 24; // quest tab holds 48 (NewContainer(QuestKind, 48)); bag 24
			var slot = await FindFreeContainerSlot(characterId, (int)kind, capacity);
			if (slot == null)
				return null;

			await Database.SetCharacterInventorySlot(characterId, (int)kind, slot.Value, itemTemplateId, count);

			if (!pushLiveAdd)
				return slot;

			// Live DispatchAddItem so the item appears without a relog. Quest items go to the QUEST container (0x71),
			// matching retail's chain-capture delivery; everything else to the backpack (0x68).
			var containerType = isQuestItem
				? InventoryContainerQuest
				: InventoryContainerBackpack;
			var entry = SnapshotItemBuilder.BuildListEntry((ulong)slot.Value,
					FullCharSnapshotBuilder.ItemId(characterId, kind, (ulong)slot.Value),
					FullCharSnapshotBuilder.RecordId(characterId, kind, (ulong)slot.Value),
					count,
					item,
					item.Hash)
				.Item;
			await SendAddItem(client, containerType, (uint)slot.Value, entry);
			return slot;
		}

		// Re-delivers, via the proven live DispatchAddItem (0x2020) path, the persisted BAG items the world-entry
		// snapshot DROPPED because they carry no hash (Hash == 0 - e.g. an AH-bought item whose template
		// we harvested from a listing, which has no hash). The snapshot only renders Hash != 0 rows
		// (FullCharSnapshotBuilder), and embedding a half-identified item in the chat-critical 0x737A6DF9 root is
		// risky, so these ride the mid-session add-item mechanism (which renders by template / Hash 0) once at login.
		// Items WITH an hash already rode the snapshot, so this only touches the dropped ones (no duplication).
		private async Task RedeliverNonSnapshotBagItems(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null)
				return;

			var rows = await Database.GetCharacterInventory(character.Id);
			var pushed = 0;
			foreach (var row in rows.Where(r => r.Container == (int)FullCharSnapshotBuilder.BagKind && r.ItemId != null && r.Item is { Hash: 0 }))
			{
				var slot = (ulong)row.Slot;
				var entry = SnapshotItemBuilder.BuildInventoryEntry(FullCharSnapshotBuilder.ItemId(character.Id, FullCharSnapshotBuilder.BagKind, slot),
					row.Item,
					0,
					[
						new SnapshotItemBuilder.ItemStat(0, 1),
						new SnapshotItemBuilder.ItemStat(23, (uint)FullCharSnapshotBuilder.RecordId(character.Id, FullCharSnapshotBuilder.BagKind, slot)),
						new SnapshotItemBuilder.ItemStat(26, row.Count)
					],
					row.Item.Level);
				await SendAddItem(client, InventoryContainerBackpack, (uint)row.Slot, entry);
				pushed++;
			}

			if (pushed > 0)
				Logger.LogInformation("Re-delivered {Count} hash-less bag item(s) to '{Name}' via live add (not in the snapshot).", pushed, character.Name);
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		private async Task SendAddItem(INetworkClient client, uint containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchAddItem);
			AddItemBody.WriteAddItemBody(ps, containerType, charId, itemPos, AddItemBody.SerializeInventoryEntry(entry), feedback);
			await ps.SendAsync(client);
		}

	}
}
