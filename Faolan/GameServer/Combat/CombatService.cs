using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Progression;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Combat domain logic: resolves melee swings and spell casts into damage, drives the NPC kill lifecycle
	///     (despawn and scheduled respawn via the world model), tracks kill-objective quest progress, and builds the
	///     combat wire packets (the BehaviorControlCenter ack, DamageResult, Energy and the vitals stat block). A kill
	///     decides how much XP it is worth and hands it to <see cref="LevelUpService" />; the level curve and the ding
	///     burst live there.
	/// </summary>
	/// <remarks>
	///     Reads and writes the world model through <see cref="WorldSimulation" />, broadcasts world-visible effects
	///     through <see cref="WorldBroadcaster" />, and frames its own packets on the world conversation. Vitals resolve
	///     from the world model, which is their only owner: a handle that does not resolve there is refused and
	///     reported, never backed by invented per-session state (such a fallback silently absorbs every hit on a
	///     target outside the player's zone view).
	/// </remarks>
	public sealed class CombatService
	{
		/// <summary>
		///     The blast radius (world units) a ground-targeted AoE damages around its reticle position, for a spell the
		///     client states none for.
		/// </summary>
		/// <remarks>
		///     The per-spell radius is <c>Spell.AreaRadius</c> - the <c>Radius</c> key of the spell's area-targeting
		///     GameAction element - and 1,023 of the 20,848 spells carry one. This is the fallback for the rest (Deathly
		///     Blizzard among them: a stated channel interval but no stated area). 5 m is where the client's own
		///     distribution centres (5 m x201, 6 m x160, 4 m x86).
		/// </remarks>
		public const float GroundAoeRadiusUnits = 5f;

		// The channel tick interval (ms) for a channeled spell the client states none for. The per-spell value is
		// `Spell.ChannelingIntervalMs` (RDB field 447 `ChannelingInterval`, 516 spells); this is the fallback only.
		private const int GroundAoeTickIntervalMs = 1500;

		// The stat a spell-damage StatDamageIIR reports against (retail's outgoing spell hits use statId 0x0a in the
		// casting-combat capture). Distinct from the melee DamageResult path: the client renders a spell hit's number
		// from StatDamage (the same IIR that renders incoming DoT numbers on the player), not DamageResult.
		private const uint SpellDamageStatId = 0x0A;

		// Per-session store for entities not in the world model; model NPCs and players keep their vitals on their
		// WorldEntity, which is the single source of truth. In-memory, never persisted.
		private readonly CombatState _combat = new CombatState();

		private readonly FatalityService _fatality; // the fatality burst on a killing blow
		private readonly FactionService _factions; // Khitai reputation, awarded on a kill
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterRepository _characters;
		private readonly ICharacterStatRepository _stats;

		// Content:KillStreaks - whether a kill chain is tracked and announced at all. Read once at construction, not
		// per kill: it is a content switch for the server, not per-session state.
		private readonly bool _killStreaks;

		// A kill hands its XP here and the service owns everything downstream - the level curve, the ding burst, the
		// level-gated ability/spell unlocks and the persistence. One-directional: combat drives progression, never the
		// reverse.
		private readonly LevelUpService _levelUp;

		// The AA pool is the second thing a kill advances. A separate service for the same reason LevelUpService is
		// one - what a kill decides is the amount, and what the amount then does is not combat's business.
		private readonly AlternateAdvancementService _alternateAdvancement;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;

		private readonly WorldSimulation _sim;

		// The PvP gate and the match scoreboard both live on the CS tier (PvpMatchService owns the match), so a swing
		// at another player asks across this seam whether it is allowed before it lands - and tells it about the kill
		// afterwards. Never a direct reference to that listener: the relationship is mutual.
		private readonly ICsPlayerAgentDispatcher _csPlayerAgent;

		// World-visible records (a death, an animation) go to everyone who can see the subject, not just to the client
		// whose action caused them. See WorldBroadcaster.BroadcastEntityRecords for the measurement behind that.
		private readonly WorldBroadcaster _outbound;

		// The spell's own stated hit, so a cast's damage is the spell's rather than the level's. Same catalog the
		// self-heal path reads (`BuffService`) - a heal and a nuke are one mechanism with opposite signs.
		private readonly SpellPoolEffects _poolEffects;

		public CombatService(WorldSimulation sim, ICatalogRepository catalog, ICharacterRepository characters, ICharacterStatRepository stats, QuestProgressionService questProgression, ILogger logger,
			LevelUpService levelUp, AlternateAdvancementService alternateAdvancement, FatalityService fatality, FactionService factions, WorldBroadcaster outbound, ICsPlayerAgentDispatcher csPlayerAgent,
			SpellPoolEffects poolEffects, bool killStreaks)
		{
			_poolEffects = poolEffects;
			_csPlayerAgent = csPlayerAgent;
			_outbound = outbound;
			_killStreaks = killStreaks;
			_factions = factions;
			_fatality = fatality;
			_sim = sim;
			_catalog = catalog;
			_characters = characters;
			_stats = stats;
			_questProgression = questProgression;
			_logger = logger;
			_levelUp = levelUp;
			_alternateAdvancement = alternateAdvancement;
		}


		/// <summary>
		///     The damage the spell's own template states, in display units, or 0 where it states none.
		/// </summary>
		/// <remarks>
		///     This is what makes one spell differ from another: `Ice Strike (Rank 6)` states -16300..-18950 on
		///     health, which is the <i>163-189.5 points of Health</i> the client's own tooltip renders, while
		///     `Ice Strike (Rank 2)` states -4300..-5000. 2,149 shipped spells carry such a row. `SpellPoolEffects`
		///     resolves the level band and then the ramp inside it, so a level-80 caster gets the level-80 number.
		///     <para />
		///     Negative is damage - the template states a pool move, and the same table's positive rows are the heals
		///     `BuffService` applies to the caster. A spell whose health row is positive is a heal and deals no damage
		///     here, which is why the sign is filtered rather than taken as an absolute value.
		///     <para />
		///     The roll runs the stated range end to end, like a weapon's min..max in <c>ResolveMeleeDamage</c>.
		/// </remarks>
		private async Task<int> SpellStatedHit(uint spellId, uint casterLevel, int rollPercent)
		{
			return StatedHit(await _poolEffects.ForSpellBase(spellId, casterLevel), casterLevel, rollPercent);
		}

		/// <summary>
		///     The damage-half of a spell's stated pool effects, as a pure function of them - split from its caller so
		///     the rule is testable against the shipped catalog without a world, a client or a database.
		/// </summary>
		internal static int StatedHit(IReadOnlyList<SpellPoolEffects.PoolEffect> effects, uint casterLevel, int rollPercent)
		{
			var total = 0;
			foreach (var effect in effects)
			{
				// `max` only matters to a percent-of-pool effect, and a percent of the target's pool is not a number
				// this path can know before it has a target - so a percent row states nothing here and falls through
				// to the placeholder rather than resolving against 0.
				if (effect.Pool != CharacterStatId.HealthCurrent || effect.IsPercent)
					continue;

				var rolled = effect.Rolled(0, casterLevel, rollPercent);
				if (rolled < 0)
					total += -rolled;
			}

			return total;
		}

		/// <summary>
		///     Channeled ground AoE: ticks damage over the channel duration so the client sees a floating number each
		///     tick, as retail's Deathly Blizzard does over its ~10.5 s channel (casting-combat capture: one
		///     DamageResultIIR per target, ~1-2 s apart).
		/// </summary>
		/// <remarks>
		///     The spell's damage is spread across the ticks and the blast area is re-checked each tick. Stops early when
		///     the cast is interrupted (<c>ActiveCastToken</c> bumped by a move or the client's cancel) or the player
		///     disconnects. Fire-and-forget from cast begin.
		/// </remarks>
		public async Task ChannelGroundAoe(INetworkClient client, uint casterHandle, Vector3 target, uint spellId, int channelMs, uint castToken)
		{
			var casterLevel = client.Session.Character.Level;
			var total = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamageFor(spellId) ?? 0,
				_sim.PlayerWorldEntity(client)?.SpellBoost(spellId) ?? 0, await SpellStatedHit(spellId, casterLevel, Random.Shared.Next(101)));

			// The client states the cadence: RDB field 447 `ChannelingInterval`. Corroborated where the two sources
			// overlap - Deathly Blizzard's 2,500 ms, Waves of Flame's 1,000 and Resting's 500 are each exactly what
			// the captured StatDamage gaps measure independently (Spell.DotIntervalMs). Only a spell the client states
			// none for falls back to the constant.
			var spell = await _catalog.GetSpell(spellId);
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
					if (client.IsStopped)
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

		/// <summary>
		///     Reseeds the live entity's three pools (max and current) for a character whose max has just changed, and
		///     hands the vitals back so the caller can push them.
		/// </summary>
		/// <remarks>
		///     Every path that changes a max has to do this and then send <see cref="EntityVitals.ToVitalsBlock" />.
		///     Both halves matter: the live entity owns its own maxima, so skipping the reseed leaves the world tick
		///     pushing the stale max straight back; and the max ids only ever reach the client when something sends
		///     them, so skipping the push leaves the client's bar on whatever it was seeded with at world entry (a
		///     current pushed alone can render above the max).
		///     <para>
		///         It rebuilds through <see cref="EntityVitals.FromCharacter" /> with the same gear, feat and class base
		///         that world entry folds in, not from the persisted <see cref="CharacterMaxStats" /> alone. Reseeding
		///         from the bare persisted row drops every bonus the character is carrying, so a level-up would hand
		///         back a max below the one world entry computed.
		///     </para>
		/// </remarks>
		public async Task<EntityVitals> ReseedFromMaxStats(INetworkClient client)
		{
			var character = client.Session.Character;
			var statBonuses = await CharacterStatBonuses.For(_stats, character);
			var classBase = await _stats.GetClassAttributeBase(character.Class, character.Level);
			var seeded = EntityVitals.FromCharacter(character, statBonuses, classBase);
			var vitals = _sim.GetOrAddPlayerVitals(client);

			vitals.ReseedMaxFrom(seeded);
			return vitals;
		}

		/// <summary>
		///     The <c>.kill</c> GM command: kills an NPC target outright by applying its full remaining health as damage,
		///     so it runs the normal death lifecycle (vitals push, corpse and loot, XP, kill-quest advance). No-op if the
		///     target is not a live NPC in the killer's zone.
		/// </summary>
		public Task KillNpc(INetworkClient client, uint targetHandle)
		{
			var target = ResolveNpcVitals(client, targetHandle, out _);

			return target == null
				? Task.CompletedTask // not a live NPC in the killer's zone - the resolver already said so
				: ApplyNpcDamage(client, targetHandle, (int)target.Health, ".kill command");
		}

		/// <summary>
		///     Resolves an instant ground-targeted AoE - a single blast on completion. Empty ground is a no-op, as in
		///     retail. Channeled AoEs go through <see cref="ChannelGroundAoe" /> instead so they tick.
		/// </summary>
		public async Task ResolveGroundAoe(INetworkClient client, uint casterHandle, Vector3 target, uint spellId)
		{
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, _sim.PlayerWorldEntity(client)?.MagicDamageFor(spellId) ?? 0,
				_sim.PlayerWorldEntity(client)?.SpellBoost(spellId) ?? 0, await SpellStatedHit(spellId, casterLevel, Random.Shared.Next(101)));
			var count = await ApplyAoeTick(client, casterHandle, target, spellId, damage);
			_logger.LogInformation("Ground AoE spell 0x{Spell:X} at {Pos}: hit {Count} target(s) for {Damage} each.", spellId, target, count, damage);
		}

		/// <summary>
		///     Resolves an offensive cast: applies the damage to the target's live vitals (a StatUpdate moves the health
		///     bar) and shows the spell hit's floating number via StatDamage (see <see cref="SendSpellDamageNumber" />).
		///     The ApplySpell VFX is broadcast by the caller.
		/// </summary>
		/// <param name="extraPercentHundredths">
		///     A caller-supplied boost on top of the caster's own spell-line feat boost. The combo finisher passes the
		///     whole-combo damage its feats grant, which is a property of the combo rather than of the spell and so
		///     cannot be read off the caster here.
		/// </param>
		public async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId, int extraPercentHundredths = 0,
			SpellModifiers? modifiers = null)
		{
			var caster = _sim.PlayerWorldEntity(client);
			caster?.LastCombatAt = DateTimeOffset.UtcNow; // casting at a target -> in combat
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, caster?.MagicDamageFor(spellId) ?? 0, (caster?.SpellBoost(spellId) ?? 0) + extraPercentHundredths,
				await SpellStatedHit(spellId, casterLevel, Random.Shared.Next(101)));

			// A spell can crit: the client's own catalog states a per-spell chance and a per-spell damage bonus (RDB
			// 147/129) on ~1,225 records each, and the caster's Critical Rating adds to both on the melee curve. A spell
			// whose record states no chance does not crit; nothing is invented for the 20,278 that say nothing. Like a
			// melee crit this is not a distinct wire flag - retail conveys it by the larger number alone.
			var spell = await _catalog.GetSpell(spellId);

			// A trained feat or a live buff can move the spell's own crit numbers before the caster's rating is added.
			// `Spell Critical Chance` (147) and `Spell Critical Damage` (129) are the two commonest stats in the
			// spell-scoped modifier table (269 and 340 rows). Both are already in the column's own hundredths, which is
			// the unit `SpellModifiers.Apply` expects.
			var mods = modifiers ?? SpellModifiers.None;
			var spellCritChance = (uint)mods.Apply((int)(spell?.CriticalChanceHundredths ?? 0), CharacterStatId.SpellCriticalChance);
			var spellCritDamage = (uint)mods.Apply((int)(spell?.CriticalDamageHundredths ?? 0), CharacterStatId.SpellCriticalDamage);

			var critChanceHundredths = CombatResolver.SpellCritChanceHundredths(spellCritChance, caster?.CriticalRating ?? 0);
			if (critChanceHundredths > 0 && Random.Shared.Next(SpellCritRollScale) < critChanceHundredths)
				damage = CombatResolver.ApplySpellCritDamage(damage, spellCritDamage, caster?.CriticalDamageRating ?? 0);

			await ApplyDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}",
				() => SendSpellDamageNumber(client, targetHandle, casterHandle, damage, spellId));
		}

		/// <summary>
		///     The melee swing / auto-attack (Combat:Melee, BehaviorId 0x0F). Retail's response sequence (the strickenbla
		///     combat capture) is: a BehaviorControlCenterIIR acking the behavior (the client blocks on it - without it
		///     the swing stalls), then a DamageResultIIR hit, then an EnergyIIR stamina update.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The ack header echoes the request (instance, behaviorId, args) with the sequence incremented; its
		///         81-byte result tail is the capture's layout with the live actor=player and the computed damage in the
		///         damage slot (the v1/v3/v5 metadata constants are templated - only v4 == the swing damage is decoded).
		///     </para>
		///     <para>
		///         The result-block ack is the targeted-hit variant: its actor=player slot makes the client play a
		///         hit-react (the blood/impact animation) on whoever the block names - the player. An air swing (no
		///         target selected) must therefore get the plain echo-ack with no result block, exactly like the unarmed
		///         air swing, or the player visibly bleeds when swinging at nothing.
		///     </para>
		/// </remarks>
		/// <returns>
		///     Whether the swing landed, so the caller can gate what rides on it - the combo finisher in particular,
		///     which is a separate op in the inbound handler and must not deliver the combo's spell on a refused swing.
		/// </returns>
		public async Task<bool> SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle, uint mode = 0, ComboSwing? combo = null)
		{
			// rawPayload = [bodyLen u32][subop u32][c350 u32][instance u32][seq u16][behaviorId u32][argsLen u32][args][reqTail]
			if (rawPayload.Length < 26)
				return false;

			var instance = client.Session.Character.Id;

			// Retail's ack echoes the request args with a field3 "phase" inserted, and the phase value is the request's
			// own Mode (field 2; the 2026-06-12 armed+unarmed capture). Echo the Mode when the caller has it; fall back
			// to the session counter for callers that do not parse the args.
			var phase = mode != 0
				? mode
				: (uint)(++client.Session.NormalAttackAckPhase);

			// Only hit a real enemy: an attack with no target selected (or one that resolves to the player's own
			// handle) must not hit the player. An air swing gets the plain echo-ack (no result block) so the client
			// plays no hit-react on the player, matching the unarmed air-swing path - retail's air swing is the bare
			// ack and nothing more.
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget || !WithinMeleeReach(client, targetHandle))
			{
				await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, phase)));
				return false;
			}

			var level = client.Session.Character.Level;
			// Melee damage is driven by the equipped main-hand weapon (min/max from the RDB template), not a flat level
			// value, so a better weapon hits harder. Unarmed / no imported weapon damage falls back to the level curve.
			var weapon = await _stats.GetEquippedWeaponDamage(client.Session.Character.Id);
			var attackerEntity = _sim.PlayerWorldEntity(client);
			attackerEntity?.LastCombatAt = DateTimeOffset.UtcNow; // attacking -> in combat (halts HP/mana regen)
			// Roll between the weapon's min and max for per-swing variance; Combat Rating adds a flat bonus.
			var damage = (uint)CombatResolver.ResolveMeleeDamage(weapon?.Min, weapon?.Max, level, Random.Shared.Next(101), attackerEntity?.CombatRating ?? 0);

			// What the player's trained feats do to this combo attack. A combo's opening and finishing weapon attacks
			// each carry their own damage and critical-chance bonus (GameAction 53250 `ChangeInheritedWeaponAttack`) -
			// "Fierce Opening" is +30% on every combo's opener, "Brutal Blows" is +10% crit chance on Overreach's
			// finisher. A plain swing is not part of a combo and takes neither.
			var comboBonus = combo != null ? attackerEntity?.ComboBonus(combo.Combo.Id) ?? ComboFeatBonus.None : ComboFeatBonus.None;
			damage = (uint)CombatResolver.ApplyPercentBonus((int)damage, comboBonus.SwingDamagePercentHundredths(combo?.IsOpener == true, combo?.IsFinisher == true));

			// Roll a critical hit off the player's Critical Rating; a crit scales the damage by the base 1.5x plus their
			// Critical Damage Rating. A crit is not a distinct wire flag: across all captured DamageResults the `type`
			// field is the attacker's damage school (constant per attacker; the player's physical melee is always
			// type 3), the two trailing fields are always 0, and there is no crit indicator. Retail conveys a crit
			// purely by the larger number, so the player's melee type (3) is sent either way.
			var criticalRating = (uint)Math.Max(0, (attackerEntity?.CriticalRating ?? 0) + comboBonus.CriticalRating(combo?.IsOpener == true, combo?.IsFinisher == true));
			var crit = CombatResolver.IsCriticalHit(criticalRating, Random.Shared.Next(100));
			if (crit)
				damage = (uint)CombatResolver.ApplyCritDamage((int)damage, attackerEntity?.CriticalDamageRating ?? 0);

			// Directional combat: the swing comes from one of the client's five zones, and the defender's shield rose
			// says how many of its three guards cover the arc that zone belongs to. An unguarded arc takes 20% more, a
			// triple-stacked one 66% less - the client's own Combat_PvE{0..3}ShieldBonus table. The defender's rose is
			// read off whatever the handle resolves to, NPC or player alike (a player's is set by the client and
			// relayed by HandlePlayerShieldRose), and a player defender switches the table to the PvP column, which is
			// the gentler one (-0.15/-0.30 where PvE mitigates -0.33/-0.66).
			//
			// A swing is always directed. The direction ability in the request tail is only how a plain swing states
			// it: word 5 is absent on 107 of the 515 captured player swings, and every one of those result records
			// still carries a direction code (0xCA x58, 0xCB x22, 0xC9 x8). For a combo step the direction is the key
			// the player pressed to reach it, which the catalog states - `ComboSwing.Direction`, off `Combo.Actions`.
			//
			// The fallback is only for a combo with no directional grammar at all (a reactive/magic marker, a `Thrust`
			// step). One draw is spent on both the mitigation and the ack, so the wire and the damage agree on every
			// swing.
			var swingDirection = CombatShieldRose.DirectionFor(BehaviorTailScan.AbilityId(BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Tail))
			                     ?? combo?.Direction ?? SwingDirection.Overhead;
			var swingArc = CombatShieldRose.ArcOf(swingDirection);
			WorldEntity? defenderEntity = null;
			if (attackerEntity != null && _sim.World.TryGetZone(attackerEntity.MapId, out var attackerZone) && attackerZone.TryGet(targetHandle, out var defender))
				defenderEntity = defender;

			damage = CombatShieldRose.ApplyArcMitigation(damage, defenderEntity?.ShieldDistribution ?? 0, swingArc, defenderEntity?.IsPlayer == true);
			const DamageSchool school = DamageSchool.Crushing; // the player's physical-melee damage school (capture-derived)

			// 1) BehaviorControlCenterIIR ack - releases the swing. The record (header, echoed args, 81-byte result
			// block naming the victim) is BehaviorAcks.HitResultAck's.
			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.HitResultAck(rawPayload, instance, phase, targetHandle, damage, swingDirection)));

			// 2) DamageResultIIR - the hit / floating number (crits are just a bigger number, same school).
			await SendDamageNumber(client, targetHandle, instance, damage, school);

			// Apply to the target's live vitals (world-authoritative), push its health bar, and run the kill
			// lifecycle (despawn + scheduled respawn) on death.
			await ApplyDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target.
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = _sim.GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, EnergyCodec.Build(instance, player.Stamina));

			return true;
		}

		// A spell crit rolls in hundredths of a percent, not whole percents like a swing: the modal per-spell chance
		// is 2.50%, which a 0..99 roll cannot express.
		private const int SpellCritRollScale = 10000;

		/// <summary>
		///     The unarmed swing (Interact action 0x11, empty main hand; behavior TypeIds 1089xx, ability id in the
		///     tail). Retail's complete reply to an air swing is the plain echo-ack with no result block (combat capture
		///     2026-06-12).
		/// </summary>
		/// <remarks>
		///     No capture holds a targeted unarmed swing (the captured swings hit air), so the damage resolution reuses
		///     the armed path; that half is unverified against retail.
		/// </remarks>
		public async Task SendUnarmedAttackResponse(INetworkClient client, byte[] rawPayload, uint mode, uint targetHandle)
		{
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Character.Id;
			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, mode)));

			// An unarmed swing reaches no further than an armed one. Unlike the armed path the echo-ack has already
			// gone out above (retail's whole reply to an unarmed air swing is that ack), so an out-of-reach swing needs
			// nothing further sent - it is already indistinguishable from an air swing.
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget || !WithinMeleeReach(client, targetHandle))
				return;

			// The unarmed swing's ability id is the last word of the request's behaviour tail - an opaque region at the
			// codec, read by name through BehaviorTailScan rather than by offset here.
			var abilityId = BehaviorTailScan.AbilityId(BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Tail);
			var damage = (uint)CombatResolver.ResolveSpellDamage(abilityId, client.Session.Character.Level);
			await SendSwingDamage(client, instance, targetHandle, damage);
		}

		/// <summary>
		///     Spends the caster's mana for a cast and pushes the caster's own StatUpdate so the mana bar drops - the
		///     spell/mana analogue of the melee swing's stamina cost. Mana is not carried by EnergyIIR (stamina-only in
		///     the captures); it rides the vitals stat block. The cost is the spell's own RDB percentage of the pool.
		///     No-op if the player has no mana pool, or if the spell costs nothing.
		/// </summary>
		public async Task SpendCasterMana(INetworkClient client, uint spellId, SpellModifiers? modifiers = null)
		{
			var player = _sim.GetOrAddPlayerVitals(client);
			if (player.MaxMana == 0)
				return;

			var spell = await _catalog.GetSpell(spellId);

			// A trained feat or a live buff can make the spell cheaper - "Mana Cost" is the stat these records aim at
			// most (217 of its 228 shipped rows are relative, 11 absolute). `ManaCost` stores the property-run field
			// verbatim, so an absolute modifier on it needs no conversion. Applied to the raw cost before the
			// percent-of-pool resolve, so a spell whose cost is itself a percentage is scaled rather than
			// double-converted.
			var raw = (ushort)(modifiers ?? SpellModifiers.None).Apply(spell?.ManaCost ?? 0, CharacterStatId.ManaCost);
			var cost = CombatResolver.ResolveSpellManaCost(raw, player.MaxMana, spell?.ManaCostIsPercent ?? false);
			if (cost == 0)
				return;

			player.ApplyManaDelta(-(int)cost);
			await SendStatUpdate(client, player.ToCurrentsBlock(), client.Session.Character.Id); // a cast moves a pool: currents-only form
		}

		/// <summary>
		///     Spends the stamina a completed melee combo costs and pushes the player's own vitals so the bar drops - the
		///     combo twin of <see cref="SpendCasterMana" />. The cost comes from ComboService (the client's own formula);
		///     this half is only the deduction plus the push, which is why it takes a number rather than a combo. No-op
		///     for a free combo or a player with no stamina pool.
		/// </summary>
		public async Task SpendComboStamina(INetworkClient client, uint cost)
		{
			if (cost == 0)
				return;

			var player = _sim.GetOrAddPlayerVitals(client);
			if (player.MaxStamina == 0)
				return;

			player.Stamina = cost >= player.Stamina ? 0 : player.Stamina - cost;
			await SendStatUpdate(client, player.ToCurrentsBlock(), client.Session.Character.Id);
		}

		/// <summary>
		///     Applies the damage half of one of a spell's area effects - "apply spell X to up to N entities within R
		///     metres of the caster". Only hostile, living NPCs are hit; friendly and neutral bystanders are spared, the
		///     same rule melee splash follows.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The two shapes share this entirely and differ only in who calls it: an instant area
		///         (<c>SpellAreaEffect.PeriodCentiseconds == 0</c>) is applied once, by the cast path; an aura
		///         (period > 0) is applied by the world tick, every period, for as long as the buff that owns it is up
		///         (`Gangrenous Stench` is a 25 s self-buff whose aura hits a 7 m radius every 4 s).
		///     </para>
		///     <para>
		///         The damage is the effect spell's, not the castable's: 221070 is the thing you press (cast time, mana,
		///         recast) and 221069 is the thing that lands. Resolving the castable here would apply the wrong
		///         record's numbers - and for a pure carrier like `Life Leech`, no numbers at all.
		///     </para>
		///     <para>
		///         Centred on the source rather than a reticle, which is what "Hostile Self Centered Area Spell" means
		///         in the client's own targeting-mode line; a ground-targeted blast is <see cref="ResolveGroundAoe" />
		///         and keeps its reticle.
		///     </para>
		///     <para>
		///         Landing the effect itself on each victim - the icon, the DoT, its own areas - is
		///         <c>WorldSimulation.ApplyAreaEffect</c>, which is why this answers who was hit rather than how many.
		///         That split keeps the buff registry with the world model, which owns it.
		///     </para>
		/// </remarks>
		public async Task<IReadOnlyList<uint>> ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || radius <= 0)
				return [];

			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(effectSpellId, casterLevel, player.MagicDamageFor(effectSpellId), player.SpellBoost(effectSpellId),
				await SpellStatedHit(effectSpellId, casterLevel, Random.Shared.Next(101)));

			// The area radiates from whoever holds it, and that is not always the player: a chained effect re-emits an
			// area from the infected mob (Gangrenous Stench's 221069 spreads 221068 to everything within 4 m of the mob
			// it is ticking on), and centring that on the caster would spray it from the wrong point.
			//
			// A source that does not resolve in the player's own zone reaches nobody rather than falling back to the
			// player: an emitter in another instance is not a reason to blast the area out of the player instead. The
			// player themselves is the one exception, since a caster is not always in the zone's entity table.
			var zone = _sim.World.ZoneForEntity(player);
			var source = zone.TryGet(sourceHandle, out var holder)
				? holder
				: sourceHandle == player.Handle
					? player
					: null;

			if (source == null)
				return [];

			// The cap the client renders as "up to N targets" (SpellAreaEffect.MaxTargets). 0 = the element stated
			// none, so everything in range is hit - not "nothing", which is what treating 0 as the cap would mean.
			// Same gate as melee splash and the ground blast: `Hostile`, not merely "not friendly", or an aura sweeps
			// up neutral bystanders (yellow-name civilians) that a sword swing beside them would spare.
			var inRange = zone
				.EntitiesInRange(source.Position, radius, sourceHandle)
				.Where(e => e is { IsPlayer: false, Hostile: true, IsDead: false })
				.Select(e => e.Handle);

			var victims = (maxTargets > 0 ? inRange.Take((int)maxTargets) : inRange).ToList();

			foreach (var handle in victims)
			{
				await ApplyDamage(client, handle, damage, $"area effect {effectSpellId} from 0x{sourceHandle:X8}");
				await SendSpellDamageNumber(client, handle, sourceHandle, damage, effectSpellId);
			}

			return victims;
		}

		/// <summary>
		///     <c>/inflict &lt;damage&gt;</c>, retail's own direct-damage lever ("inflicts damage of the specified damage
		///     type on your target"). Routed through the same <see cref="ApplyDamage" /> a cast or a DoT uses, so a GM
		///     blow that finishes the mob runs the whole kill lifecycle - XP, corpse and loot, kill-quest credit - rather
		///     than leaving a 0 hp entity standing. Works on a player target too, via the player branch.
		/// </summary>
		public async Task ApplyGmDamage(INetworkClient client, uint targetHandle, int damage, uint casterHandle)
		{
			if (damage <= 0)
				return;

			await ApplyDamage(client, targetHandle, damage, $"GM /inflict from 0x{casterHandle:X8}",
				() => SendDamageNumber(client, targetHandle, casterHandle, (uint)damage));
		}

		/// <summary>
		///     One DoT tick against a non-player entity - the bleed a player's debuff leaves on a mob. Runs through the
		///     same damage funnel a cast does, so a tick that finishes the mob runs the whole kill lifecycle (XP,
		///     corpse/loot, kill-quest credit) and credits the player who applied the debuff.
		/// </summary>
		/// <remarks>
		///     A positive delta (a heal-over-time) is a no-op here: NPC pools have no heal path, and re-using the damage
		///     one with a flipped sign would resurrect a corpse mid-lifecycle.
		/// </remarks>
		public async Task ApplyNpcDotTick(INetworkClient client, uint npcHandle, uint casterHandle, int delta, uint spellId)
		{
			if (delta >= 0)
				return;

			await ApplyDamage(client, npcHandle, -delta, $"DoT from spell {spellId} (0x{casterHandle:X8})",
				() => SendSpellDamageNumber(client, npcHandle, casterHandle, -delta, spellId));
		}

		private async Task<int> ApplyAoeTick(INetworkClient client, uint casterHandle, Vector3 target, uint spellId, int tickDamage)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return 0;

			// Per-spell where the client states one (Spell.AreaRadius); GroundAoeRadiusUnits otherwise.
			var spell = await _catalog.GetSpell(spellId);
			var radius = spell?.AreaRadius > 0
				? spell.AreaRadius
				: GroundAoeRadiusUnits;

			// The same gate melee splash uses (CombatTargeting.CleaveTargets) - "vendors standing nearby must never
			// take splash" applies no less to a blast than to a sword. `Hostile` rather than "not friendly": neutral
			// (disposition 2) covers 386 templates, 279 of them yellow-name civilians, and a blast dropped in a town
			// centre must spare them as a swing does. `IsDead` keeps corpses from taking ticks.
			var victims = _sim
				.World.ZoneForEntity(player)
				.EntitiesInRange(target, radius, casterHandle)
				.Where(e => e is { IsPlayer: false, Hostile: true, IsDead: false })
				.Select(e => e.Handle)
				.ToList();

			foreach (var handle in victims)
			{
				await ApplyDamage(client, handle, tickDamage, $"AoE spell {spellId} from 0x{casterHandle:X8} at {target}");
				await SendSpellDamageNumber(client, handle, casterHandle, tickDamage, spellId);
			}

			return victims.Count;
		}

		/// <summary>
		///     Whether the swing can reach its named target. The target handle is the client's and nothing else bounds
		///     it: `CombatTargeting` exempts the primary from the cleave cone on purpose (you face what you selected, so
		///     cone-testing it would reject a legitimate swing), so the distance is checked here.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Melee is the only range the server has to own. The client checks a spell's range itself and never
		///         sends an out-of-range cast (confirmed in-client: spells are checked client side, melee is always
		///         sent), so a server-side spell gate would guard a door the client already holds shut.
		///     </para>
		///     <para>
		///         An out-of-reach swing is answered as an air swing - the same plain echo-ack a swing with no target
		///         gets - rather than dropped: that shape is captured and the client handles it, whereas silently
		///         refusing an action the client thinks it made leaves it waiting on a reply.
		///     </para>
		///     <para>
		///         The reach is <see cref="CombatTargeting.SwingReach" />, which is provisional - see it for why there
		///         is no shipped number to use instead. The geometry and the zone resolution live in
		///         <see cref="CombatTargeting.ResolveSwingReach" />, which is pure and tested; this is only the session
		///         half. The refusal is logged at Information on purpose: the file log of a play session is where the
		///         evidence for a non-biting gate has to appear.
		///     </para>
		/// </remarks>
		private bool WithinMeleeReach(INetworkClient client, uint targetHandle)
		{
			var attacker = _sim.PlayerWorldEntity(client);
			var name = client.Session.Character?.Name ?? "<no character>";
			if (attacker == null)
			{
				// Not in the world model at all (create-editor connection, or a swing racing world entry). Nothing to
				// measure from, and nothing downstream could apply the hit either.
				_logger.LogWarning("Combat: {Player} swung at 0x{Target:X8} with no live world entity - refusing the swing (#696).", name, targetHandle);

				return false;
			}

			switch (CombatTargeting.ResolveSwingReach(_sim.World, attacker, targetHandle, out var distance))
			{
				case MeleeReach.InReach:
				{
					return true;
				}

				case MeleeReach.TooFar:
				{
					_logger.LogInformation("Combat: {Player}'s swing at 0x{Target:X8} misses - {Distance:F1}u away, reach is {Reach}u (#696).",
						name, targetHandle, distance, CombatTargeting.SwingReach);

					return false;
				}

				default:
				{
					_logger.LogWarning("Combat: {Player} swung at 0x{Target:X8}, which is not in their zone - refusing the swing. " +
					                   "The client is attacking something the server cannot place, which is a targeting/interest bug, not a combat one (#695/#696).", name, targetHandle);

					return false;
				}
			}
		}

		/// <summary>
		///     Melee cleave: an AoC swing damages "a small area in front of you" (Funcom's own quickstart), not just the
		///     selected target and not a ball around it. After the primary hit, the same damage splashes onto the
		///     hostile, living NPCs inside the attacker's frontal cone (<see cref="CombatTargeting.CleaveTargets" />).
		///     The direction picks which of the defender's guards you meet; the cone picks who you meet. Each secondary
		///     gets its own DamageResultIIR floating number and runs the full damage/kill lifecycle.
		/// </summary>
		/// <remarks>
		///     Same damage as the primary, deliberately: the quickstart says the primary takes more, but the only shipped
		///     number for it is `Combat_{PvE,PvP}DamageReductionMainOnSplash` = 1 - which the client registers and never
		///     reads (TweakGlobals: reads=0), and whose value under any non-absurd reading is a no-op. Inventing a
		///     fraction the balance sheet does not carry would be worse than matching it.
		/// </remarks>
		private async Task ApplyMeleeCleave(INetworkClient client, uint instance, uint primaryHandle, uint damage)
		{
			// ZoneForEntity, not TryGetZone(MapId): the latter resolves the default instance only and cleave would do
			// nothing in an instanced playfield.
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			zone.TryGet(primaryHandle, out var primary);
			foreach (var secondary in CombatTargeting.CleaveTargets(zone, player, primary, CombatTargeting.DefaultCleaveReach,
				         CombatTargeting.DefaultCleaveHalfAngleDegrees, CombatTargeting.DefaultMaxCleaveTargets))
			{
				await SendDamageNumber(client, secondary.Handle, instance, damage, DamageSchool.Crushing);
				await ApplyNpcDamage(client, secondary.Handle, (int)damage, $"melee cleave from 0x{instance:X8}");
			}
		}

		/// <summary>
		///     The damage funnel. A target handle is an NPC or another player, and only the second needs a permission
		///     question, so the split is here rather than repeated in the callers (melee, cleave, spell, DoT tick, ground
		///     AoE, area effect, <c>.kill</c>).
		/// </summary>
		/// <remarks>
		///     A player target that is not a legal PvP opponent is refused here, with its own log line. Letting it fall
		///     through to <see cref="ApplyNpcDamage" /> would refuse it too - the NPC resolver rejects a player entity -
		///     but would report a targeting/interest bug for what is really a rules answer.
		/// </remarks>
		/// <param name="onDamaged">
		///     The caller's own damage record (the floating number) - the spell path's StatDamage. It runs between the
		///     fatality roll and the vitals/death records because that is where retail puts it: in Stickienecro's
		///     ice-strike fatality the victim's StatDamage is idx 542, after the burst (535-538) and before the death
		///     pose (546) and BccIIR (549).
		/// </param>
		private async Task ApplyDamage(INetworkClient client, uint targetHandle, int damage, string cause, Func<Task>? onDamaged = null)
		{
			if (PlayerTarget(client, targetHandle) is not { } victim)
			{
				await ApplyNpcDamage(client, targetHandle, damage, cause, onDamaged);
				return;
			}

			if (!_csPlayerAgent.ArePvpMatchOpponents(client.Session.Character.Id, victim.Handle))
			{
				_logger.LogInformation("Combat: {Player}'s hit on player '{Victim}' is refused - PvP is only allowed between opponents of one started minigame match (#146).",
					client.Session.Character?.Name ?? "<no character>", victim.Name);

				return;
			}

			await ApplyPlayerDamage(client, victim, damage, cause, onDamaged);
		}

		// The target as a live player in the attacker's own zone, or null when it is not one. Resolved through
		// ZoneForEntity like every other target lookup here - TryGetZone(MapId) would answer the default instance and
		// so miss every minigame match, which is the one place this is reached from.
		private WorldEntity? PlayerTarget(INetworkClient client, uint targetHandle)
		{
			var attacker = _sim.PlayerWorldEntity(client);
			return attacker != null && _sim.World.ZoneForEntity(attacker).TryGet(targetHandle, out var target) && target is { IsPlayer: true }
				? target
				: null;
		}

		// Player-versus-player damage. The shape mirrors the NPC-hits-player path rather than the NPC-damage one,
		// because the victim is a live session with a health bar of its own: they get the incoming floating number and
		// their own vitals push, while the attacker gets the target's bar move (its outgoing number was already sent
		// by the caller, like every other hit).
		//
		// The death is not finished here on purpose. A dead player's release screen, buff strip and death penalty are
		// the world tick's (TickPlayerDeaths), which fires off `vitals.IsDead` whatever emptied the bar - so a PvP kill
		// takes exactly the same route as a mob kill, a fall or lava, and there is no second death sequence to keep in
		// step. What is this path's own is the scoreboard: the career counters and the match's kill credit.
		private async Task ApplyPlayerDamage(INetworkClient attackerClient, WorldEntity victim, int damage, string cause, Func<Task>? onDamaged)
		{
			var victimClient = victim.Client;
			var victimCharacter = victimClient?.Session.Character;
			if (victimClient == null || victimCharacter == null)
				return; // not connected to this listener - nothing to move and nobody to tell

			var vitals = victim.Vitals ??= EntityVitals.FromCharacter(victimCharacter, victim.GearBonuses, victim.ClassBase);
			if (vitals.IsDead)
				return; // already down; ignore overkill until they release

			if (victim.GodMode)
				return; // .godmode: the swing lands and animates, the damage does not

			var attacker = _sim.PlayerWorldEntity(attackerClient);
			var attackerCharacter = attackerClient.Session.Character;

			// Attribute the death to the killer, never to the victim: the client's ResurrectView goes into
			// wave-respawn mode iff the killer resolves to the local player, and a PvP death must release instantly
			// like every other.
			victim.LastAttackerHandle = attacker?.Handle ?? victim.LastAttackerHandle;
			victim.LastCombatAt = DateTimeOffset.UtcNow;
			vitals.ApplyHealthDelta(-damage);

			if (onDamaged != null)
				await onDamaged();

			// The victim's screen: the incoming number floating on them, then their own health bar. The second is
			// exactly what IWorldOutbound.PushVitals does (a currents-only block addressed at their own handle) - it
			// is spelled out here rather than reached through the listener because this service frames its own combat
			// packets and does not hold that port.
			await SendDamageNumber(victimClient, victim.Handle, attacker?.Handle ?? 0, (uint)Math.Max(0, damage), DamageSchool.Crushing);
			await SendStatUpdate(victimClient, vitals.ToCurrentsBlock(), victim.Handle);

			// And the attacker's: the target's bar moving, the same push a hit on an NPC sends them.
			await SendStatUpdate(attackerClient, vitals.ToCurrentsBlock(), victim.Handle);

			if (!vitals.IsDead)
				return;

			_logger.LogInformation("Combat: {Killer} killed player {Victim} ({Cause}).", attackerCharacter.Name, victimCharacter.Name, cause);

			// The PvP career pair the login snapshot carries as stats 0x290 / 0x292. Persisted per event for the same
			// reason as the PvE pair: a kill is earned once.
			attackerCharacter.PvpKills++;
			victimCharacter.PvpDeaths++;
			await _characters.SetCharacterPvpKillCounts(attackerCharacter.Id, attackerCharacter.PvpKills, attackerCharacter.PvpDeaths);
			await _characters.SetCharacterPvpKillCounts(victimCharacter.Id, victimCharacter.PvpKills, victimCharacter.PvpDeaths);

			// And the match's own scoreboard, which may end it on the score limit.
			await _csPlayerAgent.CreditPvpMatchKill(attackerCharacter.Id, victimCharacter.Id);
		}

		private async Task ApplyNpcDamage(INetworkClient client, uint targetHandle, int damage, string cause, Func<Task>? onDamaged = null)
		{
			var target = ResolveNpcVitals(client, targetHandle, out var npcEntity);
			if (target == null)
				return; // unresolvable target - already reported by the resolver

			if (target.IsDead)
				return; // already dead this life; ignore overkill until it respawns

			// Being hit puts the mob in combat with whoever hit it. The world tick reads both: the handle is the aggro
			// source proximity cannot give it (a caster/archer standing outside NpcAggroRange), and the timestamp is
			// what stops the anti-whittle full-heal from resetting a mob mid-fight.
			if (npcEntity != null)
			{
				npcEntity.LastAttackerHandle = _sim.PlayerWorldEntity(client)?.Handle ?? npcEntity.LastAttackerHandle;
				npcEntity.LastCombatAt = DateTimeOffset.UtcNow;
			}

			target.ApplyHealthDelta(-damage);

			// A lethal blow's fatality burst goes out before the client is told the target is dead - not merely before
			// the death records. Retail's kill window never carries a health-to-zero update ahead of the burst: in
			// Stickienecro's ice-strike fatality the victim's only damage record is the StatDamage at idx 542, after
			// the three ApplySpells at 535-538, and the death pose (546) and death BccIIR (549) follow that. A client
			// told the mob is dead first has already chosen how it dies before it learns the kill was a fatality.
			if (target.IsDead && _fatality != null && npcEntity != null)
				await _fatality.OnKill(client, _sim.PlayerWorldEntity(client), npcEntity);

			if (onDamaged != null)
				await onDamaged();

			// A hit moves a pool, so retail's currents-only form is what goes out here.
			await SendStatUpdate(client, target.ToCurrentsBlock(), targetHandle);

			if (target.IsDead)
				await OnNpcKilled(client, targetHandle, npcEntity!, cause);
		}

		// A kill's share of the killer's advancement: the XP the creature is worth, handed to the progression service
		// (which owns the level curve, the ding burst and the persistence), plus the PvE kill counter, which is this
		// path's alone. What a kill decides is the amount - `CombatResolver.ResolveKillXp(npcLevel)`; what that amount
		// then does to the character is not combat's business.
		private async Task AwardKillXp(INetworkClient killer, WorldEntity npc)
		{
			var character = killer.Session.Character;

			// The sheet's kill counter. Persisted here rather than at autosave because a kill is earned once, and a
			// crash between the kill and the save would lose it - the same reason the XP is. This is the NPC counter,
			// not the PvP one: the slot the snapshot sends as `PvPTotalKills` (0x290) is a PvP career total.
			// `AwardKillXp` is reached only for a creature kill - it takes the NPC's level - so every increment here
			// is PvE by construction.
			character.NpcKills++;
			await _characters.SetCharacterNpcKillCounts(character.Id, character.NpcKills, character.NpcDeaths);

			await _levelUp.ApplyXp(killer, CombatResolver.ResolveKillXp(npc.Level));

			// The alternate-advancement pool is the other thing a kill feeds, and it is the one that keeps filling once
			// the level bar cannot - retail pays AA xp only at the level cap. The service decides whether this kill
			// pays at all; combat's part is that the killing blow is what attributes it.
			await _alternateAdvancement.AwardKillXp(killer, npc.Level, npc.NpcType, npc.NpcFlags);
		}

		// An NPC was killed by `killer`: a model NPC is marked dead (starting its respawn timer), despawned for nearby
		// players (the tick respawns it), and awards the killer XP. A non-model NPC (no world entity) is just dropped
		// from the session store.
		private async Task OnNpcKilled(INetworkClient killer, uint targetHandle, WorldEntity npcEntity, string cause)
		{
			if (npcEntity != null)
			{
				// MarkDead starts the corpse + respawn timers. The killing blow already pushed health=0 vitals
				// (ApplyNpcDamage), so the client plays the death animation on the still-rendered creature; the world
				// tick then drops its loot bag beside it (NpcCorpseLootDropped), fades the body out half a minute later
				// and respawns the creature much later still. No despawn here: that would pop the corpse before the
				// death animation can play.
				npcEntity.MarkDead();
				// Record the killer so the corpse's loot bag is owned by them (the allowed looter), not whoever is in
				// range - retail's NewAllowedLooterIIR is a loot-permission grant.
				npcEntity.LastAttackerHandle = killer.Session.Character.Id;

				// The fatality burst is not sent here. It goes out in ApplyNpcDamage, ahead of the killing blow's own
				// vitals push: retail's ordering is burst -> the victim's damage -> death pose -> death BccIIR, in 37 of
				// 37 captured fatalities, and the client cannot pick a finisher for a corpse it was already told is
				// dead.

				// The health=0 StatUpdate alone does not play the death animation. Retail drives an NPC death with a
				// death-pose MoveTask (type 0x06, freezes the corpse in place) + a death-behavior BccIIR (behaviorId
				// 41877) on the dying handle - every NPC kill in NecroL80_..._casting-combat carries exactly this pair,
				// and no SimpleCharKilledBy (that record is player-death-only, driving the local release/resurrect
				// UI). The dying mob drops its target first (retail sends this LookAtIIR on 95% of captured kills,
				// Stickienecro idx 545, between the killing damage and the pose); then the pose freezes the corpse.
				//
				// Flush what it walked but never had broadcast before freezing it. The tick only emits a move once the
				// creature has covered 6u since the last one, so a mob killed mid-chase - which is what killing one at
				// range means - is carrying up to 6u the client has not seen. The pose states the true death position,
				// so without the flush the corpse jumps that distance onto its killer and only then plays the death
				// animation. Retail keeps the two in agreement: 250 of 267 captured death poses sit at distance 0.00
				// from the end of that handle's preceding MoveTask.
				await _sim.FlushPendingMove(npcEntity);

				// Broadcast, not sent to the killer alone. A death is world-visible: everyone watching the creature
				// must see it drop its target, freeze in its death pose and play its death animation, or it stands
				// there alive-looking for every other player until it fades. Measured in the two-client capture - every
				// subject of `LookAtIIR` (8/8), `MoveTaskIIR` (12/12, the mode-6 poses among them) and
				// `BehaviorControlCenterIIR` (11/11) reaches both clients. See BroadcastEntityRecords.
				//
				// The BCC's field3 is the dying NPC's own behaviour counter, not a "death variant" - so its death takes
				// the next index after its last swing, which is exactly the 5 -> 6 -> 6(abort) -> 7 progression one
				// captured NPC's records walk. `AttackSwingCounter` is the same counter its swing acks number
				// themselves with, so death simply continues it.
				await _outbound.BroadcastEntityRecords(npcEntity,
					LookAtCodec.Build(LookAts.DropTarget(targetHandle)),
					MoveTaskCodec.Build(DeathSequence.Pose(targetHandle, npcEntity.Position)),
					BehaviorControlCenterCodec.Build(DeathSequence.Bcc(targetHandle, npcEntity.DeathBehaviorId, ++npcEntity.AttackSwingCounter, npcEntity.DeathActivationTime)));

				await AwardKillXp(killer, npcEntity);
				// Khitai reputation. A no-op for every base-game NPC - the faction ids are Khitai's and a creature with
				// no `NpcStatCore.FactionId` moves nothing.
				await _factions.AwardForKill(killer, npcEntity.FactionId);
				await AnnounceKillChain(killer);

				_logger.LogInformation("Combat: NPC {Name} 0x{Target:X8} killed ({Cause}); loot drops now, corpse fades in {Fade}s, {Fate}.", npcEntity.Name, targetHandle, cause,
					WorldEntity.CorpseFadeSeconds, npcEntity.Route.NoRespawn
						? "then it leaves the world (no respawn)"
						: $"respawns in {npcEntity.Route.RespawnDelaySeconds}s");
				// A kill advances/completes any matching kill goal. A chained stage (e.g. Casilda's 13479) completes and
				// supersedes to the next stage; a chainless kill quest keeps the turn-in path. Matched by the stable
				// target template id (the goal's TrailingRefId), name as fallback.
				await _questProgression.AdvanceKillGoals(killer, npcEntity.NpcTemplateId, npcEntity.Name);
				return;
			}

			_logger.LogInformation("Combat: entity 0x{Target:X8} killed ({Cause}); not in world model, no respawn.", targetHandle, cause);
			_combat.Remove(targetHandle);
		}

		/// <summary>
		///     The NPC's authoritative live vitals: the world-model entity's, which is also returned so the caller can
		///     run its death lifecycle.
		/// </summary>
		/// <remarks>
		///     Returns null when the handle does not resolve to a live NPC in the player's zone, and the caller must
		///     treat that as a refusal. Conjuring per-session vitals instead would spend the damage on a phantom the
		///     world never sees - a target that has left the player's zone view would take no real damage while its
		///     floating numbers read off the phantom - with nothing logged and nothing thrown. Inventing state to avoid
		///     reporting a failed lookup is exactly what the no-silent-fallback rule forbids.
		/// </remarks>
		private EntityVitals? ResolveNpcVitals(INetworkClient client, uint targetHandle, out WorldEntity? npcEntity)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(targetHandle, out npcEntity) && npcEntity is { IsPlayer: false })
				return npcEntity.Vitals;

			npcEntity = null;
			_logger.LogError("Combat: no live NPC for target handle 0x{Handle:X8} in {Player}'s zone - refusing to apply damage. " +
				"The client is attacking something the server cannot find, which is a targeting/interest bug, not a combat one (#695).",
				targetHandle, client.Session.Character?.Name ?? "<no character>");

			return null;
		}

		/// <summary>
		///     The floating number + combat-log line for a spell hit on an NPC. Retail carries spell damage on
		///     StatDamageIIR - the LockedToChanged -> StatDamage -> ApplySpell bundle, with no DamageResult
		///     (casting-combat capture) - so a spell hit must use StatDamage, not the melee DamageResult (which the client
		///     only floats after a BCC attack behavior arms the target). Mirrors the incoming DoT-tick StatDamage on the
		///     player: statValue = the target's resulting health, delta = -damage, caster = the player. Call after the
		///     damage is applied so the health is the post-hit value.
		/// </summary>
		/// <remarks>
		///     <paramref name="spellId" /> is already a base id - it comes off the cast trailer, which carries the bare
		///     `Spells` key (`HandleBccCastSpell` shifts it to wire form only for `ApplySpell`), and every other use in
		///     this class treats it as one (`GetSpell(spellId)`, `MagicDamageFor(spellId)`). Shifting it again hands the
		///     client an id it cannot load ("Resource 1040005:14321 does not exist in any available RDB sources"), and
		///     the spell's own GameActions - the recast setter among them - are lost with it.
		/// </remarks>
		private Task SendSpellDamageNumber(INetworkClient client, uint npcHandle, uint casterHandle, int damage, uint spellId)
		{
			// A PvP target is asked for first, and quietly: ResolveNpcVitals refuses a player entity by design and
			// reports it as a targeting bug, so going through it for a legal opponent would log an error on every
			// spell hit of a minigame.
			var vitals = PlayerTarget(client, npcHandle)?.Vitals ?? ResolveNpcVitals(client, npcHandle, out _);

			// Only ever reached from ApplyNpcDamage's onDamaged, which already resolved the same handle - so a null
			// here means the target vanished between the hit and its number, and a floating number read off nothing
			// is worse than none.
			return vitals == null
				? Task.CompletedTask
				: SendStatDamage(client, npcHandle, vitals.Health, -damage, SpellDamageStatId, casterHandle, spellId); // already a base id - see the remarks
		}

		// The hit side of a swing: DamageResultIIR + the target's live-vitals application + the player's EnergyIIR
		// stamina update (the retail post-ack trio of the armed swing path).
		private async Task SendSwingDamage(INetworkClient client, uint instance, uint targetHandle, uint damage)
		{
			await SendDamageNumber(client, targetHandle, instance, damage, DamageSchool.Crushing); // player physical melee

			await ApplyDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target.
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			var player = _sim.GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, EnergyCodec.Build(instance, player.Stamina));
		}

		/// <summary>
		///     The generic "every behavior gets an echo-ack" reply (docs/subsystems/combat.md rule 1) for an Interact
		///     request with no special result block - the air-swing path uses it, and so does a plain item-use
		///     (RightClickItem, e.g. the Totem of Origin). Phase = the request's Mode, or the session counter when the
		///     caller did not parse the args. No-op for a malformed (too-short) payload.
		/// </summary>
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

		/// <summary>
		///     The channel-complete twin of <see cref="SendBehaviorEchoAck" />: the record that closes the cast bar when
		///     a server-timed channel ends (a spell cast, both directions of the mount toggle). Same request echo,
		///     emitted as form 0x000A - see <c>BehaviorAcks.ChannelCompleteAck</c> for why callers must not reach that
		///     form by patching the request's bytes.
		/// </summary>
		public static Task SendBehaviorChannelCompleteAck(INetworkClient client, byte[] rawPayload, uint phase)
		{
			if (rawPayload.Length < 26)
				return Task.CompletedTask;

			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.ChannelCompleteAck(rawPayload, client.Session.Character.Id, phase)));
		}

		/// <summary>
		///     Sends the cast-interrupt abort BccAck: the state=0x0007 reply to the client's cast-cancel that drops the
		///     cast bar, cancels the animation and pops the "Interrupted" text client-side. Built from the stored cast
		///     begin args + the incoming cancel payload (behaviorId + tail). Phase = the begin's Mode.
		/// </summary>
		public static Task SendCastInterruptAbort(INetworkClient client, byte[] beginArgs, byte[] cancelPayload, uint phase)
		{
			var instance = client.Session.Character.Id;
			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.CastInterruptAbort(beginArgs, cancelPayload, instance, phase)));
		}

		/// <summary>
		///     The abort for a cast the server opened (rest) - see <c>BehaviorAcks.ServerCastInterruptAbort</c> for why
		///     it cannot share the splice of <see cref="SendCastInterruptAbort" />.
		/// </summary>
		public static Task SendServerCastInterruptAbort(INetworkClient client, byte[] beginArgs, byte[] cancelPayload, uint clientCommandId)
		{
			var instance = client.Session.Character.Id;
			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.ServerCastInterruptAbort(beginArgs, cancelPayload, instance, clientCommandId)));
		}

		/// <summary>
		///     The combat-damage record for a hit (DamageResultIIR): a player's outgoing swing, or an NPC's hit on the
		///     player (target=player, attacker=NPC) so the player sees the hit number and not just the bar move.
		/// </summary>
		/// <remarks>
		///     <para>
		///         `DamageResultIIR.damage` is the target's health drain, on the x100 wire scale - not a floating number
		///         that happens to accompany the health move, but the move itself. The client subtracts this field
		///         from its own health, which is why a `StatUpdateIIR` never lowers the player's own bar however
		///         correct it is: the client is authoritative downward and this is what it listens to. Retail's own
		///         arithmetic, off `Hoxxling_..._gameplay` frames 7354-7432 on the player's own handle: the
		///         DamageResults between two StatDamage records sum exactly to the difference of their statValues
		///         (786 + 33 == 15531 - 14712; 559 + 33 == 14712 - 14120), so both are in the same units as stat 27.
		///         Across 1,657 hits in 23 captures, retail sends a health packet on a minority of them (StatUpdate
		///         24.8%, StatDamage 16.1%) because the damage number carries it. Passing a display number here would
		///         drain one hundredth of a hit point per swing and leave the bar visibly frozen.
		///     </para>
		/// </remarks>
		/// <param name="school">
		///     The attacker's damage school (not a hit result - see <c>CombatEffectBuilder</c>). Defaults to Piercing,
		///     the school of the captured NPC melee hit (fr 6514), a stand-in until per-NPC/per-weapon schools are
		///     modelled; player-melee callers pass Crushing.
		/// </param>
		public static Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint displayDamage, DamageSchool school = DamageSchool.Piercing)
		{
			return SendWorldPacket(client, DamageResultCodec.Build(targetHandle, attackerHandle, displayDamage * VitalsWireScale, school));
		}

		/// <summary>
		///     Acks an equip/unequip move (Interact 0x09) and appends the 0x201C bookkeeping entry retail sends with it:
		///     prefix 0x65 + state = the destination equip slot for a move into a slot, prefix 0x68 + state 0 for a move
		///     out of one (combat capture frames 289-290/320-321/375-376).
		/// </summary>
		public static async Task SendEquipMoveAck(INetworkClient client, byte[] rawPayload, uint mode, Item movedItem)
		{
			if (rawPayload.Length < 26)
				return;

			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EquipMoveAck(rawPayload, client.Session.Character.Id, mode, movedItem)));
		}

		/// <summary>
		///     Sends the NPC counter-attack animation ack to the player it hit: a BehaviorControlCenterIIR with actor =
		///     the NPC handle (the "second BccAck", docs/subsystems/combat.md), so the client plays the NPC's swing + the
		///     player's hit-react. Visual only - the authoritative HP move rides the world-tick StatUpdate, so the damage
		///     here is just the floating combat number. The animation/result params are capture-typical (not
		///     semantically decoded); the swing counter increments per NPC swing to mirror the per-actor counter the
		///     capture carries.
		/// </summary>
		/// <param name="direction">
		///     The zone the mob swung from. It must be the same draw the mitigation spent: the result record's type word
		///     is that direction on the wire, so the client is told what the player actually took. It is the five-zone
		///     <see cref="SwingDirection" /> rather than the three-arc <c>ShieldArc</c> because retail's mobs use all
		///     five and the arc is only the guard that covers it.
		/// </param>
		/// <param name="comboId">
		///     The `combo_npc_generic_*` row this swing performs, 0 for a plain one. It rides the same `combo_id` field a
		///     player's combo swing uses, which is what makes the client play the combo's animation and name it in the
		///     combat log rather than showing a generic hit.
		/// </param>
		public static async Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId = 0, bool ranged = false)
		{
			var playerHandle = client.Session.Character.Id;
			// args.f1 is the NormalAttack behavior TypeId the client resolves to pick the swing animation - not an
			// ability id (an ability id there plays the player's hit-react + audio but leaves the NPC body
			// un-animated). The capture's beach mobs use ~0xBEEC (48876) across several NPCs (per-NPC jitter of a
			// few); 0xBEEC is the representative value. A shot names its own cluster, 77813-77817.
			var resultType = BehaviorAcks.SwingDirectionCode(direction);
			if (!ranged)
			{
				await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(npcHandle, playerHandle, damage, 0xBEEC, swingCounter, resultType, comboId: comboId)));
				return;
			}

			// A shot is two records - begin then complete - carrying the same swing counter, the same combo and a
			// byte-identical tail. The begin alone leaves the client with a half-finished action; the melee behaviour
			// plays a sword swing for a bow.
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

		/// <summary>
		///     Sends a caster NPC's spell cast to the player it targets: the ApplySpell (MagicBuffIIR) framed from the NPC
		///     handle (applyFrom = npc, applyTo = player), built from the NPC's NpcSpell row - the counterpart of the
		///     melee <see cref="SendNpcAttackAnimation" />. The HP move rides the caller's PushVitals; this is the cast
		///     packet/effect.
		/// </summary>
		public static Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			var playerHandle = client.Session.Character.Id;
			var body = MagicBuffCodec.Build(playerHandle, npcHandle, spell.SpellId, spell.Payload);
			return SendWorldPacket(client, body);
		}

		/// <summary>
		///     Retail's per-tick vitals regen (RegenIIR): a compact [+hp][+mana][+stamina] delta the client adds to the
		///     current bars, x100 wire scale. Lets the bars regen smoothly without pushing a full StatUpdate block every
		///     tick.
		/// </summary>
		public static Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta)
		{
			return SendWorldPacket(client, RegenCodec.Build(handle, healthDelta, manaDelta, staminaDelta));
		}

		/// <summary>
		///     A StatDamageIIR - the health effect of a hit, a DoT tick or a HoT. Effect slot 1 is the typical value in
		///     the captures.
		/// </summary>
		/// <remarks>
		///     The delta is the whole packet. The client's apply (`StatDamageIIR_t` vtable slot 2, `FUN_008d5960`)
		///     writes health as <c>SetStat(statId, GetStat(statId) + record.delta)</c> (008d5b2d..008d5b5d): `statValue`
		///     is not read at all, and the delta is the only field that moves a bar. Both are stated because retail
		///     states both, and the corpus pins their scale to each other: `statValue` equals the same handle's
		///     `StatUpdateIIR` stat 27 exactly (diff 0 on twelve handles, up to 92 records each), and moves by exactly
		///     `delta` across 1,015 consecutive pairs. Stat 27 is a x100 vitals stat (`StatDisplayScale`; wire 428500
		///     draws as 4285 HP), so both fields are x100 and neither may carry a display number - a display delta
		///     against a wire-scale pool leaves the bar visibly frozen. The scaling is done here, beside the health it
		///     has to agree with, so no caller can restate it.
		/// </remarks>
		/// <param name="displayHealth">The target's health after the hit, in the display units every live <see cref="EntityVitals" /> is in.</param>
		/// <param name="displayDelta">The signed change, in those same display units. Negative for damage.</param>
		public static Task SendStatDamage(INetworkClient client, uint targetHandle, uint displayHealth, int displayDelta, uint type, uint casterHandle, uint spellBaseId)
		{
			return SendWorldPacket(client,
				StatDamageCodec.Build(StatDamages.Combat(targetHandle, displayHealth * VitalsWireScale, displayDelta * (int)VitalsWireScale, (DamageSchool)type, casterHandle, 1, spellBaseId)));
		}

		// The x100 the client keeps its vitals in. It divides only when it draws, so every vitals number on the wire
		// is pre-multiplied - see CharacterStatId.GetScale / StatDisplayScale, which recovers the same factor from the
		// client's own tooltip divisor.
		private const uint VitalsWireScale = 100;

		// Pushes a StatUpdate stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}

		// Puts a bare 0x2000 record on the world conversation. Delegates to WorldIir so this file does not hold its own
		// DispatchPassBlob sender - see WorldIir's header for why that matters.
		private static Task SendWorldPacket(INetworkClient client, byte[] record)
		{
			return WorldIir.Send(client, record);
		}

		// The on-screen kill chain. Retail sends `ChainKillIIR` on the player's own handle carrying the running count,
		// and the whole grammar is measured off the corpus (`--report chain-kill`, 67 records):
		//   * counts are 2 (x32) and 3 (x3) and never 1 - the first kill starts a chain silently;
		//   * every non-zero is closed by a 0 (32 of 32) about 9s later - the window, WorldEntity.ChainKillWindowSeconds;
		//   * escalations (2 -> 3) arrive inside that window, at 1.36-7.83s.
		// The closing 0 is the world tick's job (WorldSimulation.TickKillChains); this is the kill side.
		//
		// The chain is a live announcement and nothing else: 0 of the 67 arrivals coincide with a move of stats 106 /
		// 1000069 / 1000070, which are the persisted daily kill counters the client's stat-name table names.
		private async Task AnnounceKillChain(INetworkClient killer)
		{
			// Content:KillStreaks off means the chain is never started, which is the whole feature off in one place:
			// the tick's closing 0 (WorldSimulation.TickKillChains) can only fire for a chain a kill registered here,
			// so there is nothing left for it to close.
			if (!_killStreaks)
				return;

			var player = _sim.PlayerWorldEntity(killer);
			if (player?.RegisterKillInChain(DateTimeOffset.UtcNow) is not { } count)
				return;

			await SendWorldPacket(killer, ChainKillCodec.Build(player.Handle, count));
		}
	}
}
