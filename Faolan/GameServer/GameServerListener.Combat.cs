using Faolan.Core.Constants;
using System;
using System.Collections.Generic;
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

		Task IWorldOutbound.SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, Quaternion rotation)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, position, rotation))));
		}

		async Task IWorldOutbound.SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// #792: the zero-length 0x1B goes first - retail precedes EVERY IntendedTeleport with it, 102 of 102.
			await SendTeleportPending(client);
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			await client.SendAsync(new Rpc(PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, playfieldToken))));
		}

		Task IWorldOutbound.SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return CombatService.SendStatDamage(client, targetHandle, statValue, delta, type, casterHandle, spellBaseId);
		}

		Task<IReadOnlyList<uint>> IWorldOutbound.ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets)
		{
			return _combat.ApplySpellArea(client, sourceHandle, effectSpellId, radius, maxTargets);
		}

		// #776: the apply carrier for a buff landing on someone else. Goes through SendMagicBuffIir like every other
		// ApplySpell we send - so the viewers around the target get it too - and because applyTo != applyFrom that
		// path registers nothing, which is right: the world tick owns a mob's buff registry.
		Task IWorldOutbound.SendSpellApplication(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId, uint durationCentiseconds)
		{
			return _buffs.SendMagicBuffIir(client, applyTo, applyFrom, wireSpellId, MagicBuffDatas.ApplyNow(wireSpellId >> 8, durationCentiseconds, BuffService.CasterLevel(client), _buffs.CasterIsPlayer(client, applyFrom)));
		}

		Task IWorldOutbound.ApplyNpcDotTick(INetworkClient client, uint npcHandle, uint casterHandle, int delta, uint spellId)
		{
			return _combat.ApplyNpcDotTick(client, npcHandle, casterHandle, delta, spellId);
		}

		Task<IReadOnlyList<SpellAreaEffect>> IWorldOutbound.AuraAreasFor(uint spellId)
		{
			return _spellAreas.AuraAreas(spellId);
		}

		Task<IReadOnlyList<SpellAreaEffect>> IWorldOutbound.RepeatingChainsFor(uint spellId)
		{
			return _spellAreas.RepeatingChains(spellId);
		}

		Task<IReadOnlyList<SpellAreaEffect>> IWorldOutbound.ChainedSpellsFor(uint spellId)
		{
			return _spellAreas.ChainedSpells(spellId);
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

	}
}
