using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using ProtoBuf;

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
		// Inventory container ID32 types seen in the captured DispatchAddItem inventoryID.
		internal const uint InventoryContainerBackpack = 0x68;
		internal const uint InventoryContainerEquipped = 0x71;

		internal static void WriteAddItemBody(FaolanStream s, uint containerType, uint charId, uint itemPos, byte[] entry, uint feedback = 0)
		{
			s
				.WriteUInt32(containerType)
				.WriteUInt32(charId)
				.WriteUInt32(itemPos)
				.WriteArrayUInt32Length(entry)
				.WriteUInt32(feedback);
		}

		internal static byte[] SerializeInventoryEntry(ProtoInventoryGameEntry entry)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, entry);
			return ms.ToArray();
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		private async Task SendAddItem(INetworkClient client, uint containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.AddItem);
			WriteAddItemBody(ps, containerType, charId, itemPos, SerializeInventoryEntry(entry), feedback);
			await ps.SendAsync(client);
		}

		// Grants an item to the player's backpack: persists it to a free CharacterInventory bag cell (so it
		// survives relog via the world-entry snapshot) AND pushes the live DispatchAddItem so it appears
		// immediately. The entry is the proven snapshot entry (SnapshotItemBuilder.BuildListEntry().Item - the
		// 3-stat form that renders in-client), and itemId/recordId use FullCharSnapshotBuilder's deterministic
		// formula so the live add and the next login's snapshot agree on the entry's identity. Returns the bag
		// slot it landed in, or null when the item isn't snapshot-buildable or the bag is full.
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1)
		{
			var item = await Database.GetItem(itemTemplateId);
			if (item is not { Archetype: not 0 })
				return null;

			var characterId = client.Session.Character.Id;
			var slot = await FindFreeBagSlot(characterId);
			if (slot == null)
				return null;

			await Database.SetCharacterInventorySlot(characterId, (int)FullCharSnapshotBuilder.BagKind, slot.Value, itemTemplateId, count);

			var entry = SnapshotItemBuilder.BuildListEntry(
				(ulong)slot.Value,
				FullCharSnapshotBuilder.ItemId(characterId, FullCharSnapshotBuilder.BagKind, (ulong)slot.Value),
				FullCharSnapshotBuilder.RecordId(characterId, FullCharSnapshotBuilder.BagKind, (ulong)slot.Value),
				count, item, item.Archetype).Item;
			await SendAddItem(client, InventoryContainerBackpack, (uint)slot.Value, entry);
			return slot;
		}

		// The first free backpack cell (0..23), skipping persisted rows and the legacy starter-bag cell (pos 1)
		// while the FirstPosBag item still renders there. Null = bag full.
		private async Task<int?> FindFreeBagSlot(uint characterId)
		{
			var equipment = await Database.GetCharacterEquipment(characterId);
			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows
				.Where(r => r.Container == (int)FullCharSnapshotBuilder.BagKind)
				.Select(r => r.Slot)
				.ToHashSet();
			if (equipment?.FirstPosBag is { Archetype: not 0 })
				taken.Add(1);

			return Enumerable
				.Range(0, 24)
				.Where(s => !taken.Contains(s))
				.Cast<int?>()
				.FirstOrDefault();
		}
	}
}
