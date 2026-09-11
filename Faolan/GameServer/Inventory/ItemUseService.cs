using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Inventory
{
	public sealed class ItemUseService(ICatalogRepository catalog, ICharacterInventoryRepository inventory, ILogger logger, BuffService buffs, IChatServerDispatcher agentServerDispatcher, WorldSimulation simulation,
		QuestProgressionService questProgression, PlayerDeathService playerDeaths, EconomyOutbound economyOutbound, TeleportService teleport)
	{
		// Totem of Origins template: a non-spell on-use item that returns the player to their racial home city, not to
		// the bind point, which is what the Path of Asura spell does. The only special-cased use left; buff consumables
		// are data-driven through Items.OnUseSpell.
		private const uint TotemOfOriginsId = 3514741;

		// Apply a consumable's on-use self-buff (Spell `spellBase`, wire id base<<8) and consume one from the slot. The
		// buff uses the long, 54-byte MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong): the client runs off the end
		// of the 50-byte short self-buff form for these, as it does for shield, spellweave and mount buffs - confirmed
		// against the use-consumables and use-double-xp-potion captures. Retail does nothing at all (no apply, no
		// consume) when the same buff is already active.
		private async Task UseConsumable(INetworkClient client, Item item, int slot, uint spellBase)
		{
			var self = client.Session.Character.Id;
			var wireSpellId = spellBase << 8;

			if (simulation.PlayerWorldEntity(client)?.ActiveBuffs.ContainsKey(wireSpellId) == true)
			{
				logger.LogInformation("UseConsumable: '{Item}' buff {Spell} already active - no-op.", item.Name, spellBase);
				await agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"You already have the '{item.Name}' effect.");
				return;
			}

			var spell = await catalog.GetSpell(spellBase);
			if (spell == null)
			{
				logger.LogInformation("UseConsumable: Unknown spellBase {spellBase}.", spellBase);
				return;
			}

			var durationCs = spell.DurationMs / 10;
			await buffs.SendMagicBuffIir(client, self, self, wireSpellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, BuffService.CasterLevel(client), casterIsPlayer: true));

			// A consumable that arms a self-resurrection says so on the wire immediately, while the player is alive -
			// retail's order is the buff above, then this, then the item leaving the bag (annotated_9 f6467, all in one
			// instant). It is an offer: the client holds it and answers with ResurrectMe when the player dies, echoing
			// the spell id back.
			if (spell.SelfResurrectionSpell is { } resurrectSpell && resurrectSpell != 0)
			{
				await GameWire.SendResurrectRequest(client, self, resurrectSpell);
				playerDeaths.ArmSelfResurrection(self, resurrectSpell, spell.SelfResurrectionPercent ?? 100);
			}

			// The pool the consumable actually moves: a health potion's whole point is the instant heal - `Daggamalt`
			// into `Health Potion Effect 10` carries +40500, i.e. 405 HP - so the buff and the consume alone would
			// leave the bar where it was.
			await buffs.ApplyPoolEffects(client, self, spellBase);
			await economyOutbound.RemoveItemFromBackpack(client, slot, 1);
			await SendConsumableCooldown(client, item);
		}

		// The consumable's shared cooldown. The template states it - `CoreLockSpellLineTemplate`, stored as
		// `Item.OnUseLockSpellLine` and `OnUseLockSeconds` - and the line groups by the resource restored, so health
		// (80000), stamina (80100) and mana (80200) lock independently: a health potion does not lock mana.
		//
		// The mechanism is `DispatchLockTemplate`, the same message a spell recast uses. Retail sends nothing at all
		// when a potion is used on cooldown, because the client greys what it has been told to lock and never sends the
		// second use, so no rejection message is needed.
		//
		// One body per template in the line, items AND the spells they cast, which is what retail does: using the
		// firework 3765539 in `NecroL80_..._login_use-consumables` produces three - the item and both its spells.
		//
		// Two things this does not reproduce. Retail re-sends a lock at login carrying the time remaining (that
		// capture's login burst restores 23.23 h on an item and 79.23 h on its spells), so a long cooldown survives a
		// relog; this one is not persisted, so it does not. And the duration's unit is inferred - every potion states
		// 60, and seconds is both the obvious reading and consistent with the wire's own float being seconds, but
		// nothing states it (see Item.OnUseLockSpellLine).
		private async Task SendConsumableCooldown(INetworkClient client, Item item)
		{
			if (item.OnUseLockSpellLine is not { } line || item.OnUseLockSeconds is not { } seconds || seconds == 0)
				return;

			var locked = await catalog.LockedTemplateIdsForConsumableLine(line);
			foreach (var templateId in locked)
				await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchLockTemplate, s => s.WriteArray(LockTemplateCodec.Build(templateId, seconds))));

			logger.LogDebug("Locked {Count} template(s) on consumable line {Line} for {Seconds}s after using '{Item}'.", locked.Length, line, seconds, item.Name);
		}

		// The domain side of a player using (right-clicking) an item from a bag: the RightClickItem inbound handler
		// resolves the item by its inventory location and this routes on the item's on-use effect. Buff consumables
		// (potions, food, drink, Double XP) are data-driven through Items.OnUseSpell; the Totem recall is the one
		// remaining non-spell special case.
		public async Task UseInventoryItem(INetworkClient client, ContainerKind container, int slot)
		{
			var character = client.Session.Character;

			var item = (await inventory.GetCharacterItemSlot(character.Id, container, slot))?.Template;
			if (item == null)
			{
				logger.LogInformation("UseInventoryItem: no item at container {Container} slot {Slot}.", container, slot);
				return;
			}

			if (item.Id == TotemOfOriginsId)
			{
				await teleport.RecallToRacialHomeCommand(client); // the racial home city, not the bind point
				return;
			}

			// A consumable with an on-use spell (Items.OnUseSpell) applies that self-buff and is consumed - the
			// data-driven path for potions/food/drink/Double XP.
			if (item.OnUseSpell is { } spellBase && spellBase != 0)
			{
				await UseConsumable(client, item, slot, spellBase);
				// A `Use the <item>` quest objective ticks off the use, not off the item arriving: the one shipped Use
				// goal is an item the quest giver hands over, so counting its arrival would solve the objective before
				// the player did anything. After the effect, so an item that is both a consumable and an objective
				// does both.
				await questProgression.AdvanceUseGoals(client, item.Id);
				return;
			}

			// Surface the gap in-client (no-fallbacks rule, like the "no dialogue or shop imported yet" bubble) so an
			// un-wired consumable says so instead of silently doing nothing.
			logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
			await agentServerDispatcher.SendSystemMessage(character.Id, $"('{item.Name}' has no on-use effect wired yet - id {item.Id}, no Items.OnUseSpell)");
		}
	}
}
