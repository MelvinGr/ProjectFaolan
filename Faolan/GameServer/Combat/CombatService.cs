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
	// Combat domain logic: resolves melee swings and spell casts into damage, drives the NPC kill lifecycle
	// (despawn + scheduled respawn via the world model), tracks kill-objective quest progress, and builds the combat
	// wire packets (the BehaviorControlCenter ack, DamageResult, Energy, and 0x96B8DC59 vitals). A kill decides how
	// much XP it is worth and hands it to the LevelUpService; the level curve and the ding burst live there, not here.
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
		private readonly CombatState _combat = new CombatState();

		private readonly FatalityService _fatality; // #140: the fatality burst on a killing blow
		private readonly FactionService _factions; // #310: Khitai reputation, awarded on a kill
		private readonly IDatabaseRepository _database;

		// Content:KillStreaks - whether a kill CHAIN is tracked and announced at all (#446). Read once at construction
		// like the terrain dir, not per kill: it is a content switch for the server, not per-session state.
		private readonly bool _killStreaks;

		// Advancement (#209): a kill hands its XP here and the service owns everything downstream - the level curve,
		// the ding burst, the level-gated ability/spell unlocks and the persistence. One-directional: combat drives
		// progression, never the reverse.
		private readonly LevelUpService _levelUp;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;

		private readonly WorldSimulation _sim;

		// #146: the PvP gate and the match scoreboard both live on the CS tier (PvpMatchService owns the match), so a
		// swing at another PLAYER asks across this seam whether it is allowed before it lands - and tells it about the
		// kill afterwards. Never a direct reference to that listener: the relationship is mutual.
		private readonly ICsPlayerAgentDispatcher _csPlayerAgent;

		// #272: world-VISIBLE records (a death, an animation) go to everyone who can see the subject, not just to the
		// client whose action caused them. See WorldBroadcaster.BroadcastEntityRecords for the measurement behind that.
		private readonly WorldBroadcaster _outbound;

		// Quest kill-goals route through the QuestProgressionService (#261), which owns the broadcaster + system-message
		// seams; combat packets frame themselves directly (SendWorldPacket), so this service no longer needs either.
		public CombatService(WorldSimulation sim, IDatabaseRepository database, QuestProgressionService questProgression, ILogger logger,
			LevelUpService levelUp, FatalityService fatality, FactionService factions, WorldBroadcaster outbound, ICsPlayerAgentDispatcher csPlayerAgent, bool killStreaks)
		{
			_csPlayerAgent = csPlayerAgent;
			_outbound = outbound;
			_killStreaks = killStreaks;
			_factions = factions;
			_fatality = fatality;
			_sim = sim;
			_database = database;
			_questProgression = questProgression;
			_logger = logger;
			_levelUp = levelUp;
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
			var statBonuses = await CharacterStatBonuses.For(_database, character);
			var classBase = await _database.GetClassAttributeBase(character.Class, character.Level);
			var seeded = EntityVitals.FromCharacter(character, statBonuses, classBase);
			var vitals = _sim.GetOrAddPlayerVitals(client);

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
		//
		// `extraPercentHundredths` is a caller-supplied boost on top of the caster's own spell-line feat boost - the
		// combo finisher passes the whole-combo damage its feats grant (#697), which is a property of the COMBO
		// rather than of the spell and so cannot be read off the caster here.
		public async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId, int extraPercentHundredths = 0,
			SpellModifiers? modifiers = null)
		{
			var caster = _sim.PlayerWorldEntity(client);
			caster?.LastCombatAt = DateTimeOffset.UtcNow; // casting at a target -> in combat
			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel, caster?.MagicDamageFor(spellId) ?? 0, (caster?.SpellBoost(spellId) ?? 0) + extraPercentHundredths);

			// #633: a spell can CRIT, which it could not before - the client's own catalog states a per-spell chance
			// and a per-spell damage bonus (RDB 147/129) on ~1,225 records each, and the caster's Critical Rating adds
			// to both on the melee curve. A spell whose record states no chance does not crit; nothing is invented for
			// the 20,278 that say nothing. Like a melee crit this is NOT a distinct wire flag - retail conveys it by
			// the larger number alone.
			var spell = await _database.GetSpell(spellId);

			// ⭐ #710: a trained FEAT or a live BUFF can move the spell's OWN crit numbers before the caster's rating
			// is added. `Spell Critical Chance` (147) and `Spell Critical Damage` (129) are the two commonest stats
			// in the spell-scoped modifier table - 269 and 340 rows - and they were imported and inert: this is the
			// "a live buff that makes your next Fireball crit harder" the ticket is named for. Both are already in
			// the column's own hundredths, which is the unit `SpellModifiers.Apply` expects.
			var mods = modifiers ?? SpellModifiers.None;
			var spellCritChance = (uint)mods.Apply((int)(spell?.CriticalChanceHundredths ?? 0), CharacterStatId.SpellCriticalChance);
			var spellCritDamage = (uint)mods.Apply((int)(spell?.CriticalDamageHundredths ?? 0), CharacterStatId.SpellCriticalDamage);

			var critChanceHundredths = CombatResolver.SpellCritChanceHundredths(spellCritChance, caster?.CriticalRating ?? 0);
			if (critChanceHundredths > 0 && Random.Shared.Next(SpellCritRollScale) < critChanceHundredths)
				damage = CombatResolver.ApplySpellCritDamage(damage, spellCritDamage, caster?.CriticalDamageRating ?? 0);

			await ApplyDamage(client, targetHandle, damage, $"spell {spellId} from 0x{casterHandle:X8}",
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
		//
		// Returns whether the swing LANDED, so the caller can gate what rides on it - the combo finisher is the one
		// that matters (#696). The finisher is a separate op in the inbound handler, and it used to run whatever this
		// method decided, so a refused swing still delivered the combo's spell: melee stayed unbounded for anyone
		// finishing a combo even once the plain swing was gated.
		public async Task<bool> SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle, uint mode = 0, ComboSwing? combo = null)
		{
			// rawPayload = [bodyLen u32][subop u32][c350 u32][instance u32][seq u16][behaviorId u32][argsLen u32][args][reqTail]
			if (rawPayload.Length < 26)
				return false;

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
			if (!hasEnemyTarget || !WithinMeleeReach(client, targetHandle))
			{
				await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, phase)));
				return false;
			}

			var level = client.Session.Character.Level;
			// #395: melee damage is driven by the equipped main-hand weapon (min/max from the RDB template), not a flat
			// level value, so a better weapon hits harder. Unarmed / no imported weapon damage falls back to the level curve.
			var weapon = await _database.GetEquippedWeaponDamage(client.Session.Character.Id);
			var attackerEntity = _sim.PlayerWorldEntity(client);
			attackerEntity?.LastCombatAt = DateTimeOffset.UtcNow; // attacking -> in combat (halts HP/mana regen)
			// Roll between the weapon's min and max for per-swing variance (#395); Combat Rating adds a flat bonus (#399).
			var damage = (uint)CombatResolver.ResolveMeleeDamage(weapon?.Min, weapon?.Max, level, Random.Shared.Next(101), attackerEntity?.CombatRating ?? 0);

			// #697: what the player's trained feats do to THIS combo attack. A combo's opening and finishing weapon
			// attacks each carry their own damage and critical-chance bonus (GameAction 53250
			// `ChangeInheritedWeaponAttack`, #593) - "Fierce Opening" is +30% on every combo's opener, "Brutal Blows"
			// is +10% crit chance on Overreach's finisher. A plain swing is not part of a combo and takes neither.
			var comboBonus = combo != null ? attackerEntity?.ComboBonus(combo.Combo.Id) ?? ComboFeatBonus.None : ComboFeatBonus.None;
			damage = (uint)CombatResolver.ApplyPercentBonus((int)damage, comboBonus.SwingDamagePercentHundredths(combo?.IsOpener == true, combo?.IsFinisher == true));

			// #399: roll a critical hit off the player's Critical Rating; a crit scales the damage by the base 1.5x plus
			// their Critical Damage Rating. A crit is NOT a distinct wire flag - RE of all captured DamageResults
			// (2026-07-22) showed the `type` field is the ATTACKER's damage school (constant per attacker; the player's
			// physical melee is always type 3), the two trailing fields are always 0, and there is no crit indicator.
			// So retail conveys a crit purely by the larger number; we send the player's melee type (3) either way.
			var criticalRating = (uint)Math.Max(0, (attackerEntity?.CriticalRating ?? 0) + comboBonus.CriticalRating(combo?.IsOpener == true, combo?.IsFinisher == true));
			var crit = CombatResolver.IsCriticalHit(criticalRating, Random.Shared.Next(100));
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
			await ApplyDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = _sim.GetOrAddPlayerVitals(client);
			await SendWorldPacket(client, EnergyCodec.Build(instance, player.Stamina));

			return true;
		}

		// A spell crit rolls in HUNDREDTHS of a percent, not whole percents like a swing: the modal per-spell chance
		// is 2.50%, which a 0..99 roll cannot express.
		private const int SpellCritRollScale = 10000;

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

			// #696: an unarmed swing reaches no further than an armed one. This path had NO reach check at all - it is
			// the one place `SendSwingDamage` is called from, so a bare-handed player could kill anything selectable at
			// any distance even after the armed swing was bounded. Unlike the armed path the echo-ack has already gone
			// out above (retail's whole reply to an unarmed air swing IS that ack), so an out-of-reach swing needs
			// nothing further sent - it is already indistinguishable from the air swing it now is.
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			if (!hasEnemyTarget || !WithinMeleeReach(client, targetHandle))
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
		public async Task SpendCasterMana(INetworkClient client, uint spellId, SpellModifiers? modifiers = null)
		{
			var player = _sim.GetOrAddPlayerVitals(client);
			if (player.MaxMana == 0)
				return;

			var spell = await _database.GetSpell(spellId);

			// #710: a trained FEAT or a live BUFF can make the spell cheaper - "Mana Cost" is the stat these records
			// aim at most (217 of its 228 shipped rows are relative, 11 absolute). ⭐ Both halves apply since #787:
			// `ManaCost` stores the property-run field verbatim, so an absolute modifier on it needs no conversion.
			// Applied to the RAW cost before the percent-of-pool resolve, so a spell whose cost is itself a percentage
			// is scaled rather than double-converted.
			var raw = (ushort)(modifiers ?? SpellModifiers.None).Apply(spell?.ManaCost ?? 0, CharacterStatId.ManaCost);
			var cost = CombatResolver.ResolveSpellManaCost(raw, player.MaxMana, spell?.ManaCostIsPercent ?? false);
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

			var player = _sim.GetOrAddPlayerVitals(client);
			if (player.MaxStamina == 0)
				return;

			player.Stamina = cost >= player.Stamina ? 0 : player.Stamina - cost;
			await SendStatUpdate(client, player.ToCurrentsBlock(), client.Session.Character.Id);
		}

		// Damages every attackable NPC within the spell's blast radius of `target` for one tick: shows the floating
		// DamageResult number (0xFEC4158A) over each victim + applies the HP move. Only HOSTILE NPCs are hit -
		// friendly AND neutral bystanders are spared, the same rule melee splash follows; players never (no PvP
		// model). ⚠️ It used to hit neutral too, which is what made an aura rake civilians in a town.
		// Re-evaluated each call so NPCs that wander in/out of the blast are picked up per tick. Returns the count hit.
		// #772: applies one of a spell's AREA effects - "apply spell X to up to N entities within R metres of the
		// caster". The two shapes share this entirely and differ only in WHO calls it:
		//   * an INSTANT area (`SpellAreaEffect.PeriodCentiseconds == 0`) is applied once, by the cast path;
		//   * an AURA (period > 0) is applied by the world tick, every period, for as long as the buff that owns it
		//     is up - `Gangrenous Stench` is a 25 s self-buff whose aura hits a 7 m radius every 4 s.
		//
		// ⚠️ The damage is the EFFECT spell's, not the castable's. That distinction is the whole of #772: 221070 is
		// the thing you press (cast time, mana, recast) and 221069 is the thing that lands. Resolving the castable
		// here would apply the wrong record's numbers - and for a pure carrier like `Life Leech`, no numbers at all.
		//
		// Centred on the CASTER rather than a reticle, which is what "Hostile Self Centered Area Spell" means in the
		// client's own targeting-mode line; a ground-targeted blast is `ResolveGroundAoe` and keeps its reticle.
		//
		// ⚠️ This is the DAMAGE half only. Landing the effect itself on each victim - the icon, the DoT, its own
		// areas - is `WorldSimulation.ApplyAreaEffect`, which is why this answers WHO was hit rather than how many
		// (#776). Splitting it that way keeps the buff registry with the world model, which owns it.
		public async Task<IReadOnlyList<uint>> ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || radius <= 0)
				return [];

			var casterLevel = client.Session.Character.Level;
			var damage = CombatResolver.ResolveSpellDamage(effectSpellId, casterLevel, player.MagicDamageFor(effectSpellId), player.SpellBoost(effectSpellId));

			// The area radiates from whoever HOLDS it, and that is not always the player: the #776 chain re-emits an
			// area from the INFECTED MOB (Gangrenous Stench's 221069 spreads 221068 to everything within 4 m of the
			// mob it is ticking on), and centring that on the caster would spray it from the wrong point. For a
			// player's own cast or aura `sourceHandle` IS the player, so this resolves to exactly what it used to.
			//
			// A source that does not resolve in the player's own zone reaches NOBODY rather than falling back to the
			// player: an emitter in another instance is not a reason to blast the area out of the player instead.
			// The player themselves is the one exception, since a caster is not always in the zone's entity table.
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
			// Same gate as melee splash and the ground blast. ⚠️ This used to read
			// `Disposition != DispositionFriendly`, so an aura swept up NEUTRAL bystanders (yellow-name civilians)
			// that a sword swing beside them would have spared - reported in-client against Gangrenous Stench, whose
			// 7 m aura re-ticks every 4 s for 25 s and so rakes a whole street corner.
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

		// #776: one DoT tick against a NON-PLAYER entity - the bleed a player's debuff leaves on a mob. Runs through
		// the same `ApplyNpcDamage` a cast does, so a tick that finishes the mob runs the whole kill lifecycle (XP,
		// corpse/loot, kill-quest credit) and credits the player who applied the debuff rather than nobody.
		//
		// A positive delta (a heal-over-time) is a no-op here: NPC pools have no heal path, and quietly re-using the
		// damage one with a flipped sign would resurrect a corpse mid-lifecycle.
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

			// Per-spell where the client states one (Spell.AreaRadius, #349); GroundAoeRadiusUnits otherwise.
			var spell = await _database.GetSpell(spellId);
			var radius = spell?.AreaRadius > 0
				? spell.AreaRadius
				: GroundAoeRadiusUnits;

			// The SAME gate melee splash uses (CombatTargeting.CleaveTargets) - "vendors standing nearby must never
			// take splash" applies no less to a blast than to a sword.
			//
			// ⚠️ It used to read `Disposition != DispositionFriendly`, which also hits NEUTRAL (disposition 2) - 386
			// templates, 279 of them yellow-name civilians - while melee spared them. An AoE dropped in a town centre
			// therefore damaged bystanders a swing would not have touched. (Unknown disposition was never the
			// problem: the spawn path maps NULL to FRIENDLY, so `entity.Disposition` is never null and an
			// un-polled NPC was already spared by either test.)
			//
			// `IsDead` was missing as well, so corpses kept taking ticks.
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

		// Whether the swing can actually REACH its named target (#696). The target handle is the client's, and until
		// this existed nothing bounded it: melee landed at any distance, because the only reach in the attack path
		// belonged to the CLEAVE set and `CombatTargeting` exempts the primary from the geometric test on purpose -
		// "it takes the swing by being selected, whatever angle it is at". That exemption is right about the ANGLE (you
		// face what you selected, so cone-testing the primary would reject a legitimate swing) and it silently took the
		// DISTANCE with it.
		//
		// ⚠️ Melee is the only one of these the server has to own. The client checks a SPELL's range itself and simply
		// never sends an out-of-range cast (confirmed in-client 2026-08-07: "spells are checked client side, but melee
		// you can always do"), so a server-side spell gate would guard a door the client already holds shut. The swing
		// it always sends. Same division of labour as #271, where the client owns the recast it can read and the server
		// owns what the client cannot.
		//
		// An out-of-reach swing is answered as an AIR SWING - the same plain echo-ack a swing with no target gets -
		// rather than dropped. That shape is captured and the client already handles it, and #271 is the standing
		// reminder that silently refusing an action the client thinks it made leaves it waiting on a reply.
		//
		// ⚠️ The reach is `CombatTargeting.SwingReach`, which is PROVISIONAL - see it for why there is no shipped
		// number to use instead.
		//
		// The GEOMETRY (and the zone resolution that got it wrong the first time) lives in
		// `CombatTargeting.ResolveSwingReach`, which is pure and tested; this is only the session half - find the
		// attacker, and SAY what happened. The refusal is logged at INFORMATION on purpose: the file logger a play
		// session is read back from is where the evidence for this ticket has to appear, and the first cut's Debug
		// line is a large part of why "the gate does not bite" was diagnosed as a stale-position problem when the
		// gate had simply never resolved the (instanced) zone it was asked about.
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
			// Same instance trap as WithinMeleeReach: TryGetZone(MapId) resolves the DEFAULT instance only, so cleave
			// silently did nothing in an instanced playfield (#268).
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

		// `onDamaged` is the caller's own damage RECORD (the floating number) - the spell path's StatDamage. It runs
		// between the fatality roll and the vitals/death records because that is where retail puts it: in Stickienecro's
		// ice-strike fatality the victim's StatDamage is idx 542, after the burst (535-538) and before the death pose
		// (546) and BccIIR (549). Sending it after the corpse was already posed put the killing blow's own number
		// behind the death.
		// The damage funnel. A target handle is an NPC or ANOTHER PLAYER, and only the second needs a permission
		// question, so the split is here rather than repeated in the seven callers (melee, cleave, spell, DoT tick,
		// ground AoE, area effect, `.kill`).
		//
		// ⚠️ A player target that is not a legal PvP opponent is REFUSED here, with its own line. Letting it fall
		// through to ApplyNpcDamage would refuse it too - the NPC resolver rejects a player entity - but it would
		// report a targeting/interest bug (#695) for what is really a rules answer, and that is exactly the kind of
		// mislabelled log the #695 diagnosis was lost to the first time.
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

		// The target as a live PLAYER in the attacker's own zone, or null when it is not one. Resolved through
		// ZoneForEntity like every other target lookup here - TryGetZone(MapId) would answer the DEFAULT instance and
		// so miss every minigame match, which is the one place this is reached from (#268/#512).
		private WorldEntity? PlayerTarget(INetworkClient client, uint targetHandle)
		{
			var attacker = _sim.PlayerWorldEntity(client);
			return attacker != null && _sim.World.ZoneForEntity(attacker).TryGet(targetHandle, out var target) && target is { IsPlayer: true }
				? target
				: null;
		}

		// Player-versus-player damage (#146). The shape mirrors the NPC-hits-player path in WorldSimulation rather
		// than the NPC-damage one, because the VICTIM is a live session with a health bar of its own: they get the
		// incoming floating number and their own vitals push, while the attacker gets the target's bar move (its
		// outgoing number was already sent by the caller, like every other hit).
		//
		// The DEATH is not finished here on purpose. A dead player's release screen, buff strip and death penalty are
		// the world tick's (TickPlayerDeaths), which fires off `vitals.IsDead` whatever emptied the bar - so a PvP kill
		// takes exactly the same route as a mob kill, a fall or lava, and there is no second death sequence to keep in
		// step. What IS this path's own is the scoreboard: the career counters and the match's kill credit.
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

			// Attribute the death to the KILLER, never to the victim: the client's ResurrectView goes into
			// wave-respawn mode iff the killer resolves to the local player (#214), and a PvP death must release
			// instantly like every other.
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

			// The PvP CAREER pair the login snapshot carries as stats 0x290 / 0x292 (#808). Persisted per event for
			// the same reason as the PvE pair: a kill is earned once.
			attackerCharacter.PvpKills++;
			victimCharacter.PvpDeaths++;
			await _database.SetCharacterPvpKillCounts(attackerCharacter.Id, attackerCharacter.PvpKills, attackerCharacter.PvpDeaths);
			await _database.SetCharacterPvpKillCounts(victimCharacter.Id, victimCharacter.PvpKills, victimCharacter.PvpDeaths);

			// And the match's own scoreboard, which may end it on the score limit.
			await _csPlayerAgent.CreditPvpMatchKill(attackerCharacter.Id, victimCharacter.Id);
		}

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

		// A kill's share of the killer's advancement: the XP the creature is worth, handed to the progression service
		// (which owns the level curve, the ding burst and the persistence), plus the PvE kill counter, which is this
		// path's alone.
		//
		// What a kill decides is the AMOUNT - `CombatResolver.ResolveKillXp(npcLevel)`. What that amount then does to
		// the character is not combat's business, and used to be 40 lines of level curve + ding burst right here.
		private async Task AwardKillXp(INetworkClient killer, uint npcLevel)
		{
			var character = killer.Session.Character;

			// The sheet's kill counter (#447). Persisted here rather than at autosave because a kill is earned once,
			// and a crash between the kill and the save loses it - the same reason the XP is.
			// This is the NPC counter, not the PvP one (#808): the slot the snapshot sends as `PvPTotalKills` (0x290)
			// is a PvP CAREER total, and a mob kill used to land on it. `AwardKillXp` is reached only for a creature
			// kill - it takes the NPC's level - so every increment here is PvE by construction.
			character.NpcKills++;
			await _database.SetCharacterNpcKillCounts(character.Id, character.NpcKills, character.NpcDeaths);

			await _levelUp.ApplyXp(killer, CombatResolver.ResolveKillXp(npcLevel));
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
				// and respawns the creature much later still.
				// Don't despawn here - that instant-popped the corpse before the death animation could play.
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
				// ⚠️ FLUSH what it walked but never had broadcast, BEFORE freezing it (#272). The tick only emits a
				// move once the creature has covered 6u since the last one, so a mob killed MID-CHASE - which is what
				// killing one at range means - is carrying up to 6u the client has not seen. The pose below states the
				// true death position, so without this the corpse jumps that distance onto its killer and only then
				// plays the death animation. A melee kill never showed it: the chase had already stopped and flushed.
				//
				// Retail keeps them in agreement rather than posing somewhere else - 250 of 267 captured death poses
				// sit at distance 0.00 from the end of that handle's preceding MoveTask - so the client is brought up
				// to date and the pose stays where the creature actually died.
				await _sim.FlushPendingMove(npcEntity);

				// ⚠️ BROADCAST, not sent to the killer alone. A death is world-visible: everyone watching the creature
				// must see it drop its target, freeze in its death pose and play its death animation, or it stands
				// there alive-looking for every other player until it fades. Measured in the two-client capture - every
				// subject of `LookAtIIR` (8/8), `MoveTaskIIR` (12/12, the mode-6 poses among them) and
				// `BehaviorControlCenterIIR` (11/11) reaches BOTH clients. See BroadcastEntityRecords.
				//
				// #742: the BCC's field3 is the dying NPC's own BEHAVIOUR COUNTER, not a "death variant" - so its death
				// takes the next index after its last swing, which is exactly the 5 -> 6 -> 6(abort) -> 7 progression
				// one captured NPC's records walk. `AttackSwingCounter` is the same counter its swing acks already
				// number themselves with, so death simply continues it.
				await _outbound.BroadcastEntityRecords(npcEntity,
					LookAtCodec.Build(LookAts.DropTarget(targetHandle)),
					MoveTaskCodec.Build(DeathSequence.Pose(targetHandle, npcEntity.Position)),
					BehaviorControlCenterCodec.Build(DeathSequence.Bcc(targetHandle, npcEntity.DeathBehaviorId, ++npcEntity.AttackSwingCounter, npcEntity.DeathActivationTime)));

				await AwardKillXp(killer, npcEntity.Level);
				// #310: Khitai reputation. A no-op for every base-game NPC - the faction ids are Khitai's and a
				// creature with no `NpcStatCore.FactionId` moves nothing.
				await _factions.AwardForKill(killer, npcEntity.FactionId);
				await AnnounceKillChain(killer);

				_logger.LogInformation("Combat: NPC {Name} 0x{Target:X8} killed ({Cause}); loot drops now, corpse fades in {Fade}s, {Fate}.", npcEntity.Name, targetHandle, cause,
					WorldEntity.CorpseFadeSeconds, npcEntity.NoRespawn
						? "then it leaves the world (no respawn)"
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
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(targetHandle, out npcEntity) && npcEntity is { IsPlayer: false })
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
		// player. Call AFTER ApplyNpcDamage so the health is the post-hit value.
		//
		// ⚠️ RETAIL PUTS NOTHING IN `SpellBaseId` HERE - it sends ZERO. Measured over the corpus: of 3,061
		// StatDamageIIR records, 3,059 carry 0, and the two that do not name an id absent from the whole spell
		// catalog. Split by stat id it is total for ours - all 50 records with statId 10 (`SpellDamageStatId`) carry
		// 0, as do all 2,892 with statId 0 and all 119 with statId 5.
		//
		// This used to pass `spellId >> 8`, copying the DoT-tick call in `WorldSimulation` where the source really IS
		// a wire id (`buff.BuffId`). Here `spellId` is ALREADY the base - it comes off the cast trailer, which carries
		// the bare `Spells` key (`HandleBccCastSpell` shifts it to wire form only for `ApplySpell`), and every other
		// use in this class treats it as one (`GetSpell(spellId)`, `MagicDamageFor(spellId)`). So it was a shift too
		// many on a field that should have been empty, and the client said so on every single spell hit (#767):
		//
		//     Resource 1040005:14321 does not exist in any available RDB sources.
		//     SpellTemplate_t::GetMagicSpell failed to load spell with id 14321        (= 3666380 >> 8, Chill)
		//
		// 1040005 is the spell record type, so the client was being handed a spell id, failing to load it, and losing
		// the spell's own GameActions with it - the recast setter among them.
		private Task SendSpellDamageNumber(INetworkClient client, uint npcHandle, uint casterHandle, int damage, uint spellId)
		{
			// A PvP target is asked for FIRST, and quietly: ResolveNpcVitals refuses a player entity by design and
			// reports it as a targeting bug, so going through it for a legal opponent would log an error on every
			// spell hit of a minigame (#146).
			var vitals = PlayerTarget(client, npcHandle)?.Vitals ?? ResolveNpcVitals(client, npcHandle, out _);

			// Only ever reached from ApplyNpcDamage's onDamaged, which already resolved the same handle - so a null
			// here means the target vanished between the hit and its number, and a floating number read off nothing
			// is worse than none.
			return vitals == null
				? Task.CompletedTask
				: SendStatDamage(client, npcHandle, vitals.Health, -damage, SpellDamageStatId, casterHandle, CombatResolver.RetailSpellBaseId);
		}

		// The hit side of a swing: DamageResultIIR + the target's live-vitals application + the player's EnergyIIR
		// stamina update (the retail post-ack trio of the armed swing path).
		private async Task SendSwingDamage(INetworkClient client, uint instance, uint targetHandle, uint damage)
		{
			await SendDamageNumber(client, targetHandle, instance, damage, DamageSchool.Crushing); // player physical melee

			await ApplyDamage(client, targetHandle, (int)damage, $"melee from 0x{instance:X8}");

			// Cleave: the swing also hits the hostile NPCs clustered around the target (#18).
			await ApplyMeleeCleave(client, instance, targetHandle, damage);

			var player = _sim.GetOrAddPlayerVitals(client);
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

		// The channel-COMPLETE twin of the above: the record that closes the cast bar when a server-timed channel ends
		// (a spell cast, both directions of the mount toggle). Same request echo, emitted as form 0x000A - see
		// BehaviorAcks.ChannelCompleteAck for why the callers must not reach that form by patching the request's bytes.
		public static Task SendBehaviorChannelCompleteAck(INetworkClient client, byte[] rawPayload, uint phase)
		{
			if (rawPayload.Length < 26)
				return Task.CompletedTask;

			return SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.ChannelCompleteAck(rawPayload, client.Session.Character.Id, phase)));
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
			// Content:KillStreaks off means the chain is never STARTED, which is the whole feature off in one place:
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
