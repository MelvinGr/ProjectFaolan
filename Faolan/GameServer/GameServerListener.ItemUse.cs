using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Totem of Origins template (faolan.db): a non-spell on-use item that recalls to the bind point (#206). The
		// only special-cased use left - buff consumables are all data-driven via Items.OnUseSpell.
		private const uint TotemOfOriginsId = 3514741; // TODO: Move to consts

		// Apply a consumable's on-use self-buff (Spell `spellBase`, wire id base<<8) and consume one from the slot. The
		// buff uses the LONG (54-byte) MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong) - the client rejects the
		// 50-byte short self-buff form for these (runs off the end), same as Shield/spellweave/mount buffs; confirmed
		// against the …use-consumables / …use-double-xp-potion captures. Retail no-ops (no apply, no consume) when the
		// same buff is already active - the reason the last consumable in the capture "did nothing".
		private async Task UseConsumable(INetworkClient client, Item item, int slot, uint spellBase)
		{
			var self = client.Session.Account.ClientInstance;
			var wireSpellId = spellBase << 8;

			if (_simulation.PlayerWorldEntity(client)?.ActiveBuffs.ContainsKey(wireSpellId) == true)
			{
				Logger.LogInformation("UseConsumable: '{Item}' buff {Spell} already active - no-op.", item.Name, spellBase);
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"You already have the '{item.Name}' effect.");
				return;
			}

			var spell = await Database.GetSpell(spellBase);
			if (spell == null)
			{
				Logger.LogInformation("UseConsumable: Unknown spellBase {spellBase}.", spellBase);
				return;
			}

			var durationCs = spell.DurationMs / 10;
			var nonce = 0x14000000u | (spellBase & 0x00FFFFFF);
			await SendMagicBuffIir(client, self, self, wireSpellId, SelfBuffCodec.BuildBuffDataLong(spellBase, durationCs, nonce));
			await RemoveItemFromBackpack(client, slot, 1);
		}

		// The domain side of a player USING (right-clicking) an item from a bag (#206/#64): the RightClickItem Inbound
		// handler resolves the item by its inventory location and this routes on the item's on-use effect. Buff
		// consumables (potions/food/drink/Double XP) are DATA-DRIVEN via Items.OnUseSpell; the Totem recall is the one
		// remaining non-spell special case. (Folded in from the former ItemUseService / ItemUseClassifier.)
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

			if (item.Id == TotemOfOriginsId)
			{
				await RecallToBindCommand(client); // Totem of Origin -> recall to the bound resurrection point (#206)
				return;
			}

			// A consumable with an on-use spell (Items.OnUseSpell) applies that self-buff and is consumed - the
			// data-driven path for potions/food/drink/Double XP.
			if (item.OnUseSpell is { } spellBase && spellBase != 0)
			{
				await UseConsumable(client, item, slot, spellBase);
				return;
			}

			// Surface the gap in-client (no-fallbacks rule, like the "no dialogue or shop imported yet" bubble) so an
			// un-wired consumable says so instead of silently doing nothing.
			Logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"('{item.Name}' has no on-use effect wired yet - id {item.Id}, no Items.OnUseSpell)");
		}
	}
}
