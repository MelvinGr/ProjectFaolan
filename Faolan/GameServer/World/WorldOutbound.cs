using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Everything the world TICK sends or triggers, in one place (#826). This used to be 42 members on
	///     <c>GameServerListener</c> - <c>GameServerListener.World.cs</c> was 135 lines of nothing but
	///     <c>return _broadcaster.X(...)</c> - because the listener was the only object that existed late enough to
	///     hold every collaborator. It was never the listener's behaviour.
	/// </summary>
	/// <remarks>
	///     Built AFTER the services and handed to the simulation at <c>WorldSimulation.Start</c>, which is what keeps
	///     the ctor graph acyclic: <c>WorldBroadcaster</c> takes the simulation, so nothing that reaches through here
	///     can be a constructor argument of the thing that uses it.
	///     <para>
	///         The five <c>GameServerListener.Send*</c> calls below are <c>internal static</c> wire builders that still
	///         live in <c>GameServerListener.Outbound.cs</c> with the rest of the listener's senders. They are called
	///         statically - no listener INSTANCE is referenced - and moving them out belongs with the wider
	///         <c>.Outbound</c> extraction rather than this change.
	///     </para>
	/// </remarks>
	public sealed class WorldOutbound : IWorldOutbound
	{
		private readonly WorldBroadcaster _broadcaster;
		private readonly LootBroadcaster _loot;
		private readonly NpcBroadcaster _npcs;
		private readonly WorldObjectService _worldObjects;
		private readonly BuffService _buffs;
		private readonly CombatService _combat;
		private readonly TradeskillService _tradeskill;

		// The one op that genuinely stays on the listener: dismounting persists the mount to the DB and pushes the
		// stat + viewer broadcast, so it spans the listener's own state. A single-op Func seam, which is the shape the
		// codebase already sanctions for exactly this case.
		private readonly Func<INetworkClient, Task> _sendDismount;

		public WorldOutbound(WorldBroadcaster broadcaster, NpcBroadcaster npcs, LootBroadcaster loot, WorldObjectService worldObjects, BuffService buffs, CombatService combat, TradeskillService tradeskill, Func<INetworkClient, Task> sendDismount)
		{
			_broadcaster = broadcaster;
			_npcs = npcs;
			_loot = loot;
			_worldObjects = worldObjects;
			_buffs = buffs;
			_combat = combat;
			_tradeskill = tradeskill;
			_sendDismount = sendDismount;
		}

		public Task ApplyNpcDotTick(INetworkClient client, uint npcHandle, uint casterHandle, int delta, uint spellId)
		{
			return _combat.ApplyNpcDotTick(client, npcHandle, casterHandle, delta, spellId);
		}

		// Same op, third port: the world tick applies the death penalty at the moment of death (#504).
		public Task ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			// The IWorldOutbound form has always taken the ISpellEffectOutbound default (walkChain: true),
			// which passes a null chain so BuffService starts a fresh walk.
			var self = client.Session.Character.Id;
			return _buffs.ApplySpell(client, self, self, wireSpellId);
		}

		public Task<IReadOnlyList<uint>> ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets)
		{
			return _combat.ApplySpellArea(client, sourceHandle, effectSpellId, radius, maxTargets);
		}

		public Task BroadcastCorpseFade(WorldEntity npc)
		{
			return _loot.BroadcastCorpseFade(npc);
		}

		public Task BroadcastEntityDespawn(WorldEntity entity)
		{
			return _broadcaster.BroadcastEntityDespawn(entity);
		}

		public Task BroadcastEntityFade(WorldEntity entity)
		{
			return _broadcaster.BroadcastEntityFade(entity);
		}

		public Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			return _npcs.BroadcastNpcAggro(npc, target);
		}

		public Task BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			return _npcs.BroadcastNpcAmbientAnimation(npc);
		}

		public Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			return _npcs.BroadcastNpcCombatMode(npc, inCombat);
		}

		public Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			return _npcs.BroadcastNpcFollow(npc, leader);
		}

		public Task BroadcastNpcMove(WorldEntity npc)
		{
			return _npcs.BroadcastNpcMove(npc);
		}

		public Task BroadcastEntitySnap(WorldEntity entity)
		{
			return _npcs.BroadcastEntitySnap(entity);
		}

		public Task BroadcastNpcPatrol(WorldEntity npc)
		{
			return _npcs.BroadcastNpcPatrol(npc);
		}

		// --- IWorldOutbound: the wire side of the simulation tick (broadcaster builds packets; listener owns vitals
		// send + the world-entry replay) -----------------------------------------------------------------------------
		public Task BroadcastNpcRespawn(WorldEntity npc)
		{
			return _npcs.BroadcastNpcRespawn(npc);
		}

		public Task BroadcastNpcStop(WorldEntity npc)
		{
			return _npcs.BroadcastNpcStop(npc);
		}

		public Task BroadcastShieldRose(WorldEntity entity)
		{
			return _broadcaster.BroadcastShieldRose(entity);
		}

		public Task CompleteDueTradeskills()
		{
			return _tradeskill.CompleteDue(DateTimeOffset.UtcNow);
		}

		public Task DropCorpseLoot(WorldEntity npc)
		{
			return _loot.DropCorpseLoot(npc);
		}

		public Task MaintainAmbientPools()
		{
			return _npcs.MaintainAmbientPools();
		}

		public Task PlayDynelVisualStep(DueDynelVisualStep step)
		{
			return _worldObjects.PlayDynelVisualStep(step);
		}

		public Task RestoreNodeWaypointType(Zone zone, LiveWorldObject node)
		{
			return _worldObjects.RestoreNodeWaypointType(zone, node);
		}

		public Task PushVitals(INetworkClient client, EntityVitals vitals)
		{
			// #586: every caller of this - the regen tick, fall damage, lava, drowning - moves a CURRENT and nothing
			// else. #902: and only the ones that MOVED go on the wire, which is both what #586's title said and the
			// own-handle shape retail actually uses (single-stat blocks x1,503 against x12 for the three-current form,
			// which is what it says about OTHER entities). An empty block means nothing moved and there is nothing to
			// announce - sending one would restate a health that did not change, which is what the regen tick did on
			// every second of every fight because stamina kept it alive.
			var moved = vitals.ToChangedCurrentsBlock();

			return moved.Records.Count == 0
				? Task.CompletedTask
				: GameServerListener.SendStatUpdate(client, moved, client.Session.Character.Id);
		}

		public Task ResolveInterest(INetworkClient client)
		{
			return _broadcaster.ResolveInterest(client);
		}

		public Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			return _broadcaster.RestoreEncounterLure(boss);
		}

		// #348: the sub-area crossing banner the world tick fires - forwards to the Outbound partial's sender.
		public Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return GameServerListener.SendAreaNameBanner(client, areaNameId);
		}

		// The buff-removal end-twin (#274/#253): the same ApplySpell carrier as the apply, rank byte -> 1, empty
		// payload. Routes the private SendBuffRemoval used by .cancelbuff so the world-tick expiry and the GM cancel
		// share one builder.
		public Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			return _buffs.SendBuffRemoval(client, applyTo, applyFrom, appliedSpellId);
		}

		public Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return CombatService.SendDamageNumber(client, targetHandle, attackerHandle, damage);
		}

		public Task SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(BehaviorControlCenterCodec.Build(DeathSequence.Bcc(dyingHandle)))));
		}

		public Task SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(MoveTaskCodec.Build(DeathSequence.Pose(dyingHandle, deathPosition)))));
		}

		public Task SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(SimpleCharKilledByUpdateCodec.Build(dyingHandle, killerEndpoint))));
		}

		public Task SendDismount(INetworkClient client)
		{
			return _sendDismount(client);
		}

		public Task SendKillChain(INetworkClient client, uint handle, uint count)
		{
			return WorldIir.Send(client, ChainKillCodec.Build(handle, count));
		}

		public Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId, bool ranged)
		{
			return CombatService.SendNpcAttackAnimation(client, npcHandle, damage, swingCounter, direction, comboId, ranged);
		}

		public Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return CombatService.SendNpcSpellCast(client, npcHandle, spell);
		}

		public Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return CombatService.SendRegen(client, handle, healthDelta, manaDelta, staminaDelta);
		}

		public Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return GameServerListener.SendRespawnPointDiscoveredBanner(client, pointId, nameKey);
		}

		// The player's resurrection spots. Lives here rather than in the Combat partial because the list is not a
		// combat concern: the world tick sends it to a LIVE player to announce a newly-discovered point (#372), and
		// the death path is only its other caller.
		public Task SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			return GameServerListener.SendValidResurrectionSpots(client, referencePosition, spots);
		}

		public Task SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			// Same GameAgent 0x59 sender / receiver framing as the death sequence (retail beach #1382 uses it).
			return client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(BehaviorControlCenterCodec.Build(DeathSequence.Revive(handle, respawnPointId)))));
		}

		public Task SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, Quaternion rotation)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, position, rotation))));
		}

		public async Task SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// #792: the zero-length 0x1B goes first - retail precedes EVERY IntendedTeleport with it, 102 of 102.
			await GameServerListener.SendTeleportPending(client);
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			await client.SendAsync(new Rpc(GameServerListener.PlayerDeathSender, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TeleportCodec.Build(handle, mapId, handle, playfieldToken))));
		}

		// #776: the apply carrier for a buff landing on someone else. Goes through SendMagicBuffIir like every other
		// ApplySpell we send - so the viewers around the target get it too - and because applyTo != applyFrom that
		// path registers nothing, which is right: the world tick owns a mob's buff registry.
		public Task SendSpellApplication(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId, uint durationCentiseconds)
		{
			return _buffs.SendMagicBuffIir(client, applyTo, applyFrom, wireSpellId, MagicBuffDatas.ApplyNow(wireSpellId >> 8, durationCentiseconds, BuffService.CasterLevel(client), _buffs.CasterIsPlayer(client, applyFrom)));
		}

		public Task SendStatDamage(INetworkClient client, uint targetHandle, uint displayHealth, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return CombatService.SendStatDamage(client, targetHandle, displayHealth, delta, type, casterHandle, spellBaseId);
		}

		public Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			return _broadcaster.SwapEncounterLureForBoss(lure);
		}

		// #367: the tick's seam onto the buff stat engine (expiry / death clear), same body as the listener's own calls.
		public Task SyncBuffStats(INetworkClient client)
		{
			return _buffs.SyncBuffStats(client);
		}
	}
}
