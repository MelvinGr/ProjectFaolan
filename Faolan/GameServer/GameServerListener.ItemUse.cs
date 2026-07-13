using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Inventory;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The "Double XP" buff spell base (Spells 4552864, 12h). The Double XP potion applies it on use; the wire
		// ApplySpell id is this << 8 (the (base<<8)|rank convention). Confirmed in the …use-double-xp-potion capture.
		private const uint DoubleXpSpellBase = 4552864;

		// The domain side of a player USING (right-clicking) an item from a bag (#206/#64): the RightClickItem Inbound
		// handler resolves the item by its inventory location and this routes by item-use CATEGORY. Today the Totem of
		// Origin recalls the player to their bind point; future consumables (potions, food, scrolls...) plug in here.
		// (Folded in from the former ItemUseService - it already depended back on the listener via RecallToBindCommand.)
		private async Task UseInventoryItem(INetworkClient client, ContainerKind container, int slot)
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

				case ItemUseType.XpBoost:
					// Double XP potion: apply the "Double XP" self-buff (Spell 4552864, 12h) and consume one. Wire spell
					// id = base<<8. This buff uses the LONG (54-byte) MagicBuffIIR blob (BuildBuffDataLong) - the client
					// rejects the 50-byte short self-buff form for it (runs off the end), same as Shield/spellweave/mount
					// buffs; the …use-double-xp-potion capture (frame 509) is 54-byte, duration 0x41EB00 (=12h) matching
					// Spells 4552864. Retail's "does nothing if you already hold one" rule is a follow-up.
					var self = client.Session.Account.ClientInstance;
					var xpSpell = await Database.GetSpell(DoubleXpSpellBase);
					var durationCs = (xpSpell?.DurationMs ?? 0) / 10;
					var nonce = 0x14000000u | (DoubleXpSpellBase & 0x00FFFFFF);
					await SendMagicBuffIir(client, self, self, DoubleXpSpellBase << 8, SelfBuffCodec.BuildBuffDataLong(DoubleXpSpellBase, durationCs, nonce));
					await RemoveItemFromBackpack(client, slot, 1);
					break;

				default:
					Logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
					break;
			}
		}
	}
}
