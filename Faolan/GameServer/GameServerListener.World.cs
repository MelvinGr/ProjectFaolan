using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// #262: solve any ReachArea quest goal the player has just walked into range of. Called from the move handler
		// on every accepted player move; the goal targets are cached on the player WorldEntity (lazily loaded once, so
		// this stays a pure in-memory distance test with no DB hit until a goal actually fires). A solved target is
		// removed so it fires exactly once; SolveGoal handles the tick / completion / chain supersession.
		private async Task CheckPlayerReachGoals(INetworkClient client, WorldEntity player, Vector3 position)
		{
			var pending = player.PendingReachGoals ??= await _questProgression.LoadReachGoals(client.Session.Character.Id);
			if (pending.Count == 0)
				return;

			// Snapshot the in-range targets BEFORE solving: SolveGoal can complete a stage whose supersession calls
			// InvalidateReachGoals (PendingReachGoals -> null, reloaded next move), so we must not index the live list
			// across a solve. Removes are null-safe; a target solved after an invalidation is simply excluded from the
			// reload (LoadReachGoals skips completed goals), so no double-fire.
			var reached = pending.Where(t => t.MapId == player.MapId
			                                 && (position.X - t.X) * (position.X - t.X) + (position.Z - t.Z) * (position.Z - t.Z) <= t.RadiusSq).ToList();
			foreach (var target in reached)
			{
				player.PendingReachGoals?.Remove(target);
				Logger.LogInformation("#262: player {Char} reached quest {Quest} goal {Goal} area ({X},{Z}).", client.Session.Character.Name, target.QuestId, target.GoalId, target.X, target.Z);
				await _questProgression.SolveGoal(client, target.GoalId);
			}
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
			return SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Character.Id);
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
