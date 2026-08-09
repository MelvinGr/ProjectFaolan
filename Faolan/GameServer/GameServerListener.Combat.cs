using Faolan.Core.Constants;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		Task IWorldOutbound.ConvertCorpseToLoot(WorldEntity npc)
		{
			return _broadcaster.ConvertCorpseToLoot(npc);
		}

		Task IWorldOutbound.SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return CombatService.SendDamageNumber(client, targetHandle, attackerHandle, damage);
		}

		Task IWorldOutbound.SendDismount(INetworkClient client)
		{
			return SendDismount(client);
		}

		// Same one path, reached from the other side (#681): a no-mount volume unseats through exactly the record pair
		// the button press and a thrown rider do.
		Task IEnvironmentOutbound.SendDismount(INetworkClient client)
		{
			return SendDismount(client);
		}

		Task IWorldOutbound.SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(BehaviorControlCenterCodec.Build(DeathSequence.Bcc(dyingHandle)))));
		}

		Task IWorldOutbound.SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(MoveTaskCodec.Build(DeathSequence.Pose(dyingHandle, deathPosition)))));
		}

		Task IWorldOutbound.SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(SimpleCharKilledByUpdateCodec.Build(dyingHandle, killerEndpoint))));
		}

		Task IWorldOutbound.SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId, bool ranged)
		{
			return CombatService.SendNpcAttackAnimation(client, npcHandle, damage, swingCounter, direction, comboId, ranged);
		}

		Task IWorldOutbound.SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return CombatService.SendNpcSpellCast(client, npcHandle, spell);
		}

		Task IWorldOutbound.SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return CombatService.SendRegen(client, handle, healthDelta, manaDelta, staminaDelta);
		}

		Task IWorldOutbound.SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			// Same GameAgent 0x59 sender / receiver framing as the death sequence (retail beach #1382 uses it).
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(BehaviorControlCenterCodec.Build(DeathSequence.Revive(handle, respawnPointId)))));
		}

		Task IWorldOutbound.SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, float facingX, float facingZ)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, position, facingX, facingZ))));
		}

		Task IWorldOutbound.SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, playfieldToken))));
		}

		Task IWorldOutbound.SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return CombatService.SendStatDamage(client, targetHandle, statValue, delta, type, casterHandle, spellBaseId);
		}

		// IEnvironmentOutbound (#495/#504). The StatDamageIIR ENVIRONMENTAL form - no caster, no spell, and NO paired
		// DamageResult, because retail renders no floating combat number for a fall (CombatEffectBuilder).
		Task IEnvironmentOutbound.SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint amount, uint statId, uint school)
		{
			return client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(StatDamageCodec.Build(StatDamages.Environmental(targetHandle, amount, statId, school)))));
		}

		Task IEnvironmentOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return ((IWorldOutbound)this).PushVitals(client, vitals);
		}

		Task IEnvironmentOutbound.ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			return ((ISpellEffectOutbound)this).ApplySelfBuff(client, wireSpellId);
		}

		// Same op, third port: the world tick applies the death penalty at the moment of death (#504).
		Task IWorldOutbound.ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			return ((ISpellEffectOutbound)this).ApplySelfBuff(client, wireSpellId);
		}

		// Greys the spell's action-bar button and starts its cooldown sweep (#767) - `DispatchLockTemplate`, which is
		// how retail states a recast and the reason ours never appeared: we sent none of the 992 the corpus carries.
		//
		// There is no recast GameAction on the wire (0 across all 132 dumps); the cooldown IS this message. Body is
		// [u32 spellId][f32 seconds], and of the 741 captured bodies whose id resolves, 580 carry exactly the spell's
		// own `RecastCentiseconds / 100` (the rest are partials - time LEFT, on a refresh).
		//
		// ⚠️ ONE PER SPELL IN THE LINE, not one for the cast. A `Contract of Protection` cast emits five bodies in a
		// row - every rank on line 12 - each with the same 20.0. That is the wire form of `SetSpellLineRecastTime`,
		// and it is why all five ranks of a spell grey together. So this resolves the line's membership rather than
		// sending the cast id alone; a spell with no line locks only itself, matching `recastGroup`.
		private async Task SendSpellCooldown(INetworkClient client, Spell spell, long castBeganAt)
		{
			// Measured from the cast BEGIN, matching the server-side timer: the client starts counting on receipt, so
			// send what is LEFT rather than the base. A cast that outlasted its own recast is already ready.
			var remainingCs = spell.RecastCentiseconds - (Environment.TickCount64 - castBeganAt) / 10;
			if (remainingCs <= 0)
				return;

			var seconds = remainingCs / 100f;
			var locked = spell.GuiStackingLine != 0
				? await Database.SpellIdsOnLine(spell.GuiStackingLine)
				: new[] { spell.Id };

			foreach (var templateId in locked)
				await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchLockTemplate, s => s.WriteArray(LockTemplateCodec.Build(templateId, seconds))));

			Logger.LogDebug("Locked {Count} template(s) for {Seconds:F2}s after casting {Spell} ('{Name}').", locked.Length, seconds, spell.Id, spell.Name);
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
		private async Task ResolveComboFinisher(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			var casterHandle = client.Session.Character.Id;
			var spellId = swing.SpellId;

			// The combo costs stamina by the client's own formula, and the client gates STARTING a combo on being
			// able to afford it - so spending it here keeps the two ends agreeing rather than letting a player
			// chain combos for free.
			await _combat.SpendComboStamina(client, await _combos.StaminaCost(
				client.Session.Character.Class, client.Session.Character.Level, _combat.GetOrAddPlayerVitals(client).MaxStamina, swing));

			// No target: the finisher still played, but there is nothing to land it on (retail's air swing).
			if (targetHandle == 0 || targetHandle == casterHandle)
				return;

			// ApplySpell takes the WIRE buff id ((base << 8) | rank); the catalog carries the bare base, same as
			// the cast path (#274).
			await ApplySpell(client, targetHandle, casterHandle, spellId << 8);

			// #697: the WHOLE-combo damage the player's feats grant (GameAction 53209 `UpgradeCombo` - "improves the
			// damage inflicted by the Blood for Aid combos"). It lands here rather than on a step's swing because
			// this is where our model puts a combo's damage: in the finisher's spell. The per-ATTACK bonuses of
			// kind 53250 are the other half and ride the swings themselves, in SendNormalAttackResponse.
			var comboDamageBonus = _simulation.PlayerWorldEntity(client)?.ComboBonus(swing.Combo.Id).SpellDamagePercentHundredths ?? 0;
			await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId, comboDamageBonus);

			// ⭐ And what the combo's own NAME says it does. `Guardian_20b_knockback_Shield_Sweep_II` is a knockback,
			// and 25 player combos across six classes are - `Shield Sweep`, `Clobber`, `Ferocious Smack`, `Batter
			// Aside`, `Stagger`, `Volley Backwards`. The name is the ONLY place that is stated: every one of those
			// combos resolves to a spell with a zero duration and no stat effects, so the payload says nothing.
			//
			// `KnockbackService` existed with no gameplay trigger at all - a GM command was its only caller.
			await ApplyComboKnockback(client, swing, targetHandle);
		}

		// Shoves the victim away from the caster when the landing combo is a knockback (ComboEffect). Silent for a
		// combo that is not one, for a victim that is not in the caster's zone, and for a shove the victim resists
		// outright - all three are the service's own no-ops rather than conditions worth logging per swing.
		private async Task ApplyComboKnockback(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			if (!ComboEffect.IsKnockback(swing.Combo.Name))
				return;

			if (_simulation.PlayerWorldEntity(client) is not { } caster)
				return;

			if (_simulation.World.ZoneForEntity(caster) is not { } zone || !zone.TryGet(targetHandle, out var victim))
				return;

			if (await _knockback.Knock(victim, caster.Position))
				Logger.LogInformation("Combo '{Combo}' knocked {Victim} back.", swing.Combo.Name, victim.Name);
		}
	}
}
