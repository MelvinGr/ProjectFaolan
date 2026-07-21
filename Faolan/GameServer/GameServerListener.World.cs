using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
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

		Task IWorldOutbound.BroadcastNpcHeartbeat(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcHeartbeat(npc);
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

		Task IWorldOutbound.MaintainAmbientPools()
		{
			return _broadcaster.MaintainAmbientPools();
		}

		Task IWorldOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}

		Task IWorldOutbound.ReenterWorld(INetworkClient client)
		{
			return SendWorldEntry(client);
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

		// The player's resurrection spots. Lives here rather than in the Combat partial because the list is not a
		// combat concern: the world tick sends it to a LIVE player to announce a newly-discovered point (#372), and
		// the death path is only its other caller.
		Task IWorldOutbound.SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			return SendValidResurrectionSpots(client, referencePosition, spots);
		}
	}
}
