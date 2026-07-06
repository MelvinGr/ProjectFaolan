using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.GameServer.Inventory
{
    // Dispatches a player USING (right-clicking) an item from a bag - the domain side of the RightClickItem Interact,
    // extracted out of GameServerListener as a composed collaborator (like CombatService/EquipmentService). The Inbound
    // handler resolves the item by its inventory location and this routes by item: today the Totem of Origin recalls
    // the player to their bind point (#206/#64); future consumables (potions, food, scrolls...) plug in here. The
    // recall is a cross-cutting listener op (teleport/handoff), reached through a narrow delegate seam rather than by
    // passing the whole listener (which would re-introduce the god-class dependency the extraction removes).
    public sealed class ItemUseService
    {
        private readonly Func<INetworkClient, Task> _recallToBind;
        private readonly IDatabaseRepository _database;
        private readonly ILogger _logger;

        public ItemUseService(IDatabaseRepository database, Func<INetworkClient, Task> recallToBind, ILogger logger)
        {
            _database = database;
            _recallToBind = recallToBind;
            _logger = logger;
        }

        // The player right-clicked the item at (container, slot) - resolve it and run its use. Unknown/unusable items
        // are a no-op (logged). The wire behavior echo-ack is the caller's concern (a protocol detail, not item logic).
        public async Task UseInventoryItem(INetworkClient client, int container, int slot)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var item = (await _database.GetCharacterInventory(character.Id)).FirstOrDefault(i => i.Container == container && i.Slot == slot)?.Item;
            if (item == null)
            {
                _logger.LogInformation("UseInventoryItem: no item at container {Container} slot {Slot}.", container, slot);
                return;
            }

            // Dispatch on the item's use CATEGORY (ItemUseClassifier maps the stable template id), not its name.
            switch (ItemUseClassifier.Classify(item))
            {
                case ItemUseType.TotemRecall:
                {
                    await _recallToBind(client); // Totem of Origin -> recall to the bound resurrection point (#206)
                    break;
                }

                default:
                {
                    _logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
                    break;
                }
            }
        }
    }
}
