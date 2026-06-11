using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// Combat domain logic: resolves melee swings and spell casts into damage, drives the NPC kill lifecycle
	// (despawn + scheduled respawn via the world model), awards XP / levels, tracks kill-objective quest progress, and
	// builds the combat wire packets (the BehaviorControlCenter ack, DamageResult, Energy, and 0x96B8DC59 vitals).
	// Extracted out of the GameServer listener as a composed collaborator (like WorldBroadcaster): it reads/writes the
	// world model via the WorldSimulation, broadcasts world effects via the WorldBroadcaster, and frames its own
	// packets on the world conversation (WorldFraming). Vitals resolve from the world model (the authoritative owner);
	// a per-session CombatState backs NPCs not in the model (e.g. the built-NPC test). See protocol_stats_and_combat.
	public sealed class CombatService
	{
		// Fallback vitals store for NPCs NOT in the world model (e.g. the built-NPC test) - model NPCs and players
		// keep their vitals on their WorldEntity (the single source of truth). In-memory, never persisted.
		private readonly CombatState _combat = new CombatState();

		private readonly WorldSimulation _sim;
		private readonly WorldBroadcaster _broadcaster;
		private readonly IDatabaseRepository _database;
		private readonly IAgentServerDispatcher _agents;
		private readonly ILogger _logger;

		public CombatService(WorldSimulation sim, WorldBroadcaster broadcaster, IDatabaseRepository database, IAgentServerDispatcher agents, ILogger logger)
		{
			_sim = sim;
			_broadcaster = broadcaster;
			_database = database;
			_agents = agents;
			_logger = logger;
		}

		// The player's own live vitals - the WORLD MODEL is the single source of truth: they live on the player's
		// WorldEntity (like NPCs'), seeded from CharacterMaxStats. World-authoritative, never persisted (only
		// CharacterMaxStats is). Falls back to a transient seed only if the player isn't in the world yet (no combat/
		// regen happens then, so this is just defensive).
		public EntityVitals GetOrAddPlayerVitals(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return EntityVitals.FromCharacter(client.Session.Character);

			return entity.Vitals ??= EntityVitals.FromCharacter(client.Session.Character);
		}

		// A target NPC's live vitals, seeded from its real spawn health (recorded when the world burst spawned it)
		// so its health bar moves relative to the HP the client rendered; falls back to the placeholder for NPCs
		// not spawned via the burst (e.g. the built-NPC test).
		private EntityVitals GetOrAddNpcVitals(INetworkClient client, uint targetHandle)
		{
			return _combat.GetOrAdd(targetHandle, () =>
			{
				var maxHealth = client.Session.SpawnedNpcMaxHealthByHandle.GetValueOrDefault(targetHandle, CombatResolver.DefaultNpcMaxHealth);
				return EntityVitals.FromMax(maxHealth, 0, maxHealth);
			});
		}

		// Resolves an offensive cast: applies damage to the target's live vitals and pushes a 0x96B8DC59 update so
		// its health bar moves. The ApplySpell cast itself is broadcast by the caller.
		public async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId)
		{
			var casterLevel = client.Session.Character?.Level ?? 1;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel);
			await ApplyNpcDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}");
		}

		// Applies `damage` to an NPC and pushes its 0x96B8DC59 health update; on death, runs the kill lifecycle
		// (despawn + scheduled respawn). Vitals resolve from the WORLD MODEL when the target is a model NPC (the
		// authoritative owner), else fall back to the per-session store (e.g. the built-NPC test). Shared by the melee
		// and spell paths.
		private async Task ApplyNpcDamage(INetworkClient client, uint targetHandle, int damage, string cause)
		{
			var target = ResolveNpcVitals(client, targetHandle, out var npcEntity);
			if (target.IsDead)
				return; // already dead this life; ignore overkill until it respawns

			target.ApplyHealthDelta(-damage);
			await SendStatUpdate(client, target.ToVitalsBlock(), targetHandle);

			if (target.IsDead)
				await OnNpcKilled(client, targetHandle, npcEntity, cause);
		}

		// The NPC's authoritative live vitals: the world-model entity's if it is a tracked NPC (also returned, so the
		// caller can run its death lifecycle), otherwise the per-session vitals (npcEntity null).
		private EntityVitals ResolveNpcVitals(INetworkClient client, uint targetHandle, out WorldEntity npcEntity)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneFor(player.MapId).TryGet(targetHandle, out npcEntity) && !npcEntity.IsPlayer && npcEntity.Vitals != null)
				return npcEntity.Vitals;

			npcEntity = null;
			return GetOrAddNpcVitals(client, targetHandle);
		}

		// An NPC was killed by `killer`: a model NPC is marked dead (starting its respawn timer), despawned for nearby
		// players (the tick respawns it), and awards the killer XP. A non-model NPC (no world entity) is just dropped
		// from the session store.
		private async Task OnNpcKilled(INetworkClient killer, uint targetHandle, WorldEntity npcEntity, string cause)
		{
			if (npcEntity != null)
			{
				npcEntity.MarkDead();
				await _broadcaster.BroadcastEntityDespawn(npcEntity);
				_logger.LogInformation("Combat: NPC {Name} 0x{Target:X8} killed ({Cause}); despawned, respawns in {Delay}s.", npcEntity.Name, targetHandle, cause, npcEntity.RespawnDelaySeconds);
				await AwardKillXp(killer, npcEntity.Level);
				await _broadcaster.TrySpawnLoot(npcEntity);
				await AdvanceKillQuests(killer, npcEntity.Name);
				return;
			}

			_logger.LogInformation("Combat: entity 0x{Target:X8} killed ({Cause}); not in world model, no respawn.", targetHandle, cause);
			_combat.Remove(targetHandle);
		}

		// Advances the killer's quest kill-objectives for an NPC of `npcName`: any active quest whose GoalTargetName
		// matches gets GoalProgress++ (persisted) and a QuestGoal push; reaching GoalRequired completes it (state +
		// system message). The objective DEFINITIONS (target/count) are set when the quest is added (the pinned RDB
		// Lua VM would set them); this is the auto-tracking engine on top.
		private async Task AdvanceKillQuests(INetworkClient killer, string npcName)
		{
			var character = killer?.Session?.Character;
			if (character == null || string.IsNullOrEmpty(npcName))
				return;

			foreach (var quest in await _database.GetCharacterQuests(character.Id))
			{
				if (!QuestProgress.AdvancesOnKill(quest, npcName))
					continue;

				var progress = quest.GoalProgress + 1;
				var completed = progress >= quest.GoalRequired;
				await _database.SetCharacterQuest(character.Id, quest.QuestId,
					completed ? QuestState.Completed : QuestState.Active,
					progress, quest.GoalRequired, quest.GoalTargetName);
				await _broadcaster.SendQuestGoal(killer, quest.QuestId, 0, progress);

				if (completed)
					await _agents.SendSystemMessage(character.Id, $"Quest {quest.QuestId} complete!");
				else
					await _agents.SendSystemMessage(character.Id, $"{npcName} slain ({progress}/{quest.GoalRequired}).");
			}
		}

		// Awards the killer XP for a kill, advancing levels when the XP bar fills (LevelingResolver), and pushes a
		// 0x96B8DC59 stat update so the XP bar (and, on a ding, level + new vitals) move. Experience is within-level
		// progress; XP/level accrue on the persisted Character (saved by the usual disconnect/autosave/.save points).
		private async Task AwardKillXp(INetworkClient killer, uint npcLevel)
		{
			var character = killer?.Session?.Character;
			if (character == null)
				return;

			var xp = CombatResolver.ResolveKillXp(npcLevel);
			character.Experience += xp;

			var (levelsGained, remaining) = LevelingResolver.Apply(character.Level, character.Experience);
			character.Experience = remaining;

			if (levelsGained > 0)
			{
				character.Level = (ushort)Math.Min(character.Level + levelsGained, LevelingResolver.MaxLevel);
				character.RegenerateMaxStats();
				await PushLevelUp(killer, character);
				_logger.LogInformation("Combat: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			var block = new CharacterStatBlock()
				.Set(LegacyCharacterStatId.Exp, character.Experience)
				.Set(LegacyCharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer, block, killer.Session.Account.ClientInstance);
			_logger.LogInformation("Combat: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
		}

		// On level-up: raise the player's live vitals to the new max and refill them (a ding heals), then push the new
		// Level + Exp/MaxExp + full vitals in one 0x96B8DC59 update.
		private async Task PushLevelUp(INetworkClient killer, Character character)
		{
			var vitals = GetOrAddPlayerVitals(killer);
			var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			vitals.MaxHealth = max.Health;
			vitals.MaxStamina = max.Stamina;
			vitals.MaxMana = max.Mana;
			vitals.Health = max.Health;
			vitals.Stamina = max.Stamina;
			vitals.Mana = max.Mana;

			var block = vitals.ToVitalsBlock()
				.Set(LegacyCharacterStatId.Level, character.Level)
				.Set(LegacyCharacterStatId.Exp, character.Experience)
				.Set(LegacyCharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer, block, killer.Session.Account.ClientInstance);
		}

		// The melee swing / auto-attack (Interact ActionType 0x0F). Retail's response sequence, reproduced from the
		// strickenbla combat capture, is: a BehaviorControlCenterIIR (0x642CD3D6) acking the behavior (this is what
		// the client blocks on - without it the swing stalls), then a DamageResultIIR (0xFEC4158A) hit, then an
		// EnergyIIR (0x40749B8A) stamina update. The ack header echoes the request (instance, behaviorId, args) with
		// the sequence incremented; its 81-byte result tail is the capture's layout with the live actor=player and
		// the computed damage in the damage slot (the v1/v3/v5 metadata constants are templated - their semantics
		// aren't decoded yet, only that v4 == the swing damage).
		public async Task SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle)
		{
			// rawPayload = [bodyLen u32][subop u32][c350 u32][instance u32][seq u16][behaviorId u32][argsLen u32][args][reqTail]
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Account.ClientInstance;
			var seq = (ushort)((rawPayload[16] << 8) | rawPayload[17]);
			// argsLen is the u32 at offset 22 (the request args length). The old code read it as a u16 at 22 (= the
			// high half = 0), so the ack echoed EMPTY args and the client's behaviour FSM desynced after one swing.
			var argsLen = (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4));
			if (26 + argsLen > rawPayload.Length)
				return;

			var argBytes = rawPayload[26..(26 + argsLen)];
			var reqTail = rawPayload[(26 + argsLen)..];
			var abilityId = reqTail.Length >= 4
				? (uint)((reqTail[^4] << 24) | (reqTail[^3] << 16) | (reqTail[^2] << 8) | reqTail[^1])
				: 0u;

			// Retail's ack echoes the request args with a field3 "phase" inserted - a monotonic per-swing-ack counter
			// the client uses to sequence behaviour responses. Stamp our running counter and splice it in.
			var echoedArgs = InsertBehaviorPhaseField(argBytes, (uint)(++client.Session.NormalAttackAckPhase));

			// Only deal damage to a real enemy: an attack with no target selected (or one that resolves to the
			// player's own handle) must NOT hit the player - that was the "attack hits myself" bug.
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			var level = client.Session.Character?.Level ?? 1;
			var damage = hasEnemyTarget
				? (uint)CombatResolver.ResolveSpellDamage(abilityId, level)
				: 0u;
			var next = (ushort)(seq + 1);

			// 1) BehaviorControlCenterIIR ack - releases the swing.
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(0x642CD3D6u)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteUInt16(next)
					.WriteUInt32(0x0000000Fu)
					.WriteUInt32((uint)echoedArgs.Length)
					.WriteArray(echoedArgs)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(1) // result block: count
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0) // entry (6 u32)
					.WriteUInt32(1)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteUInt32(0) // result: count, c350, actor=player, 0
					.WriteByte(0x06)
					.WriteUInt32(0x000000CBu) // result constant (was 0xCD; the capture's BccAck result block uses 0xCB)
					.WriteUInt32(0)
					.WriteUInt32(1)
					.WriteUInt32(damage)
					.WriteUInt32(2)
					.WriteUInt32(0)
					.WriteUInt32(0))
				.SendAsync(client);

			if (hasEnemyTarget)
			{
				// 2) DamageResultIIR - the hit / floating number.
				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
					.WriteArrayUInt32Length(s => s
						.WriteUInt32(0xFEC4158Au)
						.WriteUInt32(0x0000C350u)
						.WriteUInt32(instance)
						.WriteByte(0x00)
						.WriteUInt32(0x0000C350u)
						.WriteUInt32(targetHandle)
						.WriteUInt32(damage)
						.WriteUInt32(2)
						.WriteUInt32(0)
						.WriteUInt32(0))
					.SendAsync(client);

				// Apply to the target's live vitals (world-authoritative), push its health bar, and run the kill
				// lifecycle (despawn + scheduled respawn) on death.
				await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");
			}

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = GetOrAddPlayerVitals(client);
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(0x40749B8Au)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteByte(0x01)
					.WriteUInt32(player.Stamina))
				.SendAsync(client);
		}

		// Spends the caster's mana for a cast and pushes the caster's own 0x96B8DC59 update so the mana bar drops -
		// the spell/mana analogue of the melee swing's stamina cost. (Mana isn't carried by EnergyIIR, which is
		// stamina-only in the captures; it rides the vitals stat block.) Per-spell costs aren't decoded yet, so the
		// cost is provisional. No-op if the player has no mana pool.
		public async Task SpendCasterMana(INetworkClient client, uint spellId)
		{
			var player = GetOrAddPlayerVitals(client);
			if (player.MaxMana == 0)
				return;

			var cost = CombatResolver.ResolveSpellManaCost(spellId, client.Session.Character?.Level ?? 1);
			player.ApplyManaDelta(-(int)cost);
			await SendStatUpdate(client, player.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}

		// Pushes a 0x96B8DC59 stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}

		// Splices field3 (the behaviour "phase", tag 0x18 varint) into the request args right after field2, matching
		// retail's BccAck echo. Protobuf is tag-addressed so position isn't strictly required, but this mirrors the
		// capture's byte layout. If field2 is absent/malformed the phase is appended at the end.
		internal static byte[] InsertBehaviorPhaseField(byte[] args, uint phase)
		{
			var phaseField = EncodeBehaviorPhase(phase);
			var insertAt = Math.Min(OffsetAfterField(args, 2), args.Length);
			var result = new byte[args.Length + phaseField.Length];
			Array.Copy(args, 0, result, 0, insertAt);
			phaseField.CopyTo(result, insertAt);
			Array.Copy(args, insertAt, result, insertAt + phaseField.Length, args.Length - insertAt);
			return result;
		}

		// Byte offset just after `fieldNumber`'s value in a protobuf message (the message end if absent/malformed).
		private static int OffsetAfterField(byte[] msg, int fieldNumber)
		{
			var i = 0;
			while (i < msg.Length)
			{
				var tag = msg[i++];
				var fn = tag >> 3;
				var wt = tag & 7;
				if (wt == 0)
				{
					while (i < msg.Length && (msg[i] & 0x80) != 0)
						i++;
					i++;
				}
				else if (wt == 2)
				{
					int len = 0, shift = 0;
					while (i < msg.Length)
					{
						var b = msg[i++];
						len |= (b & 0x7f) << shift;
						if ((b & 0x80) == 0)
							break;
						shift += 7;
					}

					i += len;
				}
				else
					return msg.Length; // unsupported wiretype -> append at end

				if (fn == fieldNumber)
					return i;
			}

			return msg.Length;
		}

		// field3 as protobuf: tag 0x18 (field 3, varint) + the base-128 varint of `value`.
		private static byte[] EncodeBehaviorPhase(uint value)
		{
			var bytes = new List<byte> { 0x18 };
			do
			{
				var b = (byte)(value & 0x7f);
				value >>= 7;
				if (value != 0)
					b |= 0x80;
				bytes.Add(b);
			}
			while (value != 0);

			return bytes.ToArray();
		}
	}
}
