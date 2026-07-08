using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
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

			var item = (await Database.GetCharacterInventory(character.Id)).FirstOrDefault(i => i.Container == container && i.Slot == slot)?.Item;
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
