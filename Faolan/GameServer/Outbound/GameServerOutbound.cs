using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count)
		{
			return GrantItemToBackpack(client, itemTemplateId, count);
		}

		Task IEconomyOutbound.SendAddItem(INetworkClient client, uint containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback)
		{
			return SendAddItem(client, containerType, itemPos, entry, feedback);
		}

		// --- IEconomyOutbound: the wallet + item-grant ops the economy/inventory services reach back to (forwarding
		// to the private Outbound builders, which the listener also calls directly from loot/GM paths) ---------------
		Task IEconomyOutbound.SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			return SendUpdateCash(client, balanceTin, reason);
		}

		Task IWorldOutbound.BroadcastEntityFade(WorldEntity entity)
		{
			return _broadcaster.BroadcastEntityFade(entity);
		}

		Task IWorldOutbound.BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			return _broadcaster.BroadcastNpcAggro(npc, target);
		}

		Task IWorldOutbound.BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			return _broadcaster.BroadcastNpcCombatMode(npc, inCombat);
		}

		Task IWorldOutbound.BroadcastNpcMove(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcMove(npc);
		}

		Task IWorldOutbound.BroadcastNpcPatrol(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcPatrol(npc);
		}

		// --- IWorldOutbound: the wire side of the simulation tick (broadcaster builds packets; listener owns vitals
		// send + the world-entry replay) -----------------------------------------------------------------------------
		Task IWorldOutbound.BroadcastNpcRespawn(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcRespawn(npc);
		}

		Task IWorldOutbound.BroadcastNpcStop(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcStop(npc);
		}

		Task IWorldOutbound.ConvertCorpseToLoot(WorldEntity npc)
		{
			return _broadcaster.ConvertCorpseToLoot(npc);
		}

		Task IWorldOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}

		Task IWorldOutbound.ReenterWorld(INetworkClient client)
		{
			return SendWorldEntry(client);
		}

		// The buff-removal end-twin (#274/#253): the same ApplySpell carrier as the apply, rank byte -> 1, empty
		// payload. Routes the private SendBuffRemoval used by .cancelbuff so the world-tick expiry and the GM cancel
		// share one builder.
		Task IWorldOutbound.SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			return SendBuffRemoval(client, applyTo, applyFrom, appliedSpellId);
		}

		Task IWorldOutbound.SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return CombatService.SendDamageNumber(client, targetHandle, attackerHandle, damage);
		}

		Task IWorldOutbound.SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathBcc(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathEffect(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathEffect(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathPose(dyingHandle, deathPosition)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildKilledBy(dyingHandle, killerEndpoint)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter)
		{
			return CombatService.SendNpcAttackAnimation(client, npcHandle, damage, swingCounter);
		}

		Task IWorldOutbound.SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return CombatService.SendNpcSpellCast(client, npcHandle, spell);
		}

		Task IWorldOutbound.SendResurrectionScreen(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return SendValidResurrectionSpots(client, deathPosition, spots);
		}

		Task IWorldOutbound.SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			// Same GameAgent 0x59 sender / receiver framing as the death sequence (retail beach #1382 uses it).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildRevive(handle, respawnPointId)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, float facingX, float facingZ)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveDestination(handle, mapId, handle, position, facingX, facingZ)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveTeleport(handle, mapId, handle, playfieldToken)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return CombatService.SendStatDamage(client, targetHandle, statValue, delta, type, casterHandle, spellBaseId);
		}
	}
}
