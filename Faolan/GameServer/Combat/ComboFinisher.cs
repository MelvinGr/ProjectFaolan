using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// DELIVERING a completed melee combo (#139) - the stamina it costs, the spell its finisher lands, the damage that
	// spell does, and the shove if the combo is a knockback.
	//
	// ⚠️ This is deliberately NOT on `ComboService`, which is where it looks like it belongs. The two sit on
	// different LAYERS and the construction graph is what proves it: `ComboService` is a CATALOG (the 744-row combo
	// table, the scaling curve, per-swing step tracking) that `WorldBroadcaster` reads at spawn time to give an NPC
	// its combo ids - so it is built BELOW the broadcaster, on nothing but the database. Delivery needs the buff
	// engine, which needs the broadcaster, which needs the catalog. Folding delivery into the catalog would close
	// that loop (ComboService -> BuffService -> WorldBroadcaster -> ComboService), and the alternatives - lazy
	// delegate seams, or a port re-exporting four services - are both worse than naming the second layer.
	//
	// So: `ComboService` answers "what combo is this and what does it cost", `ComboFinisher` answers "what happens
	// when it lands".
	public sealed class ComboFinisher
	{
		private readonly BuffService _buffs;
		private readonly CombatService _combat;

		// The combo CATALOG below this layer - consulted for the stamina price of the swing that just completed.
		private readonly ComboService _combos;
		private readonly KnockbackService _knockback;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// #710: a feat or buff aimed at the finisher's spell moves its numbers, exactly as on a manual cast.
		private readonly SpellModifierService _spellModifiers;

		// #697: the procs the player's feats hang off this combo, fired when it lands.
		private readonly ProcService _procs;

		public ComboFinisher(WorldSimulation sim, KnockbackService knockback, CombatService combat, BuffService buffs, ComboService combos,
			SpellModifierService spellModifiers, ProcService procs, ILogger logger)
		{
			_sim = sim;
			_knockback = knockback;
			_combat = combat;
			_buffs = buffs;
			_combos = combos;
			_spellModifiers = spellModifiers;
			_procs = procs;
			_logger = logger;
		}

		// Delivers a completed melee combo (#139). Every step of a combo is an ordinary damaging swing - the corpus
		// is explicit about that, both steps of the captured `Slave_1_AOE_2hb_Sweep_2` carry an 81-byte hit-result
		// block - so the steps need nothing special here. What the FINISHER adds is the combo's payload, and that
		// payload is a SPELL: `Combo.GameActionId` resolves into the `Spells` catalog for 726 of the 744 shipped
		// combos, under the combo's own display name (`Assassin_20b_..._Grim_Corruption_II` -> 149094 "Grim
		// Corruption II"). So a combo's damage, its DoT and its debuff are that spell's, and the finisher runs the
		// same two ops a cast does rather than a bespoke combo-damage model.
		//
		// That also matches the shape of the measured numbers: across the captured player combos a finisher lands
		// far above the steps around it (Slave_1 Sweep 1.5x a white swing on the opener, 2.6x on the finisher;
		// Soldier_19 Brutal Enraging Strike 3.2x / 2.7x / 16.8x), and the spread between combos is per-combo -
		// which is what a per-combo spell produces and a uniform step multiplier could not.
		public async Task Deliver(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			var casterHandle = client.Session.Character.Id;
			var spellId = swing.SpellId;

			// The combo costs stamina by the client's own formula, and the client gates STARTING a combo on being
			// able to afford it - so spending it here keeps the two ends agreeing rather than letting a player
			// chain combos for free.
			await _combat.SpendComboStamina(client, await _combos.StaminaCost(
				client.Session.Character.Class, client.Session.Character.Level, _sim.GetOrAddPlayerVitals(client).MaxStamina, swing));

			// No target: the finisher still played, but there is nothing to land it on (retail's air swing).
			if (targetHandle == 0 || targetHandle == casterHandle)
				return;

			// ApplySpell takes the WIRE buff id ((base << 8) | rank); the catalog carries the bare base, same as
			// the cast path (#274).
			await _buffs.ApplySpell(client, targetHandle, casterHandle, spellId << 8);

			// #697: the WHOLE-combo damage the player's feats grant (GameAction 53209 `UpgradeCombo` - "improves the
			// damage inflicted by the Blood for Aid combos"). It lands here rather than on a step's swing because
			// this is where our model puts a combo's damage: in the finisher's spell. The per-ATTACK bonuses of
			// kind 53250 are the other half and ride the swings themselves, in SendNormalAttackResponse.
			var comboDamageBonus = _sim.PlayerWorldEntity(client)?.ComboBonus(swing.Combo.Id).SpellDamagePercentHundredths ?? 0;
			// #710: the finisher is an ordinary cast as far as the spell-scoped modifiers go - a feat or buff aimed at
			// this spell moves its crit numbers here exactly as it does on a manual cast.
			var finisherModifiers = await _spellModifiers.For(client.Session.Character.Id, _sim.PlayerWorldEntity(client), spellId);
			await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId, comboDamageBonus, finisherModifiers);

			// #697: the procs the player's feats hang off THIS combo - "Touch of Death: empowers their next combo with
			// additional unholy damage" - fire on the landing, the hit and the damage being one event here.
			await _procs.OnComboLanded(client, swing, targetHandle);

			// ⭐ And what the combo's own NAME says it does. `Guardian_20b_knockback_Shield_Sweep_II` is a knockback,
			// and 25 player combos across six classes are - `Shield Sweep`, `Clobber`, `Ferocious Smack`, `Batter
			// Aside`, `Stagger`, `Volley Backwards`. The name is the ONLY place that is stated: every one of those
			// combos resolves to a spell with a zero duration and no stat effects, so the payload says nothing.
			//
			// The shove itself is `KnockbackService.Knock`, which had no gameplay trigger at all before this - a GM
			// command was its only caller.
			await ApplyKnockback(client, swing, targetHandle);
		}

		// Shoves the victim away from the caster when the landing combo is a knockback (ComboEffect). Silent for a
		// combo that is not one, for a victim that is not in the caster's zone, and for a shove the victim resists
		// outright - all three are the service's own no-ops rather than conditions worth logging per swing.
		private async Task ApplyKnockback(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			if (!ComboEffect.IsKnockback(swing.Combo.Name))
				return;

			if (_sim.PlayerWorldEntity(client) is not { } caster)
				return;

			if (_sim.World.ZoneForEntity(caster) is not { } zone || !zone.TryGet(targetHandle, out var victim))
				return;

			if (await _knockback.Knock(victim, caster.Position))
				_logger.LogInformation("Combo '{Combo}' knocked {Victim} back.", swing.Combo.Name, victim.Name);
		}
	}
}
