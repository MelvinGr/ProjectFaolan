using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network;
using Faolan.Core.Progression;
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
	// packets on the world conversation (WorldSenderEndpoint). Vitals resolve from the world model, which is their ONLY
	// owner: a handle that does not resolve there is refused and reported, never backed by invented per-session state
	// (#695 - that fallback silently ate every hit on a target outside the player's zone view). See
	// protocol_stats_and_combat.
	public sealed class CombatService
	{
		// The blast radius (world units) a ground-targeted AoE damages around its reticle position, for a spell the
		// client states none for. The per-spell radius is `Spell.AreaRadius` (#349) - the `Radius` key of the spell's
		// area-targeting GameAction element - and 1,023 of the 20,848 spells carry one. This is the fallback for the
		// rest, and Deathly Blizzard is one of them: it has a stated channel interval but no stated area, so its blast
		// really is server-side. 5 m is where the client's own distribution centres (5 m x201, 6 m x160, 4 m x86).
		public const float GroundAoeRadiusUnits = 5f;

		// The channel tick interval (ms) for a channeled spell the client states none for. The per-spell value is
		// `Spell.ChannelingIntervalMs` (RDB field 447 `ChannelingInterval`, 516 spells). Kept as the fallback only.
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
		private readonly Func<INetworkClient, Ability, Task> _announceAddSpecialAbility;

		// The listener's live spell-learn op (DispatchLearnSpell 0x200A), for the level-up spell unlocks (#209). Same
		// narrow-seam pattern as _announceAddSpecialAbility. Null-safe for tests that construct the service without it.
		private readonly Func<INetworkClient, uint, Task> _learnSpell;
		private readonly FatalityService _fatality; // #140: the fatality burst on a killing blow
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;

		private readonly WorldSimulation _sim;

		// Quest kill-goals route through the QuestProgressionService (#261), which owns the broadcaster + system-message
		// seams; combat packets frame themselves directly (SendWorldPacket), so this service no longer needs either.
		public CombatService(WorldSimulation sim, IDatabaseRepository database, QuestProgressionService questProgression, ILogger logger,
			Func<INetworkClient, Ability, Task> announceAddSpecialAbility, Func<INetworkClient, uint, Task> learnSpell, FatalityService fatality)
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
			var total = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamageFor(spellId) ?? 0,
				_sim.PlayerWorldEntity(client)?.SpellBoost(spellId) ?? 0);

			// The client STATES the cadence: RDB field 447 `ChannelingInterval` (#349). Corroborated where the two
			// sources overlap - Deathly Blizzard's 2,500 ms, Waves of Flame's 1,000 and Resting's 500 are each exactly
			// what the captured StatDamage gaps measured independently (Spell.DotIntervalMs), on all three and with no
			// disagreement. Only a spell the client states none for falls back to the constant.
			var spell = await _database.GetSpell(spellId);
			var intervalMs = spell?.ChannelingIntervalMs > 0
				? (int)spell.ChannelingIntervalMs
				: GroundAoeTickIntervalMs;
			var tickCount = Math.Max(1, channelMs / intervalMs);
			var perTick = Math.Max(1, total / tickCount);

			try
			{
				for (var tick = 0; tick < tickCount; tick++)
				{
					await Task.Delay(intervalMs);
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

		/// <summary>
		///     Reseeds the LIVE entity's three pools (max and current) for a character whose max has just changed, and
		///     hands the vitals back so the caller can push them.
		/// </summary>
		/// <remarks>
		///     Every path that CHANGES a max has to do this and then send <see cref="EntityVitals.ToVitalsBlock" />.
		///     Both halves matter: the live entity owns its own maxima, so skipping the reseed leaves the world tick
		///     pushing the STALE max straight back; and the max ids only ever reach the client when something sends
		///     them, so skipping the push leaves the client's bar on whatever it was seeded with at world entry
		///     (#450 - a `.level 80` left the client rendering `4285 / 1270`, current at 3.4x the max, because the
		///     GM path pushed the new CURRENT alone).
		///     <para>
		///         ⚠️ It rebuilds through <see cref="EntityVitals.FromCharacter" /> with the SAME gear + feat + class
		///         base that world entry folds in, not from the persisted <see cref="CharacterMaxStats" /> alone.
		///         Reseeding from the bare persisted row silently drops every bonus the character was carrying, so a
		///         `.level` would hand back a max BELOW the one world entry had computed - a level-up that lowers your
		///         health bar.
		///     </para>
		/// </remarks>
		public async Task<EntityVitals> ReseedFromMaxStats(INetworkClient client)
		{
			var character = client.Session.Character;
			var statBonuses = MaxStatAggregator.Combine(
				MaxStatAggregator.AggregateGearBonuses(await _database.GetEquippedStatItems(character.Id, character.ActiveWeaponSet)),
				MaxStatAggregator.AggregateFeatBonuses(await _database.GetCharacterFeatEffects(character.Id), character.Level));
			var classBase = await _database.GetClassAttributeBase(character.Class, character.Level);
			var seeded = EntityVitals.FromCharacter(character, statBonuses, classBase);
			var vitals = GetOrAddPlayerVitals(client);

			vitals.ReseedMaxFrom(seeded);
			return vitals;
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

			return target == null
				? Task.CompletedTask // not a live NPC in the killer's zone - the resolver already said so
				: ApplyNpcDamage(client, targetHandle, (int)target.Health, ".kill command");
		}

		// Resolves an INSTANT ground-targeted AoE (#236) - a single blast on completion. Empty ground is a no-op like
		// retail. Channeled AoEs (a cast time) go through ChannelGroundAoe instead so they tick.
		public async Task ResolveGroundAoe(INetworkClient client, uint casterHandle, Vector3 target, uint spellId)
		{
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamageFor(spellId) ?? 0,
				_sim.PlayerWorldEntity(client)?.SpellBoost(spellId) ?? 0);
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
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, caster?.MagicDamageFor(spellId) ?? 0, caster?.SpellBoost(spellId) ?? 0);
			await ApplyNpcDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}",
				() => SendSpellDamageNumber(client, targetHandle, casterHandle, damage, spellId));
		}

		// The melee swing / auto-attack (Combat:Melee, BehaviorId 0x0F). Retail's response sequence, reproduced from the
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
		public async Task SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle, uint mode = 0, ComboSwing? combo = null)
		{
			// rawPayload = [bodyLen u32][subop u32][c350 u32][instance u32][seq u16][behaviorId u32][argsLen u32][args][reqTail]
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Character.Id;

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
				await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, phase)));
				return;
			}

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

			// #20 directional combat: the swing comes from one of the client's five zones, and the defender's shield rose
			// says how many of its three guards cover the ARC that zone belongs to. An unguarded arc takes 20% MORE, a
			// triple-stacked one 66% less - the client's own Combat_PvE{0..3}ShieldBonus table.
			// The defender's own rose is read off whatever the handle resolves to, NPC or player alike - a player carries
			// one now (#620), set by the client and relayed by HandlePlayerShieldRose - and a player defender switches
			// the table to the PvP column, which is the gentler one (-0.15/-0.30 where PvE mitigates -0.33/-0.66).
			//
			// ⚠️ A SWING IS ALWAYS DIRECTED (#673), which this used to have backwards ("a combo step is undirected and
			// passes through unchanged"). The direction ABILITY in the request tail is only how a PLAIN swing states it:
			// word 5 is absent on 107 of the 515 captured player swings, and every one of those 88 result records still
			// carries a direction code (0xCA x58, 0xCB x22, 0xC9 x8). For a combo step the direction is the KEY the
			// player pressed to reach it, which the catalog states - `ComboSwing.Direction`, off `Combo.Actions`.
			//
			// The fallback is only for a combo with no directional grammar at all (a reactive/magic marker, a `Thrust`
			// step). It is spent on BOTH the mitigation and the ack, so the wire and the damage still agree on every
			// swing - the invariant #673 established - rather than one of them silently disagreeing on the rare case.
			var swingDirection = CombatShieldRose.DirectionFor(BehaviorTailScan.AbilityId(BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Tail))
			                     ?? combo?.Direction ?? SwingDirection.Overhead;
			var swingArc = CombatShieldRose.ArcOf(swingDirection);
			WorldEntity? defenderEntity = null;
			if (attackerEntity != null && _sim.World.TryGetZone(attackerEntity.MapId, out var attackerZone) && attackerZone.TryGet(targetHandle, out var defender))
				defenderEntity = defender;

			damage = CombatShieldRose.ApplyArcMitigation(damage, defenderEntity?.ShieldDistribution ?? 0, swingArc, defenderEntity?.IsPlayer == true);
			const DamageSchool school = DamageSchool.Crushing; // the player's physical-melee damage school (capture-derived)

			// 1) BehaviorControlCenterIIR ack - releases the swing. The record (header, echoed args, 81-byte result
			// block naming the VICTIM) is BehaviorAcks.HitResultAck's; this path used to write all of it inline, which
			// made it the sixth hand-rolled copy of a message that has one codec (#530 wave 7).
			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.HitResultAck(rawPayload, instance, phase, targetHandle, damage, swingDirection)));

			// 2) DamageResultIIR - the hit / floating number (crits are just a bigger number, same school).
			await SendDamageNumber(client, targetHandle, instance, damage, school);

			// Apply to the target's live vitals (world-authoritative), push its health bar, and run the kill
			// lifecycle (despawn + scheduled respawn) on death.
			await ApplyNpcDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, EnergyCodec.Build(instance, player.Stamina));
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
			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, mode)));

			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget)
				return;

			// The unarmed swing's ability id is the LAST word of the request's behaviour TAIL - an opaque region at the
			// codec (#536), read by name through BehaviorTailScan rather than at `26 + <u32 at 22>` here.
			var abilityId = BehaviorTailScan.AbilityId(BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Tail);
			var damage = (uint)CombatResolver.ResolveSpellDamage(abilityId, client.Session.Character.Level);
			await SendSwingDamage(client, instance, targetHandle, damage);
		}

		// Spends the caster's mana for a cast and pushes the caster's own 0x96B8DC59 update so the mana bar drops -
		// the spell/mana analogue of the melee swing's stamina cost. (Mana isn't carried by EnergyIIR, which is
		// stamina-only in the captures; it rides the vitals stat block.) The cost is the spell's own RDB percentage
		// of the pool (#629). No-op if the player has no mana pool, or if the spell costs nothing.
		public async Task SpendCasterMana(INetworkClient client, uint spellId)
		{
			var player = GetOrAddPlayerVitals(client);
			if (player.MaxMana == 0)
				return;

			var spell = await _database.GetSpell(spellId);
			var cost = CombatResolver.ResolveSpellManaCost(spell?.ManaCost ?? 0, player.MaxMana, spell?.ManaCostIsPercent ?? false);
			if (cost == 0)
				return;

			player.ApplyManaDelta(-(int)cost);
			await SendStatUpdate(client, player.ToCurrentsBlock(), client.Session.Character.Id); // #586: a cast MOVES mana
		}

		// Spends the stamina a completed melee combo costs and pushes the player's own vitals so the bar drops - the
		// combo twin of SpendCasterMana. The COST comes from ComboService (the client's own formula); this half is
		// only the deduction plus the push, which is why it takes a number rather than a combo. No-op for a free
		// combo or a player with no stamina pool.
		public async Task SpendComboStamina(INetworkClient client, uint cost)
		{
			if (cost == 0)
				return;

			var player = GetOrAddPlayerVitals(client);
			if (player.MaxStamina == 0)
				return;

			player.Stamina = cost >= player.Stamina ? 0 : player.Stamina - cost;
			await SendStatUpdate(client, player.ToCurrentsBlock(), client.Session.Character.Id);
		}

		// Damages every attackable NPC within the spell's blast radius of `target` for one tick: shows the floating
		// DamageResult number (0xFEC4158A) over each victim + applies the HP move. Friendly NPCs (civilians /
		// quest-givers, Disposition == Friendly) are spared; hostile + neutral are hit; players never (no PvP model).
		// Re-evaluated each call so NPCs that wander in/out of the blast are picked up per tick. Returns the count hit.
		private async Task<int> ApplyAoeTick(INetworkClient client, uint casterHandle, Vector3 target, uint spellId, int tickDamage)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return 0;

			// Per-spell where the client states one (Spell.AreaRadius, #349); GroundAoeRadiusUnits otherwise.
			var spell = await _database.GetSpell(spellId);
			var radius = spell?.AreaRadius > 0
				? spell.AreaRadius
				: GroundAoeRadiusUnits;

			var victims = _sim
				.World.ZoneForEntity(player)
				.EntitiesInRange(target, radius, casterHandle)
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

		// Melee cleave (#18): an AoC swing damages "a small area IN FRONT OF YOU" (Funcom's own quickstart), not just
		// the selected target and not a ball around it. After the primary hit, splash the same damage to the hostile,
		// living NPCs inside the attacker's frontal cone (CombatTargeting.CleaveTargets). The directional arc a swing
		// is aimed at and its shield mitigation are the separate #20/#621 - the direction picks which of the
		// defender's guards you meet, the cone picks who you meet. Each secondary gets its own DamageResultIIR
		// floating number and runs the full damage/kill lifecycle (vitals push, corpse-loot, XP, kill-quest advance)
		// via ApplyNpcDamage.
		//
		// Same damage as the primary, deliberately: the quickstart says the primary takes more, and the only shipped
		// number for it is `Combat_{PvE,PvP}DamageReductionMainOnSplash` = 1 - which the client registers and never
		// reads (TweakGlobals: reads=0, so it is ours to apply), and whose value under any non-absurd reading is a
		// no-op. Inventing a fraction the balance sheet does not carry would be worse than matching it.
		private async Task ApplyMeleeCleave(INetworkClient client, uint instance, uint primaryHandle, uint damage)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.TryGetZone(player.MapId, out var zone))
				return;

			zone.TryGet(primaryHandle, out var primary);
			foreach (var secondary in CombatTargeting.CleaveTargets(zone, player, primary, CombatTargeting.DefaultCleaveReach,
				         CombatTargeting.DefaultCleaveHalfAngleDegrees, CombatTargeting.DefaultMaxCleaveTargets))
			{
				await SendDamageNumber(client, secondary.Handle, instance, damage, DamageSchool.Crushing);
				await ApplyNpcDamage(client, secondary.Handle, (int)damage, $"melee cleave from 0x{instance:X8}");
			}
		}

		// `onDamaged` is the caller's own damage RECORD (the floating number) - the spell path's StatDamage. It runs
		// between the fatality roll and the vitals/death records because that is where retail puts it: in Stickienecro's
		// ice-strike fatality the victim's StatDamage is idx 542, after the burst (535-538) and before the death pose
		// (546) and BccIIR (549). Sending it after the corpse was already posed put the killing blow's own number
		// behind the death.
		private async Task ApplyNpcDamage(INetworkClient client, uint targetHandle, int damage, string cause, Func<Task>? onDamaged = null)
		{
			var target = ResolveNpcVitals(client, targetHandle, out var npcEntity);
			if (target == null)
				return; // unresolvable target - already reported by the resolver (#695)

			if (target.IsDead)
				return; // already dead this life; ignore overkill until it respawns

			// Being hit puts the mob IN COMBAT with whoever hit it (#695). The world tick reads both: the handle is the
			// aggro source proximity cannot give it (a caster/archer standing outside NpcAggroRange), and the timestamp
			// is what stops the anti-whittle full-heal from resetting a mob mid-fight. Without this the mob neither
			// fought back nor kept its wounds - every hit after the first was erased before the next one landed.
			if (npcEntity != null)
			{
				npcEntity.LastAttackerHandle = _sim.PlayerWorldEntity(client)?.Handle ?? npcEntity.LastAttackerHandle;
				npcEntity.LastCombatAt = DateTimeOffset.UtcNow;
			}

			target.ApplyHealthDelta(-damage);

			// #140/#440: a lethal blow's fatality burst goes out BEFORE the client is told the target is dead - not
			// merely before the death records. Retail's kill window never carries a health-to-zero update ahead of the
			// burst: in Stickienecro's ice-strike fatality the victim's only damage record is the StatDamage at idx 542,
			// AFTER the three ApplySpells at 535-538, and the death pose (546) and death BccIIR (549) follow that. Ours
			// pushed the victim's vitals at health 0 one packet ahead of the burst, so the client learned the mob was
			// dead - and had to choose how it dies - before it knew the kill was a fatality.
			if (target.IsDead && _fatality != null && npcEntity != null)
				await _fatality.OnKill(client, _sim.PlayerWorldEntity(client), npcEntity);

			if (onDamaged != null)
				await onDamaged();

			// #586: a hit MOVES a pool, so retail's currents-only form is what goes out here.
			await SendStatUpdate(client, target.ToCurrentsBlock(), targetHandle);

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

			// The sheet's kill counter (#447). Persisted here rather than at autosave for the same reason the XP is:
			// a kill is earned once, and a crash between the kill and the save loses it.
			character.Kills++;
			await _database.SetCharacterKillCounts(character.Id, character.Kills, character.Deaths);

			var (levelsGained, remaining) = LevelingResolver.Apply(character.Level, character.Experience);
			character.Experience = remaining;

			if (levelsGained > 0)
			{
				character.Level = (ushort)Math.Min(character.Level + levelsGained, LevelingResolver.MaxLevel);

				// No ceiling to store: the vitals max is DERIVED from (Class, Level), so writing the level IS writing
				// the new max (#578). It used to be a persisted CharacterMaxStats row that only the `.level` GM command
				// ever updated - a combat level-up wrote the level and left the row at its creation value, so the next
				// login re-seeded current health from the old ceiling and the player came back at a fraction of their
				// bar (a level-2 necromancer reading 89/955).
				await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);
				await PushLevelUp(killer!, character, levelsGained);
				_logger.LogInformation("Combat: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);

			var block = new CharacterStatBlock().SetExperience(character.Level, character.Experience);
			await SendStatUpdate(killer!, block, killer!.Session.Character.Id);
			_logger.LogInformation("Combat: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
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
				// The dying mob drops its target first (retail sends this LookAtIIR on 95% of captured kills, Stickienecro
				// idx 545, between the killing damage and the pose); then the pose freezes the corpse.
				await SendWorldPacket(killer, LookAtCodec.Build(LookAts.DropTarget(targetHandle)));
				await SendWorldPacket(killer, MoveTaskCodec.Build(DeathSequence.Pose(targetHandle, npcEntity.Position)));
				await SendWorldPacket(killer, BehaviorControlCenterCodec.Build(DeathSequence.Bcc(targetHandle, npcEntity.DeathBehaviorId, DeathSequence.NpcDeathVariant)));

				await AwardKillXp(killer, npcEntity.Level);
				await AnnounceKillChain(killer);

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
				await SendWorldPacket(killer, NewLevelCodec.Build(instance, lvl, deltas));

				// The combos this class unlocks AT this level, from the class<->combo ACCESS map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the old
				// class-independent level-keyed catalog (casters have no rows); it ships empty until populated from dumps,
				// so this is a no-op today.
				foreach (var combo in await _database.GetClassCombosUnlockedBetween(character.Class, lvl - 1, lvl))
					await SendWorldPacket(killer, ComboBookCodec.Build(instance, combo.Id));
			}

			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, character.Level, levelsGained))
				await SendWorldPacket(killer,
					FormatFeedbackCodec.Build(instance, new AttributeChanged(attributeId, amount)));

			var vitals = await ReseedFromMaxStats(killer);

			var block = vitals
				.ToVitalsBlock()
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);
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
			// like .learnspell / a create-flow starter. Combos are NOT in that set (#298) - they went out above, on the
			// combo book, which is the only place retail puts them.
			if (_learnSpell != null)
			{
				var learned = await _database.GetClassSpellsUnlockedBetween(character.Class, previousLevel, character.Level);
				foreach (var spellId in learned)
				{
					await _learnSpell(killer, spellId);
					await _database.AddCharacterSpell(character.Id, spellId);
				}

				await GrantCastabilityFor(character, learned);
			}
		}

		// What makes a just-learned spell CASTABLE (#298). Learning a spell puts it in the book, which is enough for
		// it to render and for the level to read right, but the client refuses the cast until the character carries
		// the FEAT that teaches it (its criteria evaluator's feat case, `YouCanNotUseYet`). Two grants, and they are
		// different things:
		//
		//   1. the TEACHING FEATS, read backwards out of FeatSpellGrants - exact, per spell, and available for all
		//      twelve classes because it comes from the client RDB rather than from a capture. It deliberately stops
		//      short of the class PROGRESSION tracks, which retail never puts in a feat list (see
		//      GetFeatsTeachingSpells);
		//   2. the class SKILL scaffold, which is the older stand-in and is kept because it is a different table
		//      (skills, not feats) and still covers whatever the feat grants do not.
		//
		// The scaffold alone only ever worked for the four classes with a high-level capture (Necromancer,
		// Demonologist, Assassin, Herald of Xotli) - `FullSkillIdsFor` returns null for the other eight, so their
		// level-up spells stayed gated. The feat grant is what closes that.
		//
		// Both take effect on the next world entry: feats ride the spawn scaffold's pre-IIR frame, so there is no
		// mid-session feat packet to send and none is invented here.
		//
		// The five classes that came out of the feat grant with ZERO coverage - Barbarian, Guardian, Conqueror,
		// Assassin, Ranger - were not a gap: 227 of their 259 level-gated catalog entries are COMBOS, which no feat
		// teaches and which never reach this method any more (see GetClassSpellsUnlockedBetween). What is left is an
		// ordinary catalog gap of the same kind the caster classes have, Conqueror's ~36 real spells being most of it.
		private async Task GrantCastabilityFor(Character character, IReadOnlyCollection<uint> learnedSpellIds)
		{
			var teachingFeats = await _database.GetFeatsTeachingSpells(learnedSpellIds);
			if (teachingFeats.Length > 0)
			{
				await _database.AddCharacterFeats(character.Id, teachingFeats);
				_logger.LogInformation("Level-up: trained {Count} feat(s) that teach {Spells} newly learned spell(s) for {Name}.", teachingFeats.Length, learnedSpellIds.Count, character.Name);
			}

			var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
			if (fullScaffold != null)
				await _database.EnsureCharacterSkills(character.Id, fullScaffold);
		}

		// The NPC's authoritative live vitals: the world-model entity's if it is a tracked NPC (also returned, so the
		// caller can run its death lifecycle), otherwise the per-session vitals (npcEntity null).
		// ⚠️ Returns NULL when the handle does not resolve to a live NPC in the world model, and the caller must treat
		// that as a REFUSAL. It used to conjure a per-session `EntityVitals` instead, and that silent fallback cost a
		// whole in-client session (#695): a target that had left the player's zone view - a patroller wandering off,
		// or a stationary NPC the player walked away from - took its damage on a phantom the world never sees, so the
		// real NPC's health never moved no matter how many times it was hit. `SendSpellDamageNumber` then read the
		// phantom's health back for the wire, so the floating NUMBER was wrong too. Reported as "only the first one
		// does real damage, all following only 1" and "the floating damage numbers are wrong". Nothing logged, nothing
		// threw, and the damage arithmetic was correct the whole time - it was just being spent on nothing.
		//
		// The conjured max was `CombatResolver.DefaultNpcMaxHealth`, i.e. a placeholder health bar for a creature that
		// already had a real one. That is exactly the shape the no-silent-fallback rule exists to stop: inventing
		// state to avoid reporting that a lookup failed.
		private EntityVitals? ResolveNpcVitals(INetworkClient client, uint targetHandle, out WorldEntity? npcEntity)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(targetHandle, out npcEntity) && npcEntity is { IsPlayer: false, Vitals: not null })
				return npcEntity.Vitals;

			npcEntity = null;
			_logger.LogError("Combat: no live NPC for target handle 0x{Handle:X8} in {Player}'s zone - refusing to apply damage. " +
				"The client is attacking something the server cannot find, which is a targeting/interest bug, not a combat one (#695).",
				targetHandle, client.Session.Character?.Name ?? "<no character>");

			return null;
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

			// Only ever reached from ApplyNpcDamage's onDamaged, which already resolved the same handle - so a null
			// here means the target vanished between the hit and its number, and a floating number read off nothing
			// is worse than none.
			return vitals == null
				? Task.CompletedTask
				: SendStatDamage(client, npcHandle, vitals.Health, -damage, SpellDamageStatId, casterHandle, spellId >> 8);
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
			await SendWorldPacket(client, EnergyCodec.Build(instance, player.Stamina));
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
			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, phase)));
		}

		// Sends the cast-interrupt abort BccAck (#347): the state=0x0007 reply to the client's cast-cancel that drops
		// the cast bar, cancels the animation and pops the "Interrupted" text client-side. Built from the stored cast
		// BEGIN args + the incoming cancel payload (behaviorId + tail). Phase = the begin's Mode.
		public static Task SendCastInterruptAbort(INetworkClient client, byte[] beginArgs, byte[] cancelPayload, uint phase)
		{
			var instance = client.Session.Character.Id;
			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.CastInterruptAbort(beginArgs, cancelPayload, instance, phase)));
		}

		// The abort for a cast the SERVER opened (rest, #505) - see BehaviorAcks.ServerCastInterruptAbort for why it
		// cannot share the splice above.
		public static Task SendServerCastInterruptAbort(INetworkClient client, byte[] beginArgs, byte[] cancelPayload, uint clientCommandId)
		{
			var instance = client.Session.Character.Id;
			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.ServerCastInterruptAbort(beginArgs, cancelPayload, instance, clientCommandId)));
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
			return SendWorldPacket(client, DamageResultCodec.Build(targetHandle, attackerHandle, damage, school));
		}

		// Acks an equip/unequip move (Interact 0x09) and appends the 0x201C bookkeeping entry retail sends with it:
		// prefix 0x65 + state = the destination equip slot for a move INTO a slot, prefix 0x68 + state 0 for a move
		// OUT of one (combat capture frames 289-290/320-321/375-376).
		public static async Task SendEquipMoveAck(INetworkClient client, byte[] rawPayload, uint mode, Item movedItem)
		{
			if (rawPayload.Length < 26)
				return;

			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EquipMoveAck(rawPayload, client.Session.Character.Id, mode, movedItem)));
		}

		// Sends the NPC counter-attack ANIMATION ack to the player it hit: a BehaviorControlCenterIIR with actor = the
		// NPC handle (the decoded "second BccAck", docs/subsystems/combat.md), so the client plays the NPC's swing + the player's
		// hit-react. Visual only - the authoritative HP move rides the world-tick StatUpdate, so the damage here is just
		// the floating combat number. The animation/result params are capture-typical (not semantically decoded); the
		// swingCounter increments per NPC swing to mirror the per-actor counter the capture carries.
		//
		// `direction` is the zone the mob swung from, and it MUST be the same draw the mitigation spent (#673): the
		// result record's type word IS that direction on the wire, so sending a constant while rolling an arc for the
		// damage told the client "overhead" on every swing regardless of what the player actually took. It is the
		// five-zone `SwingDirection` rather than the three-arc `ShieldArc` because retail's mobs use all five and the
		// arc is only the guard that covers it.
		//
		// `comboId` (#623) is the `combo_npc_generic_*` row this swing performs, 0 for a plain one. It rides the same
		// `combo_id` field a player's combo swing uses, which is what makes the client play the combo's animation and
		// name it in the combat log rather than showing a generic hit.
		public static async Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId = 0, bool ranged = false)
		{
			var playerHandle = client.Session.Character.Id;
			// args.f1 is the NormalAttack BEHAVIOR TypeId the client resolves to pick the swing animation - NOT an
			// ability id. The capture's beach mobs use ~0xBEEC (48876) across several NPCs (per-NPC jitter of a few);
			// 0xBEEC is the representative value. (The earlier 769094 was the Normal Attack ABILITY id - wrong field,
			// which played the player's hit-react + audio but left the NPC body un-animated.) A SHOT names its own
			// cluster, 77813-77817 (#676).
			var resultType = BehaviorAcks.SwingDirectionCode(direction);
			if (!ranged)
			{
				await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(npcHandle, playerHandle, damage, 0xBEEC, swingCounter, resultType, comboId: comboId)));
				return;
			}

			// #676: a shot is TWO records - begin then complete - carrying the same swing counter, the same combo and
			// a byte-identical tail. Sending only the begin left the client with a half-finished action; sending the
			// melee behaviour, which is what happened until now, played a sword swing for a bow.
			foreach (var sequence in new[] { BehaviorAnimations.RangedBeginSeq, BehaviorAnimations.RangedCompleteSeq })
			{
				var body = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(npcHandle, playerHandle, damage, BehaviorAnimations.NpcShotBehaviorType, swingCounter, sequence,
					BehaviorId.CombatRanged, RangedFlags, RangedRechargeTime, RangedActivationTime, resultType, comboId: comboId));
				await SendWorldPacket(client, body);
			}
		}

		// What a captured NPC shot carries in the three args melee sets differently: flags 24 (357 of 424), recharge
		// 83 and activation 81 (365 each) - the modal values, chosen the same way melee's 24/64/56 were.
		private const uint RangedFlags = 24;
		private const uint RangedRechargeTime = 83;
		private const uint RangedActivationTime = 81;

		// Sends a caster NPC's spell cast to the player it targets (#232): the ApplySpell (0x6B87E316) framed from the
		// NPC handle (applyFrom = npc, applyTo = player), built from the NPC's NpcSpell row by NpcSpellCastBuilder -
		// the construct-from-data counterpart of the melee SendNpcAttackAnimation. The HP move rides the caller's
		// PushVitals; this is the cast packet/effect.
		public static Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			var playerHandle = client.Session.Character.Id;
			var body = MagicBuffCodec.Build(playerHandle, npcHandle, spell.SpellId, spell.Payload);
			return SendWorldPacket(client, body);
		}

		// Retail's per-tick vitals regen (RegenIIR 0x28D0AC99, decoded): a compact [+hp][+mana][+stamina] delta the
		// client ADDS to the current bars, ×100 wire scale. Lets the bars regen smoothly without pushing a full
		// 0x96B8DC59 block every tick.
		public static Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return SendWorldPacket(client, RegenCodec.Build(handle, healthDelta, manaDelta, staminaDelta));
		}

		// A DoT tick's StatDamageIIR (the health effect of an active bleed/HoT). idx 1 is the typical effect slot in
		// the capture; statValue = the resulting HealthCurrent so the client's bar/number matches the vitals move. #274
		public static Task SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return SendWorldPacket(client, StatDamageCodec.Build(StatDamages.Combat(targetHandle, statValue, delta, (DamageSchool)type, casterHandle, 1, spellBaseId)));
		}

		// Pushes a 0x96B8DC59 stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}

		// Puts a bare 0x2000 record on the world conversation. Delegates to WorldIir so this file does not hold a
		// fourth private DispatchPassBlob sender - see WorldIir's header for why that matters.
		//
		// There WAS a second one here, SendWorldFeedback, which sent a FormatFeedbackIIR verbatim because that codec
		// used to prefix its own u32 length. #530 wave 2 moved the length onto the sender and this file was missed, so
		// the level-up attribute-gain feedback went out with no length and the client read the IIR type 0x8D5A8B6B as
		// its payload length - the same unframed-record disconnect as the loot bag in #576.
		private static Task SendWorldPacket(INetworkClient client, byte[] record)
		{
			return WorldIir.Send(client, record);
		}

		// #446: the on-screen kill CHAIN. Retail sends `ChainKillIIR` on the player's own handle carrying the running
		// count, and the whole grammar is measured off the corpus (`--report chain-kill`, 67 records):
		//   * counts are 2 (x32) and 3 (x3) and NEVER 1 - the first kill starts a chain silently;
		//   * every non-zero is closed by a 0 (32 of 32) about 9s later - the window, WorldEntity.ChainKillWindowSeconds;
		//   * escalations (2 -> 3) arrive inside that window, at 1.36-7.83s.
		// The closing 0 is the world tick's job (WorldSimulation.TickKillChains); this is the kill side.
		//
		// ⚠️ #446 paired this with stats 106 / 1000069 / 1000070 ("remaining of 20" and "achieved"). The corpus says
		// otherwise: **0 of the 67 arrivals coincide with a move of any of the three**. Those are the persisted daily
		// kill counters the client's stat-name table names; the chain is a live announcement and nothing else.
		private async Task AnnounceKillChain(INetworkClient killer)
		{
			var player = _sim.PlayerWorldEntity(killer);
			if (player?.RegisterKillInChain(DateTimeOffset.UtcNow) is not { } count)
				return;

			await SendWorldPacket(killer, ChainKillCodec.Build(player.Handle, count));
		}
	}
}
