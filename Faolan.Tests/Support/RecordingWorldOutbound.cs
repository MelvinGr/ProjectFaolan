using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;

namespace Faolan.Tests.Support
{
	// A no-op IWorldOutbound that RECORDS the handful of ops the world tick's buff jobs use, so a tick pass can be
	// driven in a test without a listener, a socket or a database behind it. Everything else answers "nothing
	// happened" - a tick that reaches one of those is not what these gates are about.
	public sealed class RecordingWorldOutbound : IWorldOutbound
	{
		public List<(uint ApplyTo, uint ApplyFrom, uint SpellId)> Removals { get; } = [];
		public List<(uint ApplyTo, uint ApplyFrom, uint WireSpellId, uint DurationCentiseconds)> Applications { get; } = [];
		public List<(uint NpcHandle, uint CasterHandle, int Delta, uint SpellId)> DotTicks { get; } = [];
		public List<(uint SourceHandle, uint EffectSpellId, float Radius, uint MaxTargets)> Areas { get; } = [];
		public List<uint> InterestResolves { get; } = [];
		public List<(uint Handle, CorpseStage Stage)> CorpseEvents { get; } = [];

		// What ApplySpellArea answers with, and what a spell's aura areas / #784 chain edges are - set by the test that
		// needs them.
		public IReadOnlyList<uint> AreaVictims { get; set; } = [];

		public Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			Removals.Add((applyTo, applyFrom, appliedSpellId));
			return Task.CompletedTask;
		}

		public Task SendSpellApplication(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId, uint durationCentiseconds)
		{
			Applications.Add((applyTo, applyFrom, wireSpellId, durationCentiseconds));
			return Task.CompletedTask;
		}

		public Task ApplyNpcDotTick(INetworkClient client, uint npcHandle, uint casterHandle, int delta, uint spellId)
		{
			DotTicks.Add((npcHandle, casterHandle, delta, spellId));
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<uint>> ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets)
		{
			Areas.Add((sourceHandle, effectSpellId, radius, maxTargets));
			return Task.FromResult(AreaVictims);
		}

		public Task ResolveInterest(INetworkClient client)
		{
			InterestResolves.Add(client.Session.Character?.Id ?? 0);
			return Task.CompletedTask;
		}

		public Task BroadcastEntityFade(WorldEntity entity)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastShieldRose(WorldEntity entity)
		{
			return Task.CompletedTask;
		}

		public Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			return Task.FromResult<WorldEntity?>(null);
		}

		public Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			return Task.FromResult<WorldEntity?>(null);
		}

		public Task BroadcastNpcMove(WorldEntity npc)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcPatrol(WorldEntity npc)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcRespawn(WorldEntity npc)
		{
			return Task.CompletedTask;
		}

		public Task BroadcastNpcStop(WorldEntity npc)
		{
			return Task.CompletedTask;
		}

		// The corpse sequence, recorded in order so a lifecycle gate can assert that the bag lands on the kill and the
		// body is torn down half a minute later, rather than the other way round.
		public Task DropCorpseLoot(WorldEntity npc)
		{
			CorpseEvents.Add((npc.Handle, CorpseStage.Drop));
			return Task.CompletedTask;
		}

		public Task BroadcastCorpseFade(WorldEntity npc)
		{
			CorpseEvents.Add((npc.Handle, CorpseStage.Fade));
			return Task.CompletedTask;
		}

		public Task BroadcastEntityDespawn(WorldEntity entity)
		{
			CorpseEvents.Add((entity.Handle, CorpseStage.Quit));
			return Task.CompletedTask;
		}

		public Task MaintainAmbientPools()
		{
			return Task.CompletedTask;
		}

		public Task CompleteDueTradeskills()
		{
			return Task.CompletedTask;
		}

		public Task PlayDynelVisualStep(DueDynelVisualStep step)
		{
			return Task.CompletedTask;
		}

		public Task PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return Task.CompletedTask;
		}

		public Task SyncBuffStats(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		public Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return Task.CompletedTask;
		}

		public Task ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			return Task.CompletedTask;
		}

		public Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return Task.CompletedTask;
		}

		public Task SendDismount(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		public Task SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return Task.CompletedTask;
		}

		public Task SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return Task.CompletedTask;
		}

		public Task SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return Task.CompletedTask;
		}

		public Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId, bool ranged)
		{
			return Task.CompletedTask;
		}

		public Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return Task.CompletedTask;
		}

		public Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return Task.CompletedTask;
		}

		public Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return Task.CompletedTask;
		}

		public Task SendKillChain(INetworkClient client, uint handle, uint count)
		{
			return Task.CompletedTask;
		}

		public Task SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			return Task.CompletedTask;
		}

		public Task SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			return Task.CompletedTask;
		}

		public Task SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, Quaternion rotation)
		{
			return Task.CompletedTask;
		}

		public Task SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			return Task.CompletedTask;
		}

		public Task SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return Task.CompletedTask;
		}
	}
}
