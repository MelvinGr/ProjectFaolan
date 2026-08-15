using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

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

		// #523: open a conversation the player has just walked up to an NPC for - the APPROACH trigger. Saddur's beach
		// confrontation is the worked example: "{hidegoodbye}Who stands before Saddur?" fires on approach, with no
		// click and no object.
		//
		// The radius is MEASURED, not chosen: across three independent captures the window opens at 12.9, 13.8 and 10.9
		// units from Saddur's spawn, so 15 covers all three with a little slack and stays far short of the next NPC
		// (Casilda at 153+). It is deliberately small - a generous radius would open the conversation across a room.
		//
		// ⚠️ This is proximity to the NPC, NOT a bounded-area volume. Only volume-entry is in RDB 1000053 and the two
		// are different mechanisms (#513), so nothing here needs region geometry.
		//
		// Fires ONCE per NPC per session (`ApproachedConversationNpcs`): Saddur's nodes carry no quest gate, so without
		// a latch the window would re-open on every step taken near him. The TEMPLATES that have one at all are cached
		// on the listener (#646 - a display name would open Saddur's confrontation on any NPC that happened to share
		// his name), so a player walking past ordinary NPCs pays one hash lookup and no DB hit.
		private async Task CheckPlayerApproachConversations(INetworkClient client, WorldEntity player, Vector3 position)
		{
			var templates = _approachConversationNpcs ??= [.. await Database.GetApproachConversationNpcs()];
			if (templates.Count == 0 || client.Session.ConversationNodeId != null)
				return; // nothing to open, or a conversation is already up

			var zone = _simulation.World.ZoneForEntity(player);
			foreach (var npc in zone.EntitiesInRange(position, ApproachConversationRadius, player.Handle))
			{
				if (npc.IsPlayer || !templates.Contains(npc.NpcTemplateId))
					continue;
				if (!client.Session.ApproachedConversationNpcs.Add(npc.Handle))
					continue; // already opened for this NPC this session

				if (await _conversation.TryOpenApproachConversation(client, npc.NpcTemplateId, npc.Handle))
				{
					Logger.LogInformation("#523: player {Char} approached {Npc} - opening its server-initiated conversation.", client.Session.Character.Name, npc.Name);
					return; // one conversation at a time
				}
			}
		}

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
			var reached = pending.Where(t => t.MapId == player.MapId && ReachedTarget(t, position)).ToList();
			foreach (var target in reached)
			{
				player.PendingReachGoals?.Remove(target);
				Logger.LogInformation("#262: player {Char} reached quest {Quest} goal {Goal} area ({X},{Z}).", client.Session.Character.Name, target.QuestId, target.GoalId, target.X, target.Z);
				await _questProgression.SolveGoal(client, target.GoalId);
			}
		}

		// Inside the goal's CLIENT trigger volume where it has one (#521) - the full prism test, so a goal region
		// stacked over another does not fire from the wrong floor - else within the marker radius (the #262
		// fallback, a horizontal circle around the goal's map pin).
		private static bool ReachedTarget(ReachGoalTarget t, Vector3 position)
		{
			return t.Region != null
				? EnvironmentService.Contains(t.Region, position)
				: (position.X - t.X) * (position.X - t.X) + (position.Z - t.Z) * (position.Z - t.Z) <= t.RadiusSq;
		}
	}
}
