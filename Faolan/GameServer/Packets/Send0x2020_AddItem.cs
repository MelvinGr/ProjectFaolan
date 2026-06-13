using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// GameCharInterface.DispatchAddItem (s2c 0x2020) - the incremental "an item appeared in your inventory"
	// delivery. [[protocol_containeradditem]] had recorded "no server add-item packet; the snapshot is the only
	// delivery"; the RPC-opcode recovery disproved that (docs/opcode-recovery-unblocks-handoff.md §1). This is
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
	}
}
