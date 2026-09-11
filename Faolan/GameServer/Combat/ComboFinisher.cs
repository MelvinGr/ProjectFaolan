using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Delivers a completed melee combo: the stamina it costs, the spell its finisher lands, the damage that spell
	///     does, and the shove if the combo is a knockback.
	/// </summary>
	/// <remarks>
	///     Deliberately not on <see cref="ComboService" />, because the two sit on different layers and the
	///     construction graph must stay acyclic. <see cref="ComboService" /> is a catalog - the 744-row combo table,
	///     the scaling curve, per-swing step tracking - that <see cref="WorldBroadcaster" /> reads at spawn time to
	///     give an NPC its combo ids, so it is built below the broadcaster on nothing but the database. Delivery needs
	///     the buff engine, which needs the broadcaster, which needs the catalog, so folding delivery into the catalog
	///     would close that loop. <see cref="ComboService" /> answers "what combo is this and what does it cost";
	///     this answers "what happens when it lands".
	/// </remarks>
	public sealed class ComboFinisher
	{
		private readonly BuffService _buffs;
		private readonly CombatService _combat;

		// The combo catalog below this layer - consulted for the stamina price of the swing that just completed.
		private readonly ComboService _combos;
		private readonly KnockbackService _knockback;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// A feat or buff aimed at the finisher's spell moves its numbers, exactly as on a manual cast.
		private readonly SpellModifierService _spellModifiers;

		// The procs the player's feats hang off this combo, fired when it lands.
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

		/// <summary>
		///     Delivers a completed melee combo.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Every step of a combo is an ordinary damaging swing - both steps of the captured
		///         <c>Slave_1_AOE_2hb_Sweep_2</c> carry an 81-byte hit-result block - so the steps need nothing
		///         special here. What the finisher adds is the combo's payload, and that payload is a spell:
		///         <c>Combo.GameActionId</c> resolves into the <c>Spells</c> catalog for 726 of the 744 shipped
		///         combos, under the combo's own display name (<c>Assassin_20b_..._Grim_Corruption_II</c> -> 149094
		///         "Grim Corruption II"). A combo's damage, DoT and debuff are that spell's, so the finisher runs the
		///         same two ops a cast does rather than a bespoke combo-damage model.
		///     </para>
		///     <para>
		///         The measured numbers have the same shape: across the captured player combos a finisher lands far
		///         above the steps around it (Slave_1 Sweep 1.5x a white swing on the opener, 2.6x on the finisher;
		///         Soldier_19 Brutal Enraging Strike 3.2x / 2.7x / 16.8x), and the spread between combos is per-combo,
		///         which is what a per-combo spell produces and a uniform step multiplier could not.
		///     </para>
		/// </remarks>
		public async Task Deliver(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			var casterHandle = client.Session.Character.Id;
			var spellId = swing.SpellId;

			// The combo costs stamina by the client's own formula, and the client gates starting a combo on being
			// able to afford it - so spending it here keeps the two ends agreeing rather than letting a player
			// chain combos for free.
			await _combat.SpendComboStamina(client, await _combos.StaminaCost(
				client.Session.Character.Class, client.Session.Character.Level, _sim.GetOrAddPlayerVitals(client).MaxStamina, swing));

			// No target: the finisher still played, but there is nothing to land it on (retail's air swing).
			if (targetHandle == 0 || targetHandle == casterHandle)
				return;

			// ApplySpell takes the wire buff id ((base << 8) | rank); the catalog carries the bare base, same as the
			// cast path.
			await _buffs.ApplySpell(client, targetHandle, casterHandle, spellId << 8);

			// The whole-combo damage the player's feats grant (GameAction 53209 `UpgradeCombo` - "improves the damage
			// inflicted by the Blood for Aid combos"). It lands here rather than on a step's swing because this is
			// where this model puts a combo's damage: in the finisher's spell. The per-attack bonuses of kind 53250
			// are the other half and ride the swings themselves, in SendNormalAttackResponse.
			var comboDamageBonus = _sim.PlayerWorldEntity(client)?.ComboBonus(swing.Combo.Id).SpellDamagePercentHundredths ?? 0;
			// The finisher is an ordinary cast as far as the spell-scoped modifiers go - a feat or buff aimed at this
			// spell moves its crit numbers here exactly as it does on a manual cast.
			var finisherModifiers = await _spellModifiers.For(client.Session.Character.Id, _sim.PlayerWorldEntity(client), spellId);
			await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId, comboDamageBonus, finisherModifiers);

			// The procs the player's feats hang off this combo - "Touch of Death: empowers their next combo with
			// additional unholy damage" - fire on the landing, the hit and the damage being one event here.
			await _procs.OnComboLanded(client, swing, targetHandle);

			// And what the combo's own name says it does: `Guardian_20b_knockback_Shield_Sweep_II` is a knockback, as
			// are 30 player combos across six classes - `Shield Sweep`, `Clobber`, `Ferocious Smack`, `Batter Aside`,
			// `Stagger`, `Volley Backwards`. The name is the only place that is stated, since every one of those
			// combos resolves to a spell with a zero duration and no stat effects.
			await ApplyKnockback(client, swing, targetHandle);
		}

		/// <summary>
		///     Shoves the victim away from the caster when the landing combo is a knockback (see
		///     <see cref="ComboEffect" />).
		/// </summary>
		/// <remarks>
		///     Silent for a combo that is not one, for a victim outside the caster's zone, and for a shove the victim
		///     resists outright - all three are ordinary no-ops rather than conditions worth logging per swing.
		/// </remarks>
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
