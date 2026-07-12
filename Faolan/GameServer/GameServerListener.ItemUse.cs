using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.GameServer.Inventory;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The domain side of a player USING (right-clicking) an item from a bag (#206/#64): the RightClickItem Inbound
		// handler resolves the item by its inventory location and this routes by item-use CATEGORY. Today the Totem of
		// Origin recalls the player to their bind point; future consumables (potions, food, scrolls...) plug in here.
		// (Folded in from the former ItemUseService - it already depended back on the listener via RecallToBindCommand.)
		private async Task UseInventoryItem(INetworkClient client, int container, int slot)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var item = (await Database.GetCharacterInventory(character.Id)).FirstOrDefault(i => i.Container == (ContainerKind)container && i.Slot == slot)?.Item;
			if (item == null)
			{
				Logger.LogInformation("UseInventoryItem: no item at container {Container} slot {Slot}.", container, slot);
				return;
			}

			// Dispatch on the item's use CATEGORY (ItemUseClassifier maps the stable template id), not its name.
			switch (ItemUseClassifier.Classify(item))
			{
				case ItemUseType.TotemRecall:
					await RecallToBindCommand(client); // Totem of Origin -> recall to the bound resurrection point (#206)
					break;

				default:
					Logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
					break;
			}
		}
	}
}
