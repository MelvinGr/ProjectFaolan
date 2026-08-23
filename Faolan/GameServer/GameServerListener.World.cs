using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
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

		Task IWorldOutbound.BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcAmbientAnimation(npc);
		}

		Task IWorldOutbound.BroadcastShieldRose(WorldEntity entity)
		{
			return _broadcaster.BroadcastShieldRose(entity);
		}

		Task<WorldEntity?> IWorldOutbound.SwapEncounterLureForBoss(WorldEntity lure)
		{
			return _broadcaster.SwapEncounterLureForBoss(lure);
		}

		Task<WorldEntity?> IWorldOutbound.RestoreEncounterLure(WorldEntity boss)
		{
			return _broadcaster.RestoreEncounterLure(boss);
		}

		Task IWorldOutbound.BroadcastNpcMove(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcMove(npc);
		}

		Task IWorldOutbound.BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			return _broadcaster.BroadcastNpcFollow(npc, leader);
		}

		Task IWorldOutbound.BroadcastNpcPatrol(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcPatrol(npc);
		}

		Task IWorldOutbound.ResolveInterest(INetworkClient client)
		{
			return _broadcaster.ResolveInterest(client);
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

		Task IWorldOutbound.MaintainAmbientPools()
		{
			return _broadcaster.MaintainAmbientPools();
		}

		Task IWorldOutbound.CompleteDueTradeskills()
		{
			return _tradeskill.CompleteDue(DateTimeOffset.UtcNow);
		}

		Task IWorldOutbound.PlayDynelVisualStep(DueDynelVisualStep step)
		{
			return _broadcaster.PlayDynelVisualStep(step);
		}

		// #367: the tick's seam onto the buff stat engine (expiry / death clear), same body as the listener's own calls.
		Task IWorldOutbound.SyncBuffStats(INetworkClient client)
		{
			return _buffs.SyncBuffStats(client);
		}

		Task IWorldOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			// #586: every caller of this - the regen tick, fall damage, lava, drowning - moves a CURRENT and nothing
			// else, which is the case retail answers with the three-record currents-only block.
			return SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}

		// #348: the sub-area crossing banner the world tick fires - forwards to the Outbound partial's sender.
		Task IWorldOutbound.SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return SendAreaNameBanner(client, areaNameId);
		}

		Task IWorldOutbound.SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return SendRespawnPointDiscoveredBanner(client, pointId, nameKey);
		}

		Task IWorldOutbound.SendKillChain(INetworkClient client, uint handle, uint count)
		{
			return WorldIir.Send(client, ChainKillCodec.Build(handle, count));
		}

		// The player's resurrection spots. Lives here rather than in the Combat partial because the list is not a
		// combat concern: the world tick sends it to a LIVE player to announce a newly-discovered point (#372), and
		// the death path is only its other caller.
		Task IWorldOutbound.SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			return SendValidResurrectionSpots(client, referencePosition, spots);
		}

	}
}
