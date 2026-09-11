using System;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     An NPC hitting a player: the melee swing, the spell cast, the damage each lands, and the mount a battering
	///     throws the rider off.
	/// </summary>
	/// <remarks>
	///     It answers "what does an aggroed creature do to its target", as against <see cref="CombatService" />'s
	///     player-initiated half and <see cref="WorldSimulation" />'s "what is in the world this tick". It reaches
	///     nothing of the world model - both entries are handed the attacker and the victim by the tick that resolved
	///     them - so it holds neither the simulation nor the registry. Its outbound port is bound at
	///     <see cref="WorldSimulation.Start" />, as its siblings' are.
	/// </remarks>
	public sealed class NpcCombatService
	{
		private readonly BuffTickService _buffTicks;
		private readonly ILogger _logger;

		private IWorldOutbound _output = null!;

		public NpcCombatService(BuffTickService buffTicks, ILogger logger)
		{
			_buffTicks = buffTicks;
			_logger = logger;
		}

		/// <summary>Binds the outbound port. Called from <see cref="WorldSimulation.Start" />, once.</summary>
		public void BindOutbound(IWorldOutbound output)
		{
			_output = output;
		}

		/// <summary>
		///     Applies an aggroed NPC's swing to the target player: play the attack-animation ack, then reduce the
		///     player's live vitals and push the update that drops their health bar.
		/// </summary>
		public async Task ApplyNpcAttack(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			// The NPC's swing is physical, so the player's worn-gear armour rating mitigates it. Level and Strength are
			// part of the client's own curve - it normalises armour against a per-level target and adds Strength at
			// 0.1 per point above 10 - so both are passed rather than mitigating on the raw rating alone. The curve
			// wants the attribute at the x10 wire scale, its (attribute - 10) / 10 term converting back to display
			// units, hence AttributeWireScore rather than the display score.
			var damage = CombatResolver.ApplyPhysicalMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level, npc.RangedAttacker), target.ArmorRating, character.Level,
				target.AttributeWireScore(CharacterStatId.Strength));
			// The player's shield rose applies in PvE. The direction is the zone the mob swings from - the same five
			// the player's own direction abilities pick - not where the mob is standing. A player defender takes the
			// gentler Combat_PvP* column, matching CombatService's rule, and it is applied after armour, as the
			// player-swing path does.
			//
			// The direction is drawn once and spent twice: it is on the wire, since the hit result's type word is the
			// direction, so the ack below must state the same zone the mitigation charged for. The mitigation sees
			// only the guard arc (three), the wire gets the full zone (five), which is why the draw is the zone and
			// the arc falls out of it.
			var direction = CombatShieldRose.RollSwingDirection(Random.Shared);
			damage = (int)CombatShieldRose.ApplyArcMitigation((uint)damage, target.ShieldDistribution, CombatShieldRose.ArcOf(direction), playerVersusPlayer: true);
			// Play the NPC's swing animation before the health move lands, so the hit reads as swing then damage. It
			// is visual only; the authoritative health change is the PushVitals below. The swing always plays: a
			// godmode target still sees the mob attack, it simply takes no damage.
			//
			// Retail puts a combo on 427 of 1,504 captured NPC melee acks, so a mob combos on some swings and swings
			// plain on the rest. Rolled per swing rather than cycled, because the corpus shows no ordering - a mob
			// alternates its generic and class combos with plain hits in no pattern. A combo id of 0 is a plain swing.
			// When it does combo, a mob whose weapon answers its archetype swings a class row about as often as its
			// generic one (the corpus splits 92/65 over the two wired weapons). The rank is drawn from the rows its
			// level allows: retail's level-75 mobs still swing the rank-1 row, so there is no highest-wins rule.
			//
			// A shot takes the other branch entirely: there is no combo_npc_generic_bow row, so an archer either
			// carries a ranger row or swings plain, at the lower rate retail shows for ranged - 13.6% against melee's
			// 28.4%.
			var classCombo = npc.ClassComboIds.Length > 0
				? npc.ClassComboIds[Random.Shared.Next(npc.ClassComboIds.Length)]
				: 0;

			uint comboId;
			if (npc.RangedAttacker)
			{
				comboId = classCombo != 0 && Random.Shared.NextDouble() < NpcWeapon.RangedComboShare
					? classCombo
					: 0;
			}
			else
			{
				comboId = npc.MeleeComboId != 0 && Random.Shared.NextDouble() < NpcWeapon.MeleeComboShare
					? npc.MeleeComboId
					: 0;
				if (comboId != 0 && classCombo != 0 && Random.Shared.NextDouble() < NpcWeapon.ClassComboShare)
					comboId = classCombo;
			}

			await _output.SendNpcAttackAnimation(client!, npc.Handle, (uint)damage, ++npc.AttackSwingCounter, direction, comboId, npc.RangedAttacker);

			if (target.GodMode) // the NPC swings and aggros normally, but the hit deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob rather than to the player
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage puts the player in combat, halting regen
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // the floating hit number
			// Retail never sends an entity-caused health record without a spell: of 656 own-handle health records the
			// only zeroes are school 12, the world - a fall or a hazard. Its physical hits name the creature's own
			// ability (Ripper Volley 3239371, Rend 3236937, Butcher), which are exactly the rows NpcSpells carries, so
			// a swing with no ability sends no health record, because retail has none to send.
			//
			// The guard is not there because the client would reject a zero. The client's apply (StatDamageIIR_t slot
			// 2, FUN_008d5960) reads spellBaseId only inside `if (deathReason == 10)` and the health write above it is
			// unconditional; in-client, a record naming Rend carrying idx 1, a real statValue and a 39 HP delta moved
			// the bar exactly as little as a zero did.
			//
			// The real gap is the data: only 35 of 1,227 NPC templates carry an ability, so most swings still move no
			// bar. Filling that in is the fix, not zeroing a field retail always fills.
			if (npc.AttackSpellBaseId != 0)
				await _output.SendStatDamage(client!, target.Handle, vitals.Health, -damage, (uint)DamageSchool.Generic, npc.Handle, npc.AttackSpellBaseId);

			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}.", character.Name, npc.Name);
		}

		/// <summary>
		///     Retail throws a rider off their mount under fire: a mounted player who soaks enough damage gets the ride
		///     buff's end-twin without pressing anything.
		/// </summary>
		/// <remarks>
		///     The damage accumulates on the entity rather than testing a single hit, because the corpus rules a
		///     per-hit threshold out - a 200,496 hit leaves the rider seated while a 10,944 one throws them - so a
		///     running meter is the shape that survives the evidence. Its ceiling,
		///     <c>MountCatalog.ThrowOffDamage</c>, is a placeholder.
		/// </remarks>
		private async Task ThrowFromMountIfBattered(WorldEntity target, int damage)
		{
			if (!target.IsMounted || damage <= 0)
				return;

			target.MountDamageTaken += damage;
			if (target.MountDamageTaken < MountCatalog.ThrowOffDamage)
				return;

			_logger.LogInformation("Mount: {Player} was thrown from their mount after {Damage} damage.", target.Client?.Session.Character?.Name, target.MountDamageTaken);
			await _output.SendDismount(target.Client!);
		}

		/// <summary>
		///     A caster NPC's tick action: cast a spell at the target player and apply its damage, mirroring
		///     <see cref="ApplyNpcAttack" />.
		/// </summary>
		/// <remarks>
		///     The spell and its effect payload come from the NPC's <c>NpcSpell</c> rows, loaded at zone load, and a
		///     caster cycles through them. The damage is the melee-scaled value: the captured payload's magnitude is
		///     not decoded, the same interim the buffs are on.
		/// </remarks>
		public async Task ApplyNpcCast(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null || npc.Spells.Count == 0)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			var spell = npc.Spells[(int)(npc.AttackSwingCounter % (uint)npc.Spells.Count)];
			var debuffMs = ActiveBuff.DurationMsFromPayload(spell.Payload);

			// Retail re-applies an NPC debuff only after the prior one expires - the combat capture's roughly 7 s
			// apply, end, re-apply cycle - and the swings in between are normal attacks. Re-sending the ApplySpell on
			// every swing makes the client stack the icon, piling up new copies at 0 s with no countdown. So if the
			// target already carries this debuff, swing normally this tick instead: the debuff keeps counting down and
			// is re-cast once the tick expires it.
			if (debuffMs > 0 && target.ActiveBuffs.ContainsKey(spell.SpellId))
			{
				await ApplyNpcAttack(npc, target);
				return;
			}

			npc.AttackSwingCounter++;
			// An NPC cast is magic damage, so the player's worn-gear Protection mitigates it - the magical mirror of
			// armour mitigating melee, on the same level-normalised curve against TWK_TargetPerLevelProtection. The
			// ranged factor is deliberately not passed: it was measured on melee hits, and it is the mob's arrow that
			// lands soft, not its spell, so a bow-carrying caster must not have its cast nerfed for holding a bow.
			var damage = CombatResolver.ApplyMagicMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level), target.Protection, character.Level,
				target.AttributeWireScore(CharacterStatId.Intelligence)); // the curve wants the x10 wire scale
			await _output.SendNpcSpellCast(client!, npc.Handle, spell);

			// Track the NPC's cast as a timed debuff on the player so it counts down and clears its icon at 0, as a
			// self-buff does. The duration is the cast payload's +8 field, in centiseconds; see DurationMsFromPayload.
			// Only a real effect, with a duration above 0, is tracked: an instant damage spell carries none and leaves
			// no icon. The caster handle is this NPC, so the tick's removal twin mirrors the apply.
			if (debuffMs > 0)
			{
				// Attach the damage-over-time effect so the tick bleeds the player's health while the debuff is
				// active - Rend at -8 per tick, and so on. Looked up by base spell id, the wire id shifted right by 8.
				var (dotDelta, dotType, dotIntervalMs) = await _buffTicks.DotEffect(spell.SpellId >> 8);
				target.ActiveBuffs[spell.SpellId] = new ActiveBuff(spell.SpellId, npc.Handle, DateTimeOffset.UtcNow, debuffMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = DateTimeOffset.UtcNow
				};
			}

			if (target.GodMode) // the NPC still casts, but the spell deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob rather than to the player
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage puts the player in combat, halting regen
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // the floating hit number
			// The same pool move the melee swing sends; see the note there. A cast lands on the player's own handle
			// too, and states the cast's own spell, because a health record names a real ability as retail's does.
			await _output.SendStatDamage(client!, target.Handle, vitals.Health, -damage, (uint)DamageSchool.Generic, npc.Handle, spell.SpellId >> 8);
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}'s spell.", character.Name, npc.Name);
		}
	}
}
