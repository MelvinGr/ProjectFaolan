using Faolan.Core.Constants;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Totem of Origins template (faolan.db): a non-spell on-use item that returns the player to their RACIAL home
		// city (#206) - NOT to the bind point, which is what the Path of Asura spell does. The only special-cased use
		// left - buff consumables are all data-driven via Items.OnUseSpell.
		private const uint TotemOfOriginsId = 3514741; // TODO: Move to consts

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
			await _buffs.SendMagicBuffIir(client, self, self, wireSpellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, BuffService.CasterLevel(client), casterIsPlayer: true));

			// #890: a consumable that ARMS a self-resurrection says so on the wire immediately, while the player is
			// alive - retail's own order is the buff above, then this, then the item leaving the bag (annotated_9
			// f6467, all in one instant). It is an OFFER: the client holds it and answers with ResurrectMe when the
			// player dies, echoing the spell id back.
			if (spell.SelfResurrectionSpell is { } resurrectSpell && resurrectSpell != 0)
			{
				await SendResurrectRequest(client, self, resurrectSpell);
				_simulation.ArmSelfResurrection(self, resurrectSpell, spell.SelfResurrectionPercent ?? 100);
			}

			// #702: and the pool the consumable actually MOVES. A health potion's whole point is the instant heal -
			// `Daggamalt` -> `Health Potion Effect 10` carries +40500 (405 HP) - and this path used to apply the buff,
			// consume the item and leave the bar exactly where it was.
			await _buffs.ApplyPoolEffects(client, self, spellBase);
			await RemoveItemFromBackpack(client, slot, 1);
			await SendConsumableCooldown(client, item);
		}

		// The consumable's SHARED COOLDOWN (#764). The template states it - `CoreLockSpellLineTemplate`, stored as
		// `Item.OnUseLockSpellLine` + `OnUseLockSeconds` - and the line groups by the resource restored, so health
		// (80000), stamina (80100) and mana (80200) lock independently: a health potion does not lock mana.
		//
		// The mechanism is `DispatchLockTemplate`, the same message a spell recast uses (#767), and it is the whole
		// answer to "what does retail send when a potion is used on cooldown": NOTHING. There is no refusal, because
		// the CLIENT greys what it has been told to lock and never sends the second use. So this needs no invented
		// rejection message - which is what the ticket was blocked on.
		//
		// One body per template in the line, items AND the spells they cast, which is what retail does: using the
		// firework 3765539 in `NecroL80_..._login_use-consumables` produces three - the item and both its spells.
		//
		// ⚠️ Two things this does NOT reproduce. Retail RE-SENDS a lock at login carrying the time REMAINING (that
		// capture's login burst restores 23.23 h on an item and 79.23 h on its spells), so a long cooldown survives a
		// relog; ours is not persisted, so it does not. And the duration's UNIT is inferred - every potion states 60 and
		// seconds is both the obvious reading and consistent with the wire's own float being seconds, but nothing states
		// it (see Item.OnUseLockSpellLine).
		private async Task SendConsumableCooldown(INetworkClient client, Item item)
		{
			if (item.OnUseLockSpellLine is not { } line || item.OnUseLockSeconds is not { } seconds || seconds == 0)
				return;

			var locked = await Database.LockedTemplateIdsForConsumableLine(line);
			foreach (var templateId in locked)
				await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchLockTemplate, s => s.WriteArray(LockTemplateCodec.Build(templateId, seconds))));

			Logger.LogDebug("Locked {Count} template(s) on consumable line {Line} for {Seconds}s after using '{Item}'.", locked.Length, line, seconds, item.Name);
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
				// #522: a `Use the <item>` quest objective ticks off the USE, not off the item arriving - the one
				// shipped Use goal is an item the quest giver hands over, so counting its arrival would solve the
				// objective before the player did anything. After the effect, so an item that is both a consumable
				// and an objective does both.
				await _questProgression.AdvanceUseGoals(client, item.Id);
				return;
			}

			// Surface the gap in-client (no-fallbacks rule, like the "no dialogue or shop imported yet" bubble) so an
			// un-wired consumable says so instead of silently doing nothing.
			Logger.LogInformation("UseInventoryItem: no use wired for '{Item}' (id {Id}).", item.Name, item.Id);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"('{item.Name}' has no on-use effect wired yet - id {item.Id}, no Items.OnUseSpell)");
		}
	}
}
