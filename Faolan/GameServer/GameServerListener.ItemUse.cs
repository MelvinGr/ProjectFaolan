using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
using Faolan.Core.Network.Codecs.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Totem of Origins template (faolan.db): a non-spell on-use item that returns the player to their RACIAL home
		// city (#206) - NOT to the bind point, which is what the Path of Asura spell does. The only special-cased use
		// left - buff consumables are all data-driven via Items.OnUseSpell.
		private const uint TotemOfOriginsId = 3514741; // TODO: Move to consts

		// Harvest a resource node (#494): grant the matching resource stack and retire the node.
		//
		// The captured payoff is the ORDINARY item path - DispatchAddItem into the backpack plus a
		// DispatchDisplayItemLootChatMessage carrying (itemId, itemId, 0, count) - so nothing new goes on the wire, and
		// the resource items already ship in faolan.db. Lives here beside UseInventoryItem because the grant does.
		//
		// One harvest empties the node. That is measured at the ends, not invented in the middle: node health bottoms
		// out at 1 in every capture and the node then despawns. The partial values the captures show (901, 701, 382, ...)
		// are OTHER players harvesting the same shared world node, so they cannot be read as a per-swing decrement -
		// which is exactly why no decrement curve is modelled here. [V0: the harvest health decrement per swing]
		private async Task HarvestResourceNode(INetworkClient client, LiveWorldObject node)
		{
			var character = client.Session.Character;
			var name = node.Row.Name;
			if (HarvestCatalog.ResourceFor(name) is not { } resourceId)
				return;

			if (!node.IsHarvestable)
			{
				// Never silent: a clickable but exhausted node means a despawn we failed to deliver.
				Logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} is already exhausted; nothing granted to {Player}.", name, node.Handle, character.Name);
				await _agentServerDispatcher.CharacterSay(node.Handle, $"({name}: already harvested)", [character.Id]);
				return;
			}

			var count = HarvestCatalog.YieldFor(name);
			if (await GrantItemToBackpack(client, resourceId, count) == null)
			{
				// Bag full: refuse the harvest ENTIRELY rather than consuming the node for nothing. The node keeps its
				// health, so the player can come back - the same grant-before-consume order the shop's buy path uses.
				Logger.LogInformation("Harvest: {Player}'s bag is full, so '{Node}' was not harvested (it keeps its health).", character.Name, name);
				await _agentServerDispatcher.CharacterSay(node.Handle, "(bag full)", [character.Id]);
				return;
			}

			node.Health = HarvestCatalog.ExhaustedHealth;
			await SendItemLootChatMessage(client, resourceId, count);
			await _broadcaster.DespawnHarvestedNode(client, node);

			Logger.LogInformation("Harvest: {Player} harvested {Count}x item {Item} from '{Node}' 0x{Handle:X8}; node exhausted and despawned.",
				character.Name, count, resourceId, name, node.Handle);
		}

		// Apply a consumable's on-use self-buff (Spell `spellBase`, wire id base<<8) and consume one from the slot. The
		// buff uses the LONG (54-byte) MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong) - the client rejects the
		// 50-byte short self-buff form for these (runs off the end), same as Shield/spellweave/mount buffs; confirmed
		// against the …use-consumables / …use-double-xp-potion captures. Retail no-ops (no apply, no consume) when the
		// same buff is already active - the reason the last consumable in the capture "did nothing".
		private async Task UseConsumable(INetworkClient client, Item item, int slot, uint spellBase)
		{
			var self = client.Session.Character.Id;
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
			await SendMagicBuffIir(client, self, self, wireSpellId, MagicBuffDataCodec.Build(spellBase, durationCs, nonce, CasterLevel(client)));
			await RemoveItemFromBackpack(client, slot, 1);
		}

		// The domain side of a player USING (right-clicking) an item from a bag (#206/#64): the RightClickItem Inbound
		// handler resolves the item by its inventory location and this routes on the item's on-use effect. Buff
		// consumables (potions/food/drink/Double XP) are DATA-DRIVEN via Items.OnUseSpell; the Totem recall is the one
		// remaining non-spell special case. (Folded in from the former ItemUseService / ItemUseClassifier.)
		private async Task UseInventoryItem(INetworkClient client, ContainerKind container, int slot)
		{
			var character = client.Session.Character;

			var item = (await Database.GetCharacterInventory(character.Id)).FirstOrDefault(i => i.Container == container && i.Slot == slot)?.Item;
			if (item == null)
			{
				Logger.LogInformation("UseInventoryItem: no item at container {Container} slot {Slot}.", container, slot);
				return;
			}

			if (item.Id == TotemOfOriginsId)
			{
				await RecallToRacialHomeCommand(client); // Totem of Origins -> the RACIAL home city, not the bind point (#206)
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
