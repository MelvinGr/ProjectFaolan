using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
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
		// The 8-byte filler retail writes where an item identity quad would otherwise go in a behavior ack
		// (B2 90 80 5C 29 E6 27 CA - the same null-identity padding as the 0xF result block and the 0x2052 grant).

		// Fallback vitals store for NPCs NOT in the world model (e.g. the built-NPC test) - model NPCs and players
		// keep their vitals on their WorldEntity (the single source of truth). In-memory, never persisted.
		private readonly CombatState _combat = new CombatState();

		// The announced 0x2052 ability grant lives on the listener (GrantAbilitiesBuilder); the XP-driven ding needs
		// it for level-gated unlocks (#209/#73 - Rest at L2), so it's injected as the single-op delegate seam the
		// composition pattern prescribes (CLAUDE.md). Null-safe for tests that construct the service without it.
		private readonly Func<INetworkClient, Ability, Task> _announceAddSpecialAbility;

		// The listener's live spell-learn op (DispatchLearnSpell 0x200A), for the level-up spell unlocks (#209). Same
		// narrow-seam pattern as _announceAddSpecialAbility. Null-safe for tests that construct the service without it.
		private readonly Func<INetworkClient, uint, Task> _learnSpell;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;

		private readonly WorldSimulation _sim;

		// Quest kill-goals route through the QuestProgressionService (#261), which owns the broadcaster + system-message
		// seams; combat packets frame themselves directly (SendWorldPacket), so this service no longer needs either.
		public CombatService(WorldSimulation sim, IDatabaseRepository database, QuestProgressionService questProgression, ILogger logger, Func<INetworkClient, Ability, Task> announceAddSpecialAbility = null,
			Func<INetworkClient, uint, Task> learnSpell = null)
		{
			_sim = sim;
			_database = database;
			_questProgression = questProgression;
			_logger = logger;
			_announceAddSpecialAbility = announceAddSpecialAbility;
			_learnSpell = learnSpell;
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

		// Applies `damage` to an NPC and pushes its 0x96B8DC59 health update; on death, runs the kill lifecycle
		// (despawn + scheduled respawn). Vitals resolve from the WORLD MODEL when the target is a model NPC (the
		// authoritative owner), else fall back to the per-session store (e.g. the built-NPC test). Shared by the melee
		// and spell paths.
		// .kill GM command: kill an NPC target outright by applying its full remaining health as damage, so it runs
		// the normal death lifecycle (vitals push, despawn/corpse-loot, XP, kill-quest advance). No-op if the target
		// isn't a live NPC in the killer's zone.
		public Task KillNpc(INetworkClient client, uint targetHandle)
		{
			var target = ResolveNpcVitals(client, targetHandle, out _);
			return ApplyNpcDamage(client, targetHandle, (int)target.Health, ".kill command");
		}

		// Resolves an offensive cast: applies damage to the target's live vitals and pushes a 0x96B8DC59 update so
		// its health bar moves. The ApplySpell cast itself is broadcast by the caller.
		public async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId)
		{
			var casterLevel = client.Session.Character?.Level ?? 1;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel);
			await ApplyNpcDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}");
		}

		// The generic "every behavior gets an echo-ack" reply (docs/combat.md rule 1) for an Interact request with no
		// special result block - the air-swing path uses it, and so does a plain item-use (RightClickItem, e.g. the
		// Totem of Origin #206). Phase = the request's Mode, or the legacy session counter when the caller didn't
		// parse the args. No-op for a malformed (too-short) payload.
		public static Task SendBehaviorEchoAck(INetworkClient client, byte[] rawPayload, uint mode)
		{
			if (rawPayload.Length < 26)
				return Task.CompletedTask;

			var instance = client.Session.Account.ClientInstance;
			var phase = mode != 0
				? mode
				: (uint)(++client.Session.NormalAttackAckPhase);
			return SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, phase));
		}

		// The floating combat-damage number over the player when an NPC hits them (DamageResultIIR 0xFEC4158A,
		// target=player + attacker=NPC). The player-attacks-NPC direction already fires this from the swing/cast
		// resolve; this is the missing NPC-attacks-player half so the player sees the hit number, not just the HP
		// bar move. Byte-exact builder (CombatEffectBuilder), golden-tested vs the captured NPC->player hit. #274.
		public static Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return SendWorldPacket(client, CombatEffectBuilder.BuildDamageResult(targetHandle, attackerHandle, damage, 2));
		}

		// Acks an equip/unequip move (Interact 0x09) and appends the 0x201C bookkeeping entry retail sends with it:
		// prefix 0x65 + state = the destination equip slot for a move INTO a slot, prefix 0x68 + state 0 for a move
		// OUT of one (combat capture frames 289-290/320-321/375-376).
		public static async Task SendEquipMoveAck(INetworkClient client, byte[] rawPayload, uint mode, Item movedItem)
		{
			if (rawPayload.Length < 26)
				return;

			await SendWorldPacket(client, BehaviorAckBody.BuildEquipMoveAckBody(rawPayload, client.Session.Account.ClientInstance, mode, movedItem));
		}

		// The melee swing / auto-attack (Interact ActionType 0x0F). Retail's response sequence, reproduced from the
		// strickenbla combat capture, is: a BehaviorControlCenterIIR (0x642CD3D6) acking the behavior (this is what
		// the client blocks on - without it the swing stalls), then a DamageResultIIR (0xFEC4158A) hit, then an
		// EnergyIIR (0x40749B8A) stamina update. The ack header echoes the request (instance, behaviorId, args) with
		// the sequence incremented; its 81-byte result tail is the capture's layout with the live actor=player and
		// the computed damage in the damage slot (the v1/v3/v5 metadata constants are templated - their semantics
		// aren't decoded yet, only that v4 == the swing damage).
		//
		// The result-block ack is the TARGETED-hit variant: its actor=player slot makes the client play a hit-react
		// (the blood/impact animation) on whoever the block names - the player. An air swing (no target selected)
		// must therefore get the PLAIN echo-ack with no result block, exactly like the unarmed air swing, or the
		// player visibly bleeds when swinging at nothing (#19). The "no damage" half was already guarded; this also
		// drops the self hit-react.
		public async Task SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle, uint mode = 0)
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

			var reqTail = rawPayload[(26 + argsLen)..];
			var abilityId = reqTail.Length >= 4
				? (uint)((reqTail[^4] << 24) | (reqTail[^3] << 16) | (reqTail[^2] << 8) | reqTail[^1])
				: 0u;

			// Retail's ack echoes the request args with a field3 "phase" inserted. The 2026-06-12 armed+unarmed
			// capture proves the phase value IS the request's own Mode (field 2) - the earlier "monotonic server
			// counter" read was an artifact of Mode itself incrementing per action. Echo the Mode when the caller
			// has it; fall back to the legacy session counter for callers that don't parse the args.
			var phase = mode != 0
				? mode
				: (uint)(++client.Session.NormalAttackAckPhase);

			// Only hit a real enemy: an attack with no target selected (or one that resolves to the player's own
			// handle) must NOT hit the player - that was the "attack hits myself" bug. An air swing gets the plain
			// echo-ack (no result block) so the client plays no hit-react on the player (#19), matching the unarmed
			// air-swing path - then we're done (retail's air swing is the bare ack).
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget)
			{
				await SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, phase));
				return;
			}

			var echoedArgs = BehaviorAckBody.InsertBehaviorPhaseField(rawPayload[26..(26 + argsLen)], phase);
			var level = client.Session.Character?.Level ?? 1;
			var damage = (uint)CombatResolver.ResolveSpellDamage(abilityId, level);
			var next = (ushort)(seq + 1);

			// 1) BehaviorControlCenterIIR ack - releases the swing.
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteUInt16(next)
					.WriteUInt32(0x0000000Fu)
					.WriteUInt32(echoedArgs.Length)
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
					.WriteUInt32(targetHandle)
					.WriteUInt32(0) // result: count, c350, VICTIM=target, 0 - the entity that plays the hit-react/blood.
									// Was `instance` (the player), which played the blood/impact on SELF when hitting an NPC (#228);
									// the proven NPC->player ack (BuildNpcAttackAnimationAckBody) names the victim handle in this slot.
					.WriteByte(0x06)
					.WriteUInt32(0x000000CBu) // result constant (was 0xCD; the capture's BccAck result block uses 0xCB)
					.WriteUInt32(0)
					.WriteUInt32(1)
					.WriteUInt32(damage)
					.WriteUInt32(2)
					.WriteUInt32(0)
					.WriteUInt32(0))
				.SendAsync(client);

			// 2) DamageResultIIR - the hit / floating number.
			await SendWorldPacket(client, CombatPacketCodec.BuildDamageResultBody(instance, targetHandle, damage));

			// Apply to the target's live vitals (world-authoritative), push its health bar, and run the kill
			// lifecycle (despawn + scheduled respawn) on death.
			await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, CombatPacketCodec.BuildEnergyBody(instance, player.Stamina));
		}

		// Sends the NPC counter-attack ANIMATION ack to the player it hit: a BehaviorControlCenterIIR with actor = the
		// NPC handle (the decoded "second BccAck", docs/combat.md), so the client plays the NPC's swing + the player's
		// hit-react. Visual only - the authoritative HP move rides the world-tick StatUpdate, so the damage here is just
		// the floating combat number. The animation/result params are capture-typical (not semantically decoded); the
		// swingCounter increments per NPC swing to mirror the per-actor counter the capture carries.
		public static Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter)
		{
			var playerHandle = client.Session.Account.ClientInstance;
			// args.f1 is the NormalAttack BEHAVIOR TypeId the client resolves to pick the swing animation - NOT an
			// ability id. The capture's beach mobs use ~0xBEEC (48876) across several NPCs (per-NPC jitter of a few);
			// 0xBEEC is the representative value. (The earlier 769094 was the Normal Attack ABILITY id - wrong field,
			// which played the player's hit-react + audio but left the NPC body un-animated.)
			var body = CombatPacketCodec.BuildNpcAttackAnimationAckBody(npcHandle, playerHandle, damage, 0xBEEC, swingCounter);
			return SendWorldPacket(client, body);
		}

		// Sends a caster NPC's spell cast to the player it targets (#232): the ApplySpell (0x6B87E316) framed from the
		// NPC handle (applyFrom = npc, applyTo = player), built from the NPC's NpcSpell row by NpcSpellCastBuilder -
		// the construct-from-data counterpart of the melee SendNpcAttackAnimation. The HP move rides the caller's
		// PushVitals; this is the cast packet/effect.
		public static Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			var playerHandle = client.Session.Account.ClientInstance;
			var body = NpcSpellCastBuilder.Build(playerHandle, npcHandle, spell.SpellId, spell.Payload);
			return SendWorldPacket(client, body);
		}

		// A DoT tick's StatDamageIIR (the health effect of an active bleed/HoT). idx 1 is the typical effect slot in
		// the capture; statValue = the resulting HealthCurrent so the client's bar/number matches the vitals move. #274
		public static Task SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return SendWorldPacket(client, CombatEffectBuilder.BuildStatDamage(targetHandle, statValue, delta, type, casterHandle, 1, spellBaseId));
		}

		// The unarmed swing (Interact action 0x11, empty main hand; behavior TypeIds 1089xx, ability id in the
		// tail). Retail's complete reply to an air swing is the plain echo-ack - no result block (combat capture
		// 2026-06-12). Damage for a TARGETED unarmed swing was not in the capture (its swings hit air), so the
		// damage resolution below reuses the proven armed path - an extension to validate in-client.
		public async Task SendUnarmedAttackResponse(INetworkClient client, byte[] rawPayload, uint mode, uint targetHandle)
		{
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Account.ClientInstance;
			await SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, mode));

			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget)
				return;

			var reqTail = rawPayload[(26 + (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4)))..];
			var abilityId = reqTail.Length >= 4
				? BinaryPrimitives.ReadUInt32BigEndian(reqTail.AsSpan(reqTail.Length - 4, 4))
				: 0u;
			var damage = (uint)CombatResolver.ResolveSpellDamage(abilityId, client.Session.Character?.Level ?? 1);
			await SendSwingDamage(client, instance, targetHandle, damage);
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

		// Melee cleave (#18): an AoC swing hits an arc, not just the selected target. After the primary hit, splash the
		// same damage to the hostile, living NPCs clustered around the target (CombatTargeting.CleaveTargets - a
		// target-centered cleave, robust without a decoded player-facing vector; the directional arc + per-direction
		// mitigation is the separate #20). Each secondary gets its own DamageResultIIR floating number and runs the
		// full damage/kill lifecycle (vitals push, corpse-loot, XP, kill-quest advance) via ApplyNpcDamage.
		private async Task ApplyMeleeCleave(INetworkClient client, uint instance, uint primaryHandle, uint damage)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.TryGetZone(player.MapId, out var zone) || !zone.TryGet(primaryHandle, out var primary))
				return;

			foreach (var secondary in CombatTargeting.CleaveTargets(zone, primary, CombatTargeting.DefaultCleaveRadius, CombatTargeting.DefaultMaxCleaveTargets))
			{
				await SendWorldPacket(client, CombatPacketCodec.BuildDamageResultBody(instance, secondary.Handle, damage));
				await ApplyNpcDamage(client, secondary.Handle, (int)damage, $"melee cleave from 0x{instance:X8}");
			}
		}

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

		// Awards the killer XP for a kill, advancing levels when the XP bar fills (LevelingResolver), and pushes a
		// 0x96B8DC59 stat update so the XP bar (and, on a ding, level + new vitals) move. Experience is within-level
		// progress. The new XP/level are PERSISTED immediately (SetCharacterProgress) so they survive relog instead of
		// waiting on the full-graph disconnect/autosave save (which the player can lose by crashing or which can roll
		// back on a navigation conflict).
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
				await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);
				await PushLevelUp(killer, character, levelsGained);
				_logger.LogInformation("Combat: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);

			var block = new CharacterStatBlock().Set(CharacterStatId.Exp, character.Experience).Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer, block, killer.Session.Account.ClientInstance);
			_logger.LogInformation("Combat: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
		}

		// A target NPC's live vitals, seeded from its real spawn health (recorded when the world burst spawned it)
		// so its health bar moves relative to the HP the client rendered; falls back to the placeholder for NPCs
		// not spawned via the burst (e.g. the built-NPC test).
		private EntityVitals GetOrAddNpcVitals(INetworkClient client, uint targetHandle)
		{
			return _combat.GetOrAdd(targetHandle,
				() =>
				{
					// NPCs spawned via the world burst get their real vitals from the simulation; this fallback only
					// covers NPCs with no world entity (e.g. the built-NPC test), so a default max is correct.
					var maxHealth = CombatResolver.DefaultNpcMaxHealth;
					return EntityVitals.FromMax(maxHealth, 0, maxHealth);
				});
		}

		// An NPC was killed by `killer`: a model NPC is marked dead (starting its respawn timer), despawned for nearby
		// players (the tick respawns it), and awards the killer XP. A non-model NPC (no world entity) is just dropped
		// from the session store.
		private async Task OnNpcKilled(INetworkClient killer, uint targetHandle, WorldEntity npcEntity, string cause)
		{
			if (npcEntity != null)
			{
				// MarkDead starts the corpse-linger + respawn timers. The killing blow already pushed health=0 vitals
				// (ApplyNpcDamage), so the client plays the death animation on the still-rendered creature; the world
				// tick then converts the lingering corpse to a lootable bag (NpcCorpseLootable) and respawns it later.
				// Don't despawn/convert here - that instant-popped the corpse before the death animation could play.
				npcEntity.MarkDead();
				// Record the killer so the corpse's loot bag is owned by them (the allowed looter), not whoever is in
				// range - retail's NewAllowedLooterIIR is a loot-PERMISSION grant (#263).
				npcEntity.LastAttackerHandle = killer.Session.Account.ClientInstance;

				// #204: the health=0 StatUpdate alone does NOT play the death animation (in-client verified) - the
				// client needs the explicit SimpleCharKilledByUpdateIIR (0xB066AFC2) kill record (docs/combat.md,
				// decoded from AssassinL23_..._resurect). Send it right after the killing-blow vitals so the lingering
				// corpse plays its death anim. Killer = the player who landed the blow.
				await SendWorldPacket(killer, WorldDeath.BuildKilledBy(targetHandle, killer.Session.Account.ClientInstance));

				await AwardKillXp(killer, npcEntity.Level);

				_logger.LogInformation("Combat: NPC {Name} 0x{Target:X8} killed ({Cause}); corpse lingers {Linger}s then becomes lootable, {Fate}.",
					npcEntity.Name,
					targetHandle,
					cause,
					WorldEntity.CorpseLingerSeconds,
					npcEntity.NoRespawn
						? "then fades (no respawn)"
						: $"respawns in {npcEntity.RespawnDelaySeconds}s");
				// Quest chain (#261): a kill advances/completes any matching kill goal. A chained stage (e.g. Casilda's
				// 13479) completes + supersedes to the next stage; a chainless kill quest keeps the legacy turn-in path.
				await _questProgression.AdvanceKillGoals(killer, npcEntity.Name);
				return;
			}

			_logger.LogInformation("Combat: entity 0x{Target:X8} killed ({Cause}); not in world model, no respawn.", targetHandle, cause);
			_combat.Remove(targetHandle);
		}

		// On level-up: emit the retail attribute-increase feedback (one AttributeChanged on-screen line per primary
		// that rose - the VISIBLE half of a ding, #209), then raise the player's live vitals to the new max and refill
		// them (a ding heals) and push the new Level + Exp/MaxExp + full vitals in one 0x96B8DC59 update.
		private async Task PushLevelUp(INetworkClient killer, Character character, uint levelsGained)
		{
			var instance = killer.Session.Account.ClientInstance;

			// The level-up EVENT first (0x6D314743) - fires the client's "reached level N" banner + effect (#209);
			// retail sends a SEPARATE event PER level ahead of the AttributeChanged lines (combat-levelup frame 9937,
			// and Hoxxling's L2/L3/L4 as three), each carrying [level, currentXP, xpThisLevel, xpNextLevel, 1, 8, 0].
			var fromLevel = character.Level - levelsGained;
			for (var lvl = fromLevel + 1; lvl <= character.Level; lvl++)
			{
				var thisXp = LevelingResolver.CumulativeXpForLevel(lvl);
				var nextXp = LevelingResolver.CumulativeXpForLevel(lvl + 1);
				var currentXp = lvl == character.Level
					? thisXp + character.Experience
					: thisXp;
				ReadOnlySpan<uint> deltas = [currentXp, thisXp, nextXp, 1u, 8u, 0u];
				await SendWorldFeedback(killer, NewLevelIirCodec.Build(instance, lvl, deltas));

				// The combos this class unlocks AT this level, from the class<->combo ACCESS map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the
				// class-independent LevelComboCatalog (casters have no rows); it ships empty until populated from dumps,
				// so this is a no-op today.
				foreach (var combo in await _database.GetClassCombosUnlockedBetween(character.Class, lvl - 1, lvl))
					await SendWorldFeedback(killer, ComboBookIirCodec.Build(instance, combo.Id));
			}

			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, character.Level, levelsGained))
				await SendWorldFeedback(killer, AttributeChangedFeedback.Build(instance, attributeId, amount));

			var vitals = GetOrAddPlayerVitals(killer);
			var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			vitals.MaxHealth = max.Health;
			vitals.MaxStamina = max.Stamina;
			vitals.MaxMana = max.Mana;
			vitals.Health = max.Health;
			vitals.Stamina = max.Stamina;
			vitals.Mana = max.Mana;

			var block = vitals.ToVitalsBlock().Set(CharacterStatId.Level, character.Level).Set(CharacterStatId.Exp, character.Experience).Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer, block, killer.Session.Account.ClientInstance);

			// Level-gated ability unlocks (#209/#73): retail grants the abilities that unlock at the new level as
			// ANNOUNCED 0x2052s at the ding (Rest at the L1->L2 ding - Blablabla frame 9937, Blablatest 1357,
			// Stihetfv 685 - and in every L2+ char's login re-grant). The class<->ability map gives the per-class
			// unlock level, so the ding grants are the band newly crossed.
			var previousLevel = character.Level - levelsGained;
			if (_announceAddSpecialAbility != null)
			{
				foreach (var ability in await _database.GetClassAbilitiesUnlockedBetween(character.Class, previousLevel, character.Level))
					await _announceAddSpecialAbility(killer, ability);
			}

			// Level-gated SPELL unlocks (#209/#74): the class's spells whose Spells.Level falls in the newly-crossed
			// range - learned (DispatchLearnSpell) and persisted to CharacterSpells so they ride every relog, exactly
			// like .learnspell / a create-flow starter.
			if (_learnSpell != null)
			{
				foreach (var spellId in await _database.GetClassSpellsUnlockedBetween(character.Class, previousLevel, character.Level))
				{
					await _learnSpell(killer, spellId);
					await _database.AddCharacterSpell(character.Id, spellId);
				}

				// Feat scaffold (#298/#251): grant the harvested class scaffold so the just-learned spells become
				// castable (the client gates usability on feat presence; the exact spell->feat map is server-GDB-only).
				var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
				if (fullScaffold != null)
					await _database.EnsureCharacterSkills(character.Id, fullScaffold);
			}
		}

		// The NPC's authoritative live vitals: the world-model entity's if it is a tracked NPC (also returned, so the
		// caller can run its death lifecycle), otherwise the per-session vitals (npcEntity null).
		private EntityVitals ResolveNpcVitals(INetworkClient client, uint targetHandle, out WorldEntity npcEntity)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(targetHandle, out npcEntity) && !npcEntity.IsPlayer && npcEntity.Vitals != null)
				return npcEntity.Vitals;

			npcEntity = null;
			return GetOrAddNpcVitals(client, targetHandle);
		}

		// The hit side of a swing: DamageResultIIR + the target's live-vitals application + the player's EnergyIIR
		// stamina update (the retail post-ack trio of the armed swing path).
		private async Task SendSwingDamage(INetworkClient client, uint instance, uint targetHandle, uint damage)
		{
			await SendWorldPacket(client, CombatPacketCodec.BuildDamageResultBody(instance, targetHandle, damage));

			await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			var player = GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, CombatPacketCodec.BuildEnergyBody(instance, player.Stamina));
		}

		// Pushes a 0x96B8DC59 stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}

		// Frames a FormatFeedbackIIR body (which already carries its own u32 length prefix) raw on the world
		// conversation - unlike SendWorldPacket, which length-wraps a bare body.
		private static Task SendWorldFeedback(INetworkClient client, byte[] feedbackBody)
		{
			return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(feedbackBody).SendAsync(client);
		}

		// Frames a raw 0x2000 body on the world conversation.
		private static Task SendWorldPacket(INetworkClient client, byte[] body)
		{
			return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
		}
	}
}
