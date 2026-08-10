using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
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
		// ONE INTERACTION IS ONE SWING, and a swing takes 100 of the node's 1000 health and grants exactly 1 unit - so
		// a full node is ten swings, not one. This used to empty the node in a single use and hand out 40, both of
		// which were misreadings the annotated mining session settles:
		//
		//   - "one harvest empties it" came from reading the ENDS of the earlier captures and explaining the partial
		//     values (901, 701, 382, ...) away as OTHER players on a shared node. They are the per-swing decrement.
		//     Melvin's own gold vein runs 298 -> 198 -> 98 while he narrates "-10% each time".
		//   - the 40 came from the 4th u32 of the DispatchDisplayItemLootChatMessage that follows. That word is the
		//     item's LEVEL (see HarvestCatalog.Yields); the count rides the ADD's stat_list 26, and it is 1 every time.
		//
		// The client renders health/max as the PERCENTAGE in the node's health bar - which is where the "12% silver
		// vein" / "30% gold vein" annotations come from - so the swing has to put the new health on the wire or the
		// node keeps reading full. That is the same StatIIR the respawn sequence already sends for CarsGroup.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 18:22-18:45, dynels 0x2688/0x26d6/0x269a/0x2666/0x26c0]
		//
		// Retail also REGROWS a node in place (+33/+26/+16 per 60s, per node) rather than despawning it at the floor.
		// We still despawn-and-respawn on exhaustion, which is the existing DynelVisualSequence; regrowth-in-place is
		// tracked in Gitea #732.
		private async Task HarvestResourceNode(INetworkClient client, LiveWorldObject node)
		{
			var character = client.Session.Character;

			// ⚠️ The node is identified by its DISPLAY id, not by Row.Name (#734). The record's name STRING is an
			// internal name retail itself gets wrong - the silver vein ships as "Martial Ring", a copper node as
			// "Vitalic Bronze-studded Armbands" - so keying on it left those rows silently inert. `Name` here is the
			// LDB name the player actually sees, which is also what the messages below should say.
			if (HarvestCatalog.NodeFor(node.Row.DisplayCategory, node.Row.DisplayId) is not { } harvest)
				return; // not a harvest node at all - some other destructible the player clicked

			// #746: a node type whose resource has no item row. Nine of the 25 shipped types are in this state, and it
			// is not something a player can be expected to infer from a click that does nothing - so say it, the same
			// way the exhausted and bag-full refusals do. Never silent: the alternative reads as a broken node.
			if (harvest.ResourceItemId is not { } resourceId)
			{
				Logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} has no resource item in the catalog, so {Player} was granted nothing (Gitea #746/#203).",
					harvest.Name, node.Handle, character.Name);
				await _agentServerDispatcher.CharacterSay(node.Handle, $"({harvest.Name}: not yet harvestable)", [character.Id]);
				return;
			}

			var name = harvest.Name;

			if (!node.IsHarvestable)
			{
				// Never silent: a clickable but exhausted node means a despawn we failed to deliver.
				Logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} is already exhausted; nothing granted to {Player}.", name, node.Handle, character.Name);
				await _agentServerDispatcher.CharacterSay(node.Handle, $"({name}: already harvested)", [character.Id]);
				return;
			}

			var count = HarvestCatalog.YieldFor(node.Row.DisplayId);
			if (await GrantItemToBackpack(client, resourceId, count) == null)
			{
				// Bag full: refuse the harvest ENTIRELY rather than consuming the node for nothing. The node keeps its
				// health, so the player can come back - the same grant-before-consume order the shop's buy path uses.
				Logger.LogInformation("Harvest: {Player}'s bag is full, so '{Node}' was not harvested (it keeps its health).", character.Name, name);
				await _agentServerDispatcher.CharacterSay(node.Handle, "(bag full)", [character.Id]);
				return;
			}

			var before = node.Health ?? HarvestCatalog.NodeMaxHealth;
			var after = before - HarvestCatalog.SwingCost;
			node.Health = after;
			await SendItemLootChatMessage(client, resourceId, (uint)((await Database.GetItem(resourceId))?.Level ?? 0));
			await _broadcaster.BroadcastNodeHealth(client, node, after);

			// Exhausted only when it can no longer pay for a swing. Until then the node stays in the world with its
			// health bar down, which is what the player sees in retail.
			if (after >= HarvestCatalog.SwingCost)
			{
				Logger.LogInformation("Harvest: {Player} took {Count}x item {Item} from '{Node}' 0x{Handle:X8}; health {Before} -> {After} ({Percent}%).",
					character.Name, count, resourceId, name, node.Handle, before, after, after * 100 / HarvestCatalog.NodeMaxHealth);
				return;
			}

			node.Health = HarvestCatalog.ExhaustedHealth;
			await _broadcaster.DespawnHarvestedNode(client, node);

			Logger.LogInformation("Harvest: {Player} took the last {Count}x item {Item} from '{Node}' 0x{Handle:X8}; node exhausted and despawned.",
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
			await SendMagicBuffIir(client, self, self, wireSpellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client)));

			// #702: and the pool the consumable actually MOVES. A health potion's whole point is the instant heal -
			// `Daggamalt` -> `Health Potion Effect 10` carries +40500 (405 HP) - and this path used to apply the buff,
			// consume the item and leave the bar exactly where it was.
			await ApplyPoolEffects(client, self, spellBase);
			await RemoveItemFromBackpack(client, slot, 1);
		}

		// The domain side of a player USING (right-clicking) an item from a bag (#206/#64): the RightClickItem Inbound
		// handler resolves the item by its inventory location and this routes on the item's on-use effect. Buff
		// consumables (potions/food/drink/Double XP) are DATA-DRIVEN via Items.OnUseSpell; the Totem recall is the one
		// remaining non-spell special case. (Folded in from the former ItemUseService / ItemUseClassifier.)
		private async Task UseInventoryItem(INetworkClient client, ContainerKind container, int slot)
		{
			var character = client.Session.Character;

			var item = (await Database.GetCharacterItemSlot(character.Id, container, slot))?.Template;
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
