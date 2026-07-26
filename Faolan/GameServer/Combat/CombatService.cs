using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// Combat domain logic: resolves melee swings and spell casts into damage, drives the NPC kill lifecycle
	// (despawn + scheduled respawn via the world model), awards XP / levels, tracks kill-objective quest progress, and
	// builds the combat wire packets (the BehaviorControlCenter ack, DamageResult, Energy, and 0x96B8DC59 vitals).
	// Extracted out of the GameServer listener as a composed collaborator (like WorldBroadcaster): it reads/writes the
	// world model via the WorldSimulation, broadcasts world effects via the WorldBroadcaster, and frames its own
	// packets on the world conversation (WorldSenderEndpoint). Vitals resolve from the world model (the authoritative owner);
	// a per-session CombatState backs NPCs not in the model (e.g. the built-NPC test). See protocol_stats_and_combat.
	public sealed class CombatService
	{
		// The blast radius (world units) a ground-targeted AoE damages around its reticle position.
		// [V0: a single interim radius - the real per-spell AoE radius is not in the RDB spell record or any bulk
		// source we import (Spell has only Range = cast distance). Source it per spell - see Gitea follow-up.]
		public const float GroundAoeRadiusUnits = 5f;

		// The channel tick interval (ms) for a channeled ground AoE. [V0: a gameplay approximation - the exact retail
		// cadence is muddied in the mixed casting-combat capture; each tick's DamageResult packet is byte-exact.]
		private const int GroundAoeTickIntervalMs = 1500;

		// The stat a spell-damage StatDamageIIR reports against (retail's outgoing spell hits use statId 0x0a in the
		// casting-combat capture). Distinct from the melee DamageResult path - the client renders a spell hit's number
		// from StatDamage (the same IIR that renders incoming DoT numbers on the player), NOT DamageResult.
		private const uint SpellDamageStatId = 0x0A;
		// The 8-byte filler retail writes where an item identity quad would otherwise go in a behavior ack
		// (B2 90 80 5C 29 E6 27 CA - the same null-identity padding as the 0xF result block and the 0x2052 grant).

		// Fallback vitals store for NPCs NOT in the world model (e.g. the built-NPC test) - model NPCs and players
		// keep their vitals on their WorldEntity (the single source of truth). In-memory, never persisted.
		private readonly CombatState _combat = new();

		// The announced 0x2052 ability grant lives on the listener (GrantAbilitiesBuilder); the XP-driven ding needs
		// it for level-gated unlocks (#209/#73 - Rest at L2), so it's injected as the single-op delegate seam the
		// composition pattern prescribes (CLAUDE.md). Null-safe for tests that construct the service without it.
		private readonly Func<INetworkClient, Ability, Task>? _announceAddSpecialAbility;

		// The listener's live spell-learn op (DispatchLearnSpell 0x200A), for the level-up spell unlocks (#209). Same
		// narrow-seam pattern as _announceAddSpecialAbility. Null-safe for tests that construct the service without it.
		private readonly Func<INetworkClient, uint, Task>? _learnSpell;
		private readonly FatalityService? _fatality; // #140: the fatality burst on a killing blow
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;

		private readonly WorldSimulation _sim;

		// Quest kill-goals route through the QuestProgressionService (#261), which owns the broadcaster + system-message
		// seams; combat packets frame themselves directly (SendWorldPacket), so this service no longer needs either.
		public CombatService(WorldSimulation sim, IDatabaseRepository database, QuestProgressionService questProgression, ILogger logger,
			Func<INetworkClient, Ability, Task>? announceAddSpecialAbility = null, Func<INetworkClient, uint, Task>? learnSpell = null, FatalityService? fatality = null)
		{
			_fatality = fatality;
			_sim = sim;
			_database = database;
			_questProgression = questProgression;
			_logger = logger;
			_announceAddSpecialAbility = announceAddSpecialAbility;
			_learnSpell = learnSpell;
		}

		// Channeled ground AoE (#349): ticks damage over the cast/channel duration so the client sees a floating number
		// each tick - retail's Deathly Blizzard ticks each NPC repeatedly over its ~10.5s channel (casting-combat
		// capture: DamageResultIIR 0xFEC4158A per target, ~1-2s apart), where we used to apply one silent hit at the
		// end. The spell's damage is spread across the ticks; the blast area is re-checked each tick. Stops early when
		// the cast is interrupted (ActiveCastToken bumped by a move / the client's cancel) or the player disconnects.
		// Fire-and-forget from cast begin. The tick CADENCE is interim; each tick's DamageResult packet is byte-exact.
		public async Task ChannelGroundAoe(INetworkClient client, uint casterHandle, Vector3 target, uint spellId, int channelMs, uint castToken)
		{
			var casterLevel = client.Session.Character.Level;
			var total = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamage ?? 0);
			var tickCount = Math.Max(1, channelMs / GroundAoeTickIntervalMs);
			var perTick = Math.Max(1, total / tickCount);

			try
			{
				for (var tick = 0; tick < tickCount; tick++)
				{
					await Task.Delay(GroundAoeTickIntervalMs);
					if (client.Session.Account == null)
						return; // disconnected
					if (client.Session.ActiveCastToken != castToken)
						return; // interrupted (moved / cancelled) - stop ticking

					await ApplyAoeTick(client, casterHandle, target, spellId, perTick);
				}

				_logger.LogInformation("Ground AoE channel spell 0x{Spell:X} at {Pos}: {Ticks} ticks of {Dmg} each.", spellId, target, tickCount, perTick);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Ground AoE channel failed.");
			}
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

		// Resolves an INSTANT ground-targeted AoE (#236) - a single blast on completion. Empty ground is a no-op like
		// retail. Channeled AoEs (a cast time) go through ChannelGroundAoe instead so they tick.
		public async Task ResolveGroundAoe(INetworkClient client, uint casterHandle, Vector3 target, uint spellId)
		{
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamage ?? 0);
			var count = await ApplyAoeTick(client, casterHandle, target, spellId, damage);
			_logger.LogInformation("Ground AoE spell 0x{Spell:X} at {Pos}: hit {Count} target(s) for {Damage} each.", spellId, target, count, damage);
		}

		// Resolves an offensive cast: applies the damage to the target's live vitals (a 0x96B8DC59 update moves the
		// health bar) and shows the spell hit's floating number via StatDamage (see SendSpellDamageNumber). The
		// ApplySpell VFX is broadcast by the caller.
		public async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId)
		{
			var caster = _sim.PlayerWorldEntity(client);
			caster?.LastCombatAt = DateTimeOffset.UtcNow; // casting at a target -> in combat
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, caster?.MagicDamage ?? 0);
			await ApplyNpcDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}");
			await SendSpellDamageNumber(client, targetHandle, casterHandle, damage, spellId);
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

			var instance = client.Session.Character.Id;
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
			var level = client.Session.Character.Level;
			// #395: melee damage is driven by the equipped main-hand weapon (min/max from the RDB template), not a flat
			// level value, so a better weapon hits harder. Unarmed / no imported weapon damage falls back to the level curve.
			var weapon = await _database.GetEquippedWeaponDamage(client.Session.Character.Id);
			var attackerEntity = _sim.PlayerWorldEntity(client);
			attackerEntity?.LastCombatAt = DateTimeOffset.UtcNow; // attacking -> in combat (halts HP/mana regen)
			// Roll between the weapon's min and max for per-swing variance (#395); Combat Rating adds a flat bonus (#399).
			var damage = (uint)CombatResolver.ResolveMeleeDamage(weapon?.Min, weapon?.Max, level, Random.Shared.Next(101), attackerEntity?.CombatRating ?? 0);
			// #399: roll a critical hit off the player's Critical Rating; a crit scales the damage by the base 1.5x plus
			// their Critical Damage Rating. A crit is NOT a distinct wire flag - RE of all captured DamageResults
			// (2026-07-22) showed the `type` field is the ATTACKER's damage school (constant per attacker; the player's
			// physical melee is always type 3), the two trailing fields are always 0, and there is no crit indicator.
			// So retail conveys a crit purely by the larger number; we send the player's melee type (3) either way.
			var crit = CombatResolver.IsCriticalHit(attackerEntity?.CriticalRating ?? 0, Random.Shared.Next(100));
			if (crit)
				damage = (uint)CombatResolver.ApplyCritDamage((int)damage, attackerEntity?.CriticalDamageRating ?? 0);
			const DamageSchool school = DamageSchool.Crushing; // the player's physical-melee damage school (capture-derived)
			var next = (ushort)(seq + 1);

			// 1) BehaviorControlCenterIIR ack - releases the swing.
			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, b =>
				b.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(instance)
					.WriteUInt16(next)
					.WriteUInt32(0x0000000Fu)
					.WriteArrayUInt32Length(echoedArgs)
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
					.WriteUInt32(EntityClass.Character)
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
					.WriteUInt32(0))));

			// 2) DamageResultIIR - the hit / floating number (crits are just a bigger number, same school).
			await SendDamageNumber(client, targetHandle, instance, damage, school);

			// Apply to the target's live vitals (world-authoritative), push its health bar, and run the kill
			// lifecycle (despawn + scheduled respawn) on death.
			await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, CombatPacketCodec.BuildEnergyBody(instance, player.Stamina));
		}

		// The unarmed swing (Interact action 0x11, empty main hand; behavior TypeIds 1089xx, ability id in the
		// tail). Retail's complete reply to an air swing is the plain echo-ack - no result block (combat capture
		// 2026-06-12). Damage for a TARGETED unarmed swing was not in the capture (its swings hit air), so the
		// damage resolution below reuses the proven armed path - an extension to validate in-client.
		public async Task SendUnarmedAttackResponse(INetworkClient client, byte[] rawPayload, uint mode, uint targetHandle)
		{
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Character.Id;
			await SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, mode));

			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget)
				return;

			var reqTail = rawPayload[(26 + (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4)))..];
			var abilityId = reqTail.Length >= 4
				? BinaryPrimitives.ReadUInt32BigEndian(reqTail.AsSpan(reqTail.Length - 4, 4))
				: 0u;
			var damage = (uint)CombatResolver.ResolveSpellDamage(abilityId, client.Session.Character.Level);
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

			var cost = CombatResolver.ResolveSpellManaCost(spellId, client.Session.Character.Level);
			player.ApplyManaDelta(-(int)cost);
			await SendStatUpdate(client, player.ToVitalsBlock(), client.Session.Character.Id);
		}

		// Damages every attackable NPC within GroundAoeRadiusUnits of `target` for one tick: shows the floating
		// DamageResult number (0xFEC4158A) over each victim + applies the HP move. Friendly NPCs (civilians /
		// quest-givers, Disposition == Friendly) are spared; hostile + neutral are hit; players never (no PvP model).
		// Re-evaluated each call so NPCs that wander in/out of the blast are picked up per tick. Returns the count hit.
		private async Task<int> ApplyAoeTick(INetworkClient client, uint casterHandle, Vector3 target, uint spellId, int tickDamage)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return 0;

			var victims = _sim
				.World.ZoneForEntity(player)
				.EntitiesInRange(target, GroundAoeRadiusUnits, casterHandle)
				.Where(e => !e.IsPlayer && e.Disposition != WorldEntity.DispositionFriendly)
				.Select(e => e.Handle)
				.ToList();

			foreach (var handle in victims)
			{
				await ApplyNpcDamage(client, handle, tickDamage, $"AoE spell {spellId} from 0x{casterHandle:X8} at {target}");
				await SendSpellDamageNumber(client, handle, casterHandle, tickDamage, spellId);
			}

			return victims.Count;
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
				await SendDamageNumber(client, secondary.Handle, instance, damage, DamageSchool.Crushing);
				await ApplyNpcDamage(client, secondary.Handle, (int)damage, $"melee cleave from 0x{instance:X8}");
			}
		}

		private async Task ApplyNpcDamage(INetworkClient client, uint targetHandle, int damage, string cause)
		{
			var target = ResolveNpcVitals(client, targetHandle, out var npcEntity);
			if (target.IsDead)
				return; // already dead this life; ignore overkill until it respawns

			target.ApplyHealthDelta(-damage);

			// #140/#440: a lethal blow's fatality burst goes out BEFORE the client is told the target is dead - not
			// merely before the death records. Retail's kill window never carries a health-to-zero update ahead of the
			// burst: in Stickienecro's ice-strike fatality the victim's only damage record is the StatDamage at idx 542,
			// AFTER the three ApplySpells at 535-538, and the death pose (546) and death BccIIR (549) follow that. Ours
			// pushed the victim's vitals at health 0 one packet ahead of the burst, so the client learned the mob was
			// dead - and had to choose how it dies - before it knew the kill was a fatality.
			if (target.IsDead && _fatality != null && npcEntity != null)
				await _fatality.OnKill(client, _sim.PlayerWorldEntity(client), npcEntity);

			await SendStatUpdate(client, target.ToVitalsBlock(), targetHandle);

			if (target.IsDead)
				await OnNpcKilled(client, targetHandle, npcEntity!, cause);
		}

		// Awards the killer XP for a kill, advancing levels when the XP bar fills (LevelingResolver), and pushes a
		// 0x96B8DC59 stat update so the XP bar (and, on a ding, level + new vitals) move. Experience is within-level
		// progress. The new XP/level are PERSISTED immediately (SetCharacterProgress) so they survive relog instead of
		// waiting on the full-graph disconnect/autosave save (which the player can lose by crashing or which can roll
		// back on a navigation conflict).
		private async Task AwardKillXp(INetworkClient killer, uint npcLevel)
		{
			var character = killer.Session.Character;

			var xp = CombatResolver.ResolveKillXp(npcLevel);
			character.Experience += xp;

			var (levelsGained, remaining) = LevelingResolver.Apply(character.Level, character.Experience);
			character.Experience = remaining;

			if (levelsGained > 0)
			{
				character.Level = (ushort)Math.Min(character.Level + levelsGained, LevelingResolver.MaxLevel);
				character.RegenerateMaxStats();
				await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);
				await PushLevelUp(killer!, character, levelsGained);
				_logger.LogInformation("Combat: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);

			var block = new CharacterStatBlock().Set(CharacterStatId.Exp, character.Experience).Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer!, block, killer!.Session.Character.Id);
			_logger.LogInformation("Combat: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
		}

		// A target NPC's live vitals, seeded from its real spawn health (recorded when the world burst spawned it)
		// so its health bar moves relative to the HP the client rendered; falls back to the placeholder for NPCs
		// not spawned via the burst (e.g. the built-NPC test).
		private EntityVitals GetOrAddNpcVitals(INetworkClient client, uint targetHandle)
		{
			return _combat.GetOrAdd(targetHandle, () =>
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
				npcEntity.LastAttackerHandle = killer.Session.Character.Id;

				// ⚠️ The FATALITY burst is NOT sent here. It goes out in ApplyNpcDamage, ahead of the killing blow's own
				// vitals push - see the comment there. Retail's ordering is burst -> the victim's damage -> death pose ->
				// death BccIIR, in 37 of 37 captured fatalities; the client cannot pick a finisher for a corpse it was
				// already told is dead (#140/#440).

				// #204/#349: the health=0 StatUpdate alone does NOT play the death animation. Retail drives an NPC
				// death with a death-pose MoveTask (type 0x06, freezes the corpse in place) + a death-behavior BccIIR
				// (behaviorId 41877) on the dying handle - decoded from NecroL80_..._casting-combat, where EVERY NPC kill
				// carries exactly this pair (and NO SimpleCharKilledBy: that record is player-death-only, driving the
				// local release/resurrect UI). Send both to the killer right after the killing-blow vitals so the
				// lingering corpse plays its death anim.
				await SendWorldPacket(killer, WorldDeath.BuildDeathPose(targetHandle, npcEntity.Position));
				await SendWorldPacket(killer, WorldDeath.BuildDeathBcc(targetHandle, npcEntity.DeathBehaviorId, WorldDeath.NpcDeathVariant));

				await AwardKillXp(killer, npcEntity.Level);

				_logger.LogInformation("Combat: NPC {Name} 0x{Target:X8} killed ({Cause}); corpse lingers {Linger}s then becomes lootable, {Fate}.", npcEntity.Name, targetHandle, cause,
					WorldEntity.CorpseLingerSeconds, npcEntity.NoRespawn
						? "then fades (no respawn)"
						: $"respawns in {npcEntity.RespawnDelaySeconds}s");
				// Quest chain (#261): a kill advances/completes any matching kill goal. A chained stage (e.g. Casilda's
				// 13479) completes + supersedes to the next stage; a chainless kill quest keeps the legacy turn-in path.
				// Matched by the STABLE target template id (the goal's TrailingRefId), name as fallback (#262).
				await _questProgression.AdvanceKillGoals(killer, npcEntity.NpcTemplateId, npcEntity.Name);
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
			var instance = killer.Session.Character.Id;

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
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the old
				// class-independent level-keyed catalog (casters have no rows); it ships empty until populated from dumps,
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

			var block = vitals
				.ToVitalsBlock()
				.Set(CharacterStatId.Level, character.Level)
				.Set(CharacterStatId.Exp, character.Experience)
				.Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			await SendStatUpdate(killer, block, killer.Session.Character.Id);

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

				// Ability/skill scaffold (#298/#251): grant the harvested class scaffold so the just-learned spells
				// become castable (the client gates usability on the ability/skill entry). NB this is the skill list,
				// NOT the feat tree - feats ride a separate FeatIIR (0x28da68a0); see [[feat_trees_decode_251]].
				var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
				if (fullScaffold != null)
					await _database.EnsureCharacterSkills(character.Id, fullScaffold);
			}
		}

		// The NPC's authoritative live vitals: the world-model entity's if it is a tracked NPC (also returned, so the
		// caller can run its death lifecycle), otherwise the per-session vitals (npcEntity null).
		private EntityVitals ResolveNpcVitals(INetworkClient client, uint targetHandle, out WorldEntity? npcEntity)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(targetHandle, out npcEntity) && npcEntity is { IsPlayer: false, Vitals: not null })
				return npcEntity.Vitals;

			npcEntity = null!;
			return GetOrAddNpcVitals(client, targetHandle);
		}

		// The floating number + combat-log line for a SPELL hit on an NPC. Retail carries spell damage on
		// StatDamageIIR (0xE9F24184) - the LockedToChanged -> StatDamage -> ApplySpell bundle, with NO DamageResult
		// (casting-combat capture) - so a spell hit must use StatDamage, not the melee DamageResult (which the client
		// only floats after a BCC attack behavior arms the target). Mirrors the incoming DoT-tick StatDamage that
		// already renders on the player: statValue = the target's resulting health, delta = -damage, caster = the
		// player, spellBase = the wire id >> 8. Call AFTER ApplyNpcDamage so the health is the post-hit value.
		private Task SendSpellDamageNumber(INetworkClient client, uint npcHandle, uint casterHandle, int damage, uint spellId)
		{
			var vitals = ResolveNpcVitals(client, npcHandle, out _);
			return SendStatDamage(client, npcHandle, vitals.Health, -damage, SpellDamageStatId, casterHandle, spellId >> 8);
		}

		// The hit side of a swing: DamageResultIIR + the target's live-vitals application + the player's EnergyIIR
		// stamina update (the retail post-ack trio of the armed swing path).
		private async Task SendSwingDamage(INetworkClient client, uint instance, uint targetHandle, uint damage)
		{
			await SendDamageNumber(client, targetHandle, instance, damage, DamageSchool.Crushing); // player physical melee

			await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			var player = GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, CombatPacketCodec.BuildEnergyBody(instance, player.Stamina));
		}

		// The generic "every behavior gets an echo-ack" reply (docs/subsystems/combat.md rule 1) for an Interact request with no
		// special result block - the air-swing path uses it, and so does a plain item-use (RightClickItem, e.g. the
		// Totem of Origin #206). Phase = the request's Mode, or the legacy session counter when the caller didn't
		// parse the args. No-op for a malformed (too-short) payload.
		public static Task SendBehaviorEchoAck(INetworkClient client, byte[] rawPayload, uint mode)
		{
			if (rawPayload.Length < 26)
				return Task.CompletedTask;

			var instance = client.Session.Character.Id;
			var phase = mode != 0
				? mode
				: (uint)(++client.Session.NormalAttackAckPhase);
			return SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, phase));
		}

		// Sends the cast-interrupt abort BccAck (#347): the state=0x0007 reply to the client's cast-cancel that drops
		// the cast bar, cancels the animation and pops the "Interrupted" text client-side. Built from the stored cast
		// BEGIN args + the incoming cancel payload (behaviorId + tail). Phase = the begin's Mode.
		public static Task SendCastInterruptAbort(INetworkClient client, byte[] beginArgs, byte[] cancelPayload, uint phase)
		{
			var instance = client.Session.Character.Id;
			return SendWorldPacket(client, BehaviorAckBody.BuildCastInterruptAbort(beginArgs, cancelPayload, instance, phase));
		}

		// The floating combat-damage number over the player when an NPC hits them (DamageResultIIR 0xFEC4158A,
		// target=player + attacker=NPC). The player-attacks-NPC direction already fires this from the swing/cast
		// resolve; this is the missing NPC-attacks-player half so the player sees the hit number, not just the HP
		// bar move. Byte-exact builder (CombatEffectBuilder), golden-tested vs the captured NPC->player hit. #274.
		// `school` is the attacker's DamageResult damage school (DamageSchool; NOT a hit result - see CombatEffectBuilder).
		// Defaults to Piercing, the school of the captured NPC melee hit (fr 6514) - a reasonable stand-in until we model
		// per-NPC/per-weapon schools; player-melee callers pass Crushing.
		public static Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage, DamageSchool school = DamageSchool.Piercing)
		{
			return SendWorldPacket(client, CombatEffectBuilder.BuildDamageResult(targetHandle, attackerHandle, damage, school));
		}

		// Acks an equip/unequip move (Interact 0x09) and appends the 0x201C bookkeeping entry retail sends with it:
		// prefix 0x65 + state = the destination equip slot for a move INTO a slot, prefix 0x68 + state 0 for a move
		// OUT of one (combat capture frames 289-290/320-321/375-376).
		public static async Task SendEquipMoveAck(INetworkClient client, byte[] rawPayload, uint mode, Item movedItem)
		{
			if (rawPayload.Length < 26)
				return;

			await SendWorldPacket(client, BehaviorAckBody.BuildEquipMoveAckBody(rawPayload, client.Session.Character.Id, mode, movedItem));
		}

		// Sends the NPC counter-attack ANIMATION ack to the player it hit: a BehaviorControlCenterIIR with actor = the
		// NPC handle (the decoded "second BccAck", docs/subsystems/combat.md), so the client plays the NPC's swing + the player's
		// hit-react. Visual only - the authoritative HP move rides the world-tick StatUpdate, so the damage here is just
		// the floating combat number. The animation/result params are capture-typical (not semantically decoded); the
		// swingCounter increments per NPC swing to mirror the per-actor counter the capture carries.
		public static Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter)
		{
			var playerHandle = client.Session.Character.Id;
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
			var playerHandle = client.Session.Character.Id;
			var body = NpcSpellCastBuilder.Build(playerHandle, npcHandle, spell.SpellId, spell.Payload);
			return SendWorldPacket(client, body);
		}

		// Retail's per-tick vitals regen (RegenIIR 0x28D0AC99, decoded): a compact [+hp][+mana][+stamina] delta the
		// client ADDS to the current bars, ×100 wire scale. Lets the bars regen smoothly without pushing a full
		// 0x96B8DC59 block every tick. RegenCodec.Build already frames it as the [u32 len][iir] carrier body, so it
		// rides the DispatchPassBlob directly (WriteArray, not WriteArrayUInt32Length).
		public static Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				RegenCodec.Build(handle, healthDelta, manaDelta, staminaDelta)));
		}

		// A DoT tick's StatDamageIIR (the health effect of an active bleed/HoT). idx 1 is the typical effect slot in
		// the capture; statValue = the resulting HealthCurrent so the client's bar/number matches the vitals move. #274
		public static Task SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return SendWorldPacket(client, CombatEffectBuilder.BuildStatDamage(targetHandle, statValue, delta, type, casterHandle, 1, spellBaseId));
		}

		// Pushes a 0x96B8DC59 stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}

		// Frames a FormatFeedbackIIR body (which already carries its own u32 length prefix) raw on the world
		// conversation - unlike SendWorldPacket, which length-wraps a bare body.
		private static Task SendWorldFeedback(INetworkClient client, byte[] feedbackBody)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, feedbackBody));
		}

		// Frames a raw 0x2000 body on the world conversation.
		private static Task SendWorldPacket(INetworkClient client, byte[] body)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}
	}
}
