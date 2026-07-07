using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.GameServer;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// GameCharInterface.DispatchRemoveItem (s2c 0x201F) - the live "an item left your inventory" counterpart to
	// DispatchAddItem (0x2020). Until now there was NO way to undo a mid-session grant: an .additem'd item could
	// only be cleared by relog (it just wouldn't be in the next snapshot). This pushes the removal live on the same
	// GameCharAgent conversation the add / wallet (0x2005) updates ride.
	//
	// Wire body (big-endian, decoded from the gameplay/questcomplete captures, golden-tested in DispatchRemoveItemTests):
	//   i_cInventoryID   : ID32   [u32 containerType][u32 charId]   (0x68 = backpack, 0x71 = equipped - same as AddItem)
	//   i_nItemPos       : u32    slot within that container
	//   i_nAmountToRemove: u32    quantity to remove (0 in every captured remove - the equipped-slot clear / "all")
	public partial class GameServerListener
	{
		// Removes an item from the player's backpack: clears the persisted CharacterInventory bag cell (so it stays
		// gone across relog) AND pushes the live DispatchRemoveItem so it disappears immediately. The mirror of
		// GrantItemToBackpack. Returns true if a row was cleared.
		private async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			var rows = await Database.GetCharacterInventory(characterId);
			var present = false;
			foreach (var r in rows)
			{
				if (r.Container == (int)FullCharSnapshotBuilder.BagKind && r.Slot == slot)
					present = true;
			}

			if (!present)
				return false;

			await Database.SetCharacterInventorySlot(characterId, (int)FullCharSnapshotBuilder.BagKind, slot, null);
			await SendRemoveItem(client, InventoryContainerBackpack, (uint)slot, amountToRemove);
			return true;
		}

		// Pushes a live item removal to the client (the inverse of SendAddItem).
		private async Task SendRemoveItem(INetworkClient client, uint containerType, uint itemPos, uint amountToRemove = 0)
		{
			var charId = client.Session.Character.Id;

			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchRemoveItem);
			RemoveItemBody.WriteRemoveItemBody(ps, containerType, charId, itemPos, amountToRemove);
			await ps.SendAsync(client);
		}

	}
}
