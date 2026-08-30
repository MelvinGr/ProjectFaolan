using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// An NPC HITTING a player (#900, cut out of WorldSimulation): the melee swing, the spell cast, the damage each
	// lands, and the mount a battering throws the rider off.
	//
	// The question it answers is "what does an aggroed creature do to its target", as against `CombatService`'s
	// player-initiated half ("what does the player's swing do") and `WorldSimulation`'s "what is in the world this
	// tick". ⚠️ It reaches NOTHING of the world model - both entries are handed the attacker and the victim by the
	// tick that resolved them - so it holds neither the simulation nor the registry, which is the cleanest form the
	// one-way rule takes anywhere in this split.
	//
	// Its outbound port is BOUND at Start, like its siblings'. See WorldSimulation._output.
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

		// Applies an aggroed NPC's swing to the target player: play the decoded attack-animation ack
		// (SendNpcAttackAnimation), then reduce the player's live vitals and push a 0x96B8DC59 update so their HP bar
		// drops.
		public async Task ApplyNpcAttack(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			// #401: the NPC's swing is physical, so the player's worn-gear armor rating mitigates it. LEVEL and STRENGTH
			// are part of the client's own curve - it normalises armour against a per-level target and adds Strength at
			// 0.1/point above 10 - so both are passed rather than mitigating on the raw rating alone.
			// The curve wants the attribute at the ×10 WIRE scale (its `(attribute - 10) / 10` term converts back to
			// display units), so go through AttributeWireScore rather than the display score.
			var damage = CombatResolver.ApplyPhysicalMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level, npc.RangedAttacker), target.ArmorRating, character.Level,
				target.AttributeWireScore(CharacterStatId.Strength));
			// #621: the player's shield rose finally does something in PvE. The direction is the zone the MOB SWINGS
			// FROM - the same five the player's own direction abilities pick - not where the mob is standing. The
			// player defender takes the gentler Combat_PvP* column, matching CombatService's rule, and it is applied
			// AFTER armour, like the player-swing path.
			//
			// ⚠️ DRAWN ONCE, spent twice (#673). The swing's direction is on the wire - the hit result's type word IS
			// it - so the ack below has to state the same zone the mitigation charged for. Two draws made the packet
			// and the damage disagree on every swing. The MITIGATION only sees the guard arc (three), the WIRE gets
			// the full zone (five), which is why the draw is the zone and the arc falls out of it.
			var direction = CombatShieldRose.RollSwingDirection(Random.Shared);
			damage = (int)CombatShieldRose.ApplyArcMitigation((uint)damage, target.ShieldDistribution, CombatShieldRose.ArcOf(direction), playerVersusPlayer: true);
			// Play the NPC's swing animation (the decoded attack-animation ack) BEFORE the HP move lands, so the
			// hit reads as "swing -> damage". Visual only; the authoritative health change is the PushVitals below.
			// The swing ALWAYS plays - .godmode mobs still visibly attack, they just deal no damage.
			// #623: retail puts a combo on 427 of 1,504 captured NPC melee acks, so the mob combos on some swings and
			// swings plain on the rest. Rolled per swing rather than cycled, because the corpus shows no ordering -
			// a mob alternates its generic and class combos with plain hits in no pattern. 0 = plain.
			// #675: and when it does, a mob whose weapon answers its archetype swings a CLASS row about as often as
			// its generic one (the corpus splits 92/65 over the two wired weapons). Which rank is drawn from the rows
			// its level allows - retail's level-75 mobs still swing the rank-1 row, so there is no "highest wins".
			//
			// A SHOT takes the other branch entirely (#676): there is no `combo_npc_generic_bow` row, so an archer
			// either carries a ranger row or swings plain, at the lower rate retail shows for ranged (13.6% against
			// melee's 28.4%).
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

			if (target.GodMode) // .godmode: the NPC swings and aggros normally, but the hit deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			// Retail never sends an entity-caused health record without a spell: of 656 own-handle health records the
			// only zeroes are school 12, the world (a fall, a hazard). Its physical hits name the creature's own
			// ability - `Ripper Volley` (3239371), `Rend` (3236937), `Butcher` - which are exactly the rows our own
			// `NpcSpells` carries. So a swing with NO ability sends NO health record, because retail has none to send.
			//
			// ⛔ NOT because the client would reject one. That was #902's reading and it is wrong: the client's apply
			// (`StatDamageIIR_t` slot 2, `FUN_008d5960`) reads `spellBaseId` in ONE place, `if (word3 == 10)`, and
			// nothing downstream depends on it - the health write is unconditional. The
			// `GetMagicSpell failed to load spell with id 0` in the client log comes from one of the ~80 OTHER callers,
			// not from here. A probe naming `Rend` on a pirate's swing was staged to test that theory in-client; the
			// disassembly answers it without the client, so it was dropped rather than run.
			//
			// ⚠️ Which leaves the gap where it actually is: only 35 of 1,227 NPC templates carry an ability, so most
			// swings still move no bar. That is a DATA gap (Gitea #903), and filling it is the fix - not zeroing a
			// field retail always fills.
			if (npc.AttackSpellBaseId != 0)
				await _output.SendStatDamage(client!, target.Handle, vitals.Health, -damage, (uint)DamageSchool.Generic, npc.Handle, npc.AttackSpellBaseId);

			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}.", character.Name, npc.Name);
		}

		// Retail throws a rider off their mount under fire (#142): a mounted player who soaks enough damage gets the
		// ride buff's end-twin with no press of their own. The damage accumulates on the entity rather than testing a
		// single hit because the corpus rules a per-hit threshold out - a 200,496 hit leaves the rider seated while a
		// 10,944 one throws them - so the meter is the shape that survives the evidence even though its CEILING is a
		// placeholder (MountCatalog.ThrowOffDamage, #653).
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

		// A caster NPC's tick action (#232): cast a spell at the target player (ApplySpell from the NPC handle) and
		// apply its damage, mirroring ApplyNpcAttack. The spell + effect payload come from the NPC's NpcSpell rows
		// (entity.Spells, loaded at zone load); a caster cycles through its spells. Damage is the provisional
		// melee-scaled value for now - the captured payload's magnitude isn't decoded, same interim as buffs.
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

			// Retail re-applies an NPC debuff only AFTER the prior one expires (the combat capture's ~7s
			// apply->end->re-apply cycle); the swings in between are NORMAL attacks. Re-sending the ApplySpell every 2s
			// swing makes the client STACK the icon (piling up new copies at 0s with no countdown). So if the target
			// already carries this debuff, swing normally this tick instead - the debuff keeps counting down and is
			// re-cast once the tick expires it. (#274)
			if (debuffMs > 0 && target.ActiveBuffs.ContainsKey(spell.SpellId))
			{
				await ApplyNpcAttack(npc, target);
				return;
			}

			npc.AttackSwingCounter++;
			// #401: an NPC cast is magic damage, so the player's worn-gear Protection mitigates it (the magical mirror
			// of armor mitigating melee) - same level-normalised curve, against TWK_TargetPerLevelProtection.
			// NOT `npc.RangedAttacker`: #671's factor was measured on melee HITS (DamageResultIIR), and it is the
			// mob's ARROW that lands soft, not its spell. A bow-carrying caster would otherwise have its cast nerfed
			// for holding a bow.
			var damage = CombatResolver.ApplyMagicMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level), target.Protection, character.Level,
				target.AttributeWireScore(CharacterStatId.Intelligence)); // the curve wants the ×10 wire scale
			await _output.SendNpcSpellCast(client!, npc.Handle, spell);

			// Track the NPC's cast as a timed DEBUFF on the player so it counts down and clears its icon at 0, like a
			// self-buff (#274). The duration is the cast payload's +8 field (centiseconds, see DurationMsFromPayload).
			// Only a real effect (duration > 0) is tracked - an instant damage spell carries none and leaves no icon.
			// CasterHandle = this NPC so the tick's removal twin mirrors the apply (applyTo=player, applyFrom=NPC).
			if (debuffMs > 0)
			{
				// Attach the DoT effect (Spell.DotDelta, captured StatDamageIIR) so the tick bleeds the player's health
				// while the debuff is active - Rend -8/tick etc. Looked up by base spell id (wire id >> 8). #274
				var (dotDelta, dotType, dotIntervalMs) = await _buffTicks.DotEffect(spell.SpellId >> 8);
				target.ActiveBuffs[spell.SpellId] = new ActiveBuff(spell.SpellId, npc.Handle, DateTimeOffset.UtcNow, debuffMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = DateTimeOffset.UtcNow
				};
			}

			if (target.GodMode) // .godmode: the NPC still casts, but the spell deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			// The same pool MOVE the melee swing sends - see the note there. A cast lands on the player's own handle
			// too, so it needs the StatDamageIIR just as much.
			// The CAST's own spell - a health record must name a real one or the client voids it (see
			// CombatResolver.RetailSpellBaseId).
			await _output.SendStatDamage(client!, target.Handle, vitals.Health, -damage, (uint)DamageSchool.Generic, npc.Handle, spell.SpellId >> 8);
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}'s spell.", character.Name, npc.Name);
		}
	}
}
