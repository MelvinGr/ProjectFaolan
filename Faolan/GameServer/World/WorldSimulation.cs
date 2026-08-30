using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The live in-memory world (Gitea #404): owns the world model + the connected players'
	// authoritative transforms/vitals, and drives the 10 Hz simulation tick (NPC respawn/AI, vitals regen, player
	// death/respawn, resurrection-point discovery). Extracted out of GameServerListener so the simulation's state +
	// timing are no longer fused with the networking front-door; it reaches the wire through IWorldOutbound. Packet
	// CONSTRUCTION (spawn/move/loot broadcasts) and persistence still live on the listener for now (see the
	// architecture roadmap's later decomposition increments).
	public sealed class WorldSimulation
	{
		/// <summary>
		///     The world-entity interest radius - how far a player is streamed spawns and despawns of everything
		///     else. Every viewer-set decision in <c>WorldBroadcaster</c> and <c>InterestManager</c> turns on it.
		/// </summary>
		/// <remarks>
		///     ⭐ <b>MEASURED, not invented</b> (#856, <c>--report interest-radius</c>). It was <b>150 u</b>, which
		///     nothing measured and the corpus now REFUTES: of the ~750 despawns that survive the report's filters,
		///     <b>76% arrive inside 150 u</b>, and an interest despawn cannot fire inside the boundary.
		///     <para>
		///         The distribution is a radius rather than a smear - zero samples below 20 u, a mode at 45-55 u and a
		///         decaying tail - and the player's SPEED separates its two halves: the median rises from 68 u at
		///         2-4 u/s to 391 u at 12-20 u/s while the p10 does not move, which is what "boundary plus a variable
		///         sweep overshoot" predicts. Read off the slow buckets, where the overshoot is a few units, the
		///         boundary lands at ~60 u.
		///     </para>
		///     <para>
		///         ⚠️ This is the DESPAWN radius. Retail may run hysteresis (spawn nearer than it despawns), and only
		///         the despawn side is measurable - nothing bulk-despawns at login, which is what makes it clean.
		///         Using one constant for both means we stream a little more eagerly than retail on the way in, which
		///         is the safe direction.
		///     </para>
		///     <para>
		///         ⚠️ Not yet checked in-client, and it is a 6x cut in streamed AREA - see #856.
		///     </para>
		/// </remarks>
		public const float PlayerInterestRange = 60f;

		// NPC auto-attack. The hostile-NPC swing now plays the decoded attack-ANIMATION ack (the "second BccAck" with
		// actor = NPC handle - ApplyNpcAttack -> IWorldOutbound.SendNpcAttackAnimation, docs/subsystems/combat.md) and then the
		// 0x96B8DC59 vitals move for the HP drop. CAVEAT (why this was once gated off): on a zone with many hostiles
		// (Tortage Beach has ~400) a player who keeps dying can hit a death-loop (re-enters the world every ~10s, never
		// finishes loading). Kept on to exercise real combat death + the death/release screen (#214).
		private const bool NpcAutoAttackEnabled = true;

		// #766: play each creature's ambient idle animations (BehaviorId.Emote) so a standing NPC looks alive rather than
		// frozen on its spawn pose. Confirmed-gated in the broadcaster (never hits a loading client). A kill-switch for
		// the in-client pass, in case the animations want disabling rather than just re-timing.
		private const bool NpcAmbientAnimationEnabled = true;

		// #363 ambient spawn-pool maintenance cadence. This interval doubles as the respawn delay - a killed pool member
		// is replaced on the next pass - so it tracks the DefaultRespawnDelaySeconds placed mobs use rather than being
		// a number of its own. It was 15 s alongside the old 15 s respawn; leaving it there while placed spawns moved to
		// the measured 150 s would have made the ambient population the fast-respawning one, which is the complaint that
		// started this.
		//
		// ⚠️ **The corpus is not settled on that** (#855). Splitting `--report respawn-interval`'s 23 matches by
		// whether the creature HAS a placement separates them cleanly: the placed ones cluster (Brutal Pirate
		// 149.9-150.2 across three captures, Wild Cow/Wild Pig 119.5-120.0), while the eleven with NO placement -
		// ambient or summoned - run a 3.4 s to 99.6 s continuum (eight Hatchlings 3.4-15.4, three Panthers
		// 20.4/60.2/99.6). So retail's ambient population may well come back far faster than its placed one. It is
		// NOT changed here because a continuum is not a timer's signature - a real interval piles up at one value,
		// which is exactly what distinguishes the two clusters above - and a nest spawning ADDS during a fight
		// produces the same shape. Recording the tension rather than acting on it.
		private const double AmbientMaintainIntervalSeconds = WorldEntity.DefaultRespawnDelaySeconds;

		// #778: how often a standing player's interest is re-resolved so entities that moved THEMSELVES stream in and
		// out. Slow on purpose - the subjects walk at ~2.2 u/s against a 60 u range, so a second's granularity is two
		// units of travel, and the pass costs one distance check per entity in the player's zone.
		private const double InterestSweepIntervalSeconds = 1.0;

		// Base game-loop cadence: 10 Hz. Continuous systems run every base tick with the real elapsed dt so they stay
		// smooth; slower rate-based systems run via a TickAccumulator at their own interval. The loop measures
		// wall-clock dt, not the nominal period, so a late tick doesn't desync the rates.
		private const double BaseTickSeconds = 0.1;

		// Slow-tick reporting: how often the loop may complain that it is behind, and how far under nominal counts as
		// behind. 0.9 leaves room for ordinary timer jitter (PeriodicTimer fires on the thread pool, so a tick landing
		// a few ms late is normal and is not a load problem).
		private const double SlowTickReportSeconds = 30.0;
		private const double SlowTickThreshold = 0.9;

		// How long after dealing/taking damage a player counts as "in combat" (HP + mana regen halted). Provisional.
		private const double CombatRegenPause = 6.0;

		// Applies damage/heal-over-time from active DoT debuffs (#274). Each tracked buff with a DotDelta bleeds (or
		// heals) the target's HealthCurrent on a fixed cadence, mirroring the captured StatDamageIIR cycle: the tick
		// moves the player's vitals by the delta and sends the byte-exact StatDamageIIR (+ a PushVitals for the bar).
		// The fallback DoT cadence, for a spell whose interval the captures never established. This was once a guess
		// ("bleeds tick roughly every couple of seconds") applied to EVERY DoT, until the interval was measured per
		// spell (#437) - see Spell.DotIntervalMs.
		//
		// It survived the measurement as the MEDIAN of the 53 intervals now measured, which is the most defensible
		// thing to guess for the 31 still unmeasured. Worth knowing that it is not the most COMMON value: the
		// measured set peaks at 1 s (16 spells) against 2 s (14), so the fallback is deliberately the middle of the
		// range rather than its mode, and it is still wrong for any individual spell that lands on it.
		private const double DotTickSeconds = 2.0;

		// Vitals regen cadence (rate-based; dt-scaled by the real elapsed time when it fires).
		private const double RegenIntervalSeconds = 1.0;


		// Local-say range in world units. Built-NPC tests showed a +4-unit offset renders "beside" the player, so
		// units are ~metres; ~50 is a generous "nearby" for /say. Tunable; same-map is the hard filter either way.
		private const float SayRangeUnits = 50f;
		// Cached DoT effect per base spell id (Spell.DotDelta/DotType): looked up once per spell, then reused so the
		// per-cast NPC path doesn't hit the DB. (delta 0 = not a DoT.)
		private readonly ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)> _dotEffects = new ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)>();
		// Players currently dead, with the seconds remaining until respawn (counted down every tick).
		private readonly ConcurrentDictionary<uint, MapRegion[]> _mapAreas = new ConcurrentDictionary<uint, MapRegion[]>(); // #348: per-map banner volumes, read every tick

		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// BOUND AT Start, NOT AT CONSTRUCTION (#826). Everything this port reaches - WorldBroadcaster above all - is
		// built ON the simulation, so demanding it in the ctor is what made GameServerListener the façade for 41
		// members it does not implement: a genuine WorldSimulation <-> WorldBroadcaster construction cycle that no
		// container can build. Binding it at Start breaks that: the simulation now constructs with no outbound
		// dependency at all, and the tick - the only thing that uses it - does not run until Start.
		private IWorldOutbound _output = null!;

		// #813/#784/#786: the spell-area + chain-edge CATALOG, read directly rather than through IWorldOutbound.
		// It is a pure read over the DB (SpellAreaCatalog takes nothing else), so routing four table lookups through
		// the wire façade only made the port wider and the tick's dependency on the listener look bigger than it is.
		private readonly SpellAreaCatalog _spellAreas;

		// #272: send what `entity` has walked but not yet had broadcast, and end its move.
		//
		// The tick throttles a chase into ~6u segments (`MoveBroadcastSegment`) and flushes the remainder in its own
		// `EndMove` when the entity arrives. DEATH is the one way a move ends without going through that, so a creature
		// killed mid-chase leaves up to 6u the client never saw - and the death POSE states the true position, so the
		// corpse jumps that far onto its killer before playing its death animation. Lives here because the tick owns
		// the segment bookkeeping this is finishing.
		public async Task FlushPendingMove(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			if (entity.TakePendingMoveSegment())
				await _output.BroadcastNpcMove(entity);
		}

		// Last aura application per (holder, buff, area) - see TickAuras for why it is not WorldEntity.LastDotTick, and
		// #776 for why the HOLDER is in the key: since a mob can carry an area-emitting debuff, two entities holding
		// the same buff would otherwise share one stamp and each suppress the other's cadence.
		private readonly Dictionary<(uint Holder, uint BuffId, uint Sequence), DateTimeOffset> _auraTicks = new Dictionary<(uint, uint, uint), DateTimeOffset>();

		// #776: the NON-PLAYER entities currently carrying a buff, so the tick can find them without walking every
		// zone. Written where a buff lands on one and pruned by the tick as they empty, die or despawn - in practice a
		// handful of mobs mid-fight, never the world.
		private readonly ConcurrentDictionary<uint, WorldEntity> _buffedEntities = new ConcurrentDictionary<uint, WorldEntity>();

		private Task _tickTask = Task.CompletedTask;
		public PlayerRegistry Players { get; }

		// Server-side terrain (RDB heightmap): ground-height snapping + terrain line-of-sight (#284). Reached by the
		// broadcaster (loot-bag Y) and the WorldState tick (aggro line-of-sight).
		public TerrainService? Terrain { get; }

		// The live world model + the connected players' authoritative world entities (transform + vitals).
		public WorldState World { get; }

		// How the tick loop is actually running (achieved rate + busy fraction). Read by the keep-alive Ping reply for
		// the client's dev "Server Framerate" overlay, and by the slow-tick warning below. See TickMetrics.
		public TickMetrics Metrics { get; } = new TickMetrics(1.0 / BaseTickSeconds);

		// ⚠️ `World` and `Players` arrive by INJECTION rather than being created here (#900). Both are shared with
		// PlayerDeathService, which must not hold this class - so the state they both read is what gets passed, not
		// a reference back to their owner.
		public WorldSimulation(IDatabaseRepository database, IChatServerDispatcher agents, ILogger logger, SpellAreaCatalog spellAreas,
			WorldState world, PlayerRegistry players, PlayerDeathService playerDeaths, TerrainService? terrain = null)
		{
			_spellAreas = spellAreas;
			_database = database;
			_agents = agents;
			_logger = logger;
			Terrain = terrain;
			World = world;
			Players = players;
			_playerDeaths = playerDeaths;
		}

		// A player dying, releasing and coming back. Forwarded to below so the listener's call sites are unchanged;
		// the tick drives its two per-tick halves directly. See World/PlayerDeathService.cs.
		private readonly PlayerDeathService _playerDeaths;

		// The player's live world entity for this connection (authoritative transform + vitals), or null if not in the
		// world yet.
		public WorldEntity? PlayerWorldEntity(INetworkClient client)
		{
			return Players.TryGetValue(client, out var entity)
				? entity
				: null;
		}

		// The player's own live vitals - the WORLD MODEL is the single source of truth: they live on the player's
		// WorldEntity (like NPCs'), seeded from CharacterMaxStats. World-authoritative, never persisted (only
		// CharacterMaxStats is). Falls back to a transient seed only if the player isn't in the world yet (no combat/
		// regen happens then, so this is just defensive).
		//
		// It lives here rather than on CombatService (its historical home) because it reads nothing but this class:
		// keeping it there made BuffService depend on CombatService for one call, which closed the construction cycle
		// SpellEffectOutbound -> BuffService -> CombatService -> FatalityService -> SpellEffectOutbound.
		public EntityVitals GetOrAddPlayerVitals(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null)
				return EntityVitals.FromCharacter(client.Session.Character);

			return entity.Vitals ??= EntityVitals.FromCharacter(client.Session.Character);
		}

		// Registers (or relocates) the player in the live world after world entry, SEEDING the authoritative WorldEntity
		// transform + vitals from the persisted Character. After this the WorldEntity is the source of truth for live
		// position/vitals; the Character is only re-read on a fresh entry/teleport. Idempotent (GetOrAdd keeps the
		// existing entity, so vitals aren't reset across re-entry/teleport/respawn).
		public void RegisterPlayerInWorld(INetworkClient client, MapInstanceMode instanceMode = MapInstanceMode.Shared, IReadOnlyDictionary<CharacterStatId, int>? statBonuses = null,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null, IReadOnlyDictionary<uint, int>? spellBoosts = null,
			IReadOnlyDictionary<uint, uint>? spellScalingAttributes = null, IReadOnlyDictionary<uint, ComboFeatBonus>? comboBonuses = null,
			uint? teamId = null, bool epic = false, uint? pvpMatchId = null)
		{
			var character = client.Session.Character;

			var handle = character.Id;
			// The player's pre-existing entity (null on a fresh login) decides whether this is a re-entry that should
			// keep its current instance vs. a fresh placement that picks one from the map's mode (#268).
			Players.TryGetValue(client, out var existing);
			// #3: seed the max vitals with the equipped-gear bonus so the regen bar fills past the class base. Only the
			// fresh-entry seed uses it; a re-entry keeps its existing (already gear-aware) vitals via GetOrAdd.
			var entity = Players.GetOrAdd(client, _ => new WorldEntity(handle, character.MapId, true, character.Name ?? $"Character{handle}", client)
			{
				Vitals = EntityVitals.FromCharacter(character, statBonuses, classBase),
				// #401: cache the worn-gear physical armor + magical Protection for incoming-damage mitigation.
				ArmorRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Armor) ?? 0),
				Protection = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Protection) ?? 0),
				// #399: cache the worn-gear Hit Rating (hit chance, not yet consumed) + the crit stats.
				HitRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.HitRating) ?? 0),
				CriticalRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalRating) ?? 0),
				CriticalDamageRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalDamageRating) ?? 0),
				CombatRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CombatRating) ?? 0),
				// #399: cache the worn-gear Magic Damage added flat to the player's spell hits.
				MagicDamage = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.MagicDamage) ?? 0),
				// Cache the worn-gear natural mana/stamina regen added onto the base regen tick.
				NaturalManaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalManaRegen) ?? 0),
				NaturalStaminaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalStaminaRegen) ?? 0),
				// #400: seed the live drawn weapon set from the persisted value so a mid-session swap starts from the set
				// the character last had out (and the statBonuses above were aggregated for that same set).
				ActiveWeaponSet = character.ActiveWeaponSet,
				// #367: keep the gear bonuses themselves, not just their sum into the fields above - they are the
				// baseline a buff's stat delta is added to (and removed from) by BuffStatEffects.
				GearBonuses = statBonuses ?? new Dictionary<CharacterStatId, int>(),
				ClassBase = classBase ?? new Dictionary<CharacterStatId, int>(),
				// #590: the feat boosts for this character's spells, already fanned out from line to rank.
				SpellBoosts = spellBoosts ?? new Dictionary<uint, int>(),
				// #592: the caster's attribute scores and class, which is what the magic-damage scaling term reads,
				// plus the per-spell attribute overrides. Same inputs as the pools above (VitalAttributes.From), so
				// the two never disagree about what the character's Intelligence is.
				Attributes = VitalAttributes.From(classBase, statBonuses),
				// ⚠️ The character's LEVEL. This was never set, so every player entity carried Level 0 while only NPCs
				// got a real one - and two things read it, both silently wrong:
				//   * BuffStatEffects resolves a RAMPED buff delta off it (#599: the same buff is +887 health at level
				//     20 and +17500 at level 80), so every ramped buff on a player was resolved at level 0;
				//   * RebaseFromAttributes passes it to CharacterMaxStats.ClassBaseHundredths, which THROWS on 0.
				// The throw is what finally surfaced it: the rebase only runs when an equip CHANGES an attribute, so
				// equipping a piece with an attribute bonus killed the packet loop and dropped the player's connection.
				Level = character.Level,
				CharacterClass = character.Class,
				SpellScalingAttributes = spellScalingAttributes ?? new Dictionary<uint, uint>(),
				// #697: what the character's feats do to their combos, resolved once here for the same reason the
				// spell boosts are - a swing must not turn into a query.
				ComboBonuses = comboBonuses ?? new Dictionary<uint, ComboFeatBonus>()
			});
			// Re-apply the account-persisted GM toggles (#279): the live entity is fresh on each world entry (a map
			// change is a new world connection), so these would otherwise be lost on every zone. Only GMs ever carry them.
			if (client.Session.Account is { } account)
			{
				entity.GodMode = account.GmGodMode;
				entity.Invisible = account.GmInvisible;
			}

			// #512: `teamId` is what makes a GROUP map route to the party's copy rather than to a private one. It is
			// passed IN rather than looked up here, because the roster lives on the CS tier and the world model does
			// not reach across listeners - the caller crosses that seam.
			// #309: an EPIC entry routes to an epic copy of the map - same content, never the same zone as a normal
			// one - and the zone remembers its difficulty so re-entry routing and the (future) stat tuning can ask it.
			var instanceId = World.ResolveEntryInstance(existing, character.MapId, instanceMode, teamId, epic, pvpMatchId);
			World.ZoneFor(character.MapId, instanceId).IsEpic = epic;
			// The DB rotation is a quaternion; the live sim/movement layer is Vector3 (Euler degrees) - bridge it.
			var zone = World.Place(entity, character.MapId, instanceId, character.Position, character.Rotation);
			var nearby = zone.PlayersInRange(character.Position, PlayerInterestRange, handle).Count();
			_logger.LogInformation("World: player {Name} (0x{Handle:X8}) entered map {Map} instance 0x{Instance:X8} at {Pos}; {Nearby} other player(s) in range.", entity.Name, handle, character.MapId,
				instanceId, character.Position, nearby);
		}

		// A same-MAP (same-playfield) teleport: relocate the player to `destination` on their CURRENT map with NO
		// loading screen, reusing the death-revive same-map path (IntendedTeleport with the LIVE playfield token +
		// n3Teleport). This is how a same-playfield zone-line door works - e.g. the Thirsty Dog Inn, whose UseObject
		// (door handle 0xBEB) resolves to IntendedTeleport destMap == the current map + an N3Teleport, with NO reconnect
		// (Hoxxling_..._gameplay2: door 0xBEB -> arrival (45, 158.05, 124) on map 4020, the player already on 4020).
		// Contrast DoTeleport, which cross-maps via a full 0x2002 handoff + reconnect (loading screen). The position is
		// persisted so it survives relog.
		public async Task RelocateSameMap(INetworkClient client, Vector3 destination)
		{
			var entity = PlayerWorldEntity(client);
			var character = client.Session.Character;
			if (entity == null || character == null)
				return;

			entity.Position = destination;
			character.Position = destination;
			await _database.SaveCharacterTransform(character.Id, destination, character.Rotation, character.MapId);

			var liveToken = World.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client!, entity.Handle, character.MapId, liveToken);
			await _output.SendReviveDestination(client!, entity.Handle, character.MapId, destination, character.Rotation);
			_logger.LogInformation("World: {Player} same-map relocated to {Dest} on map {Map} (token 0x{Token:X}).", character.Name, destination, character.MapId, liveToken);
		}

		// Removes the player from the world model on disconnect. The caller broadcasts the despawn FIRST (while the
		// leaver is still in the zone so its viewers resolve), then calls this. Returns false if the player wasn't in
		// the world.
		public bool RemovePlayer(INetworkClient client, [MaybeNullWhen(false)] out WorldEntity entity)
		{
			if (!Players.TryRemove(client, out entity))
				return false;

			World.Remove(entity);
			_playerDeaths.ClearRespawnTimer(client);
			return true;
		}

		// The character ids that should hear a proximity "say" from `sender`: same-map players within SayRangeUnits of
		// the sender, plus the sender (you always hear yourself). Resolved against the live world model.
		public IReadOnlyCollection<uint> SayRecipients(INetworkClient sender)
		{
			var ids = new HashSet<uint>();
			var entity = PlayerWorldEntity(sender);
			if (entity == null)
			{
				// Not in the world model yet - fall back to just the sender so they still see their own message.
				if (sender.Session.Character != null)
					ids.Add(sender.Session.Character.Id);
				return ids;
			}

			ids.Add(entity.Handle);
			foreach (var near in World.ZoneForEntity(entity).PlayersInRange(entity.Position, SayRangeUnits, entity.Handle))
				ids.Add(near.Handle);

			return ids;
		}

		// Starts the simulation tick on the given lifetime token (owned by the listener so startup/shutdown stay in one
		// place). The task is held so StopAsync can await a clean exit.
		// `output` arrives here rather than in the ctor - see the _output field for why. Start is called from the
		// listener's StartAsync, long after every collaborator exists, and nothing touches the port before it.
		public void Start(CancellationToken cancellationToken, IWorldOutbound output)
		{
			BindOutbound(output);
			_playerDeaths.BindOutbound(output);
			_tickTask = RunTickLoop(cancellationToken);
		}

		// The binding on its own, for tests that drive tick methods directly instead of running the loop
		// (InternalsVisibleTo Faolan.Tests is global). Production goes through Start.
		internal void BindOutbound(IWorldOutbound output)
		{
			_output = output;
			_playerDeaths.BindOutbound(output);
		}

		// Awaits the tick loop's (cancelled) exit. The loop swallows its own OperationCanceledException, so this
		// completes normally once the lifetime token has been cancelled by the listener.
		public Task StopAsync()
		{
			return _tickTask;
		}

		// Applies an aggroed NPC's swing to the target player: play the decoded attack-animation ack
		// (SendNpcAttackAnimation), then reduce the player's live vitals and push a 0x96B8DC59 update so their HP bar
		// drops.
		private async Task ApplyNpcAttack(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			// #401: the NPC's swing is physical, so the player's worn-gear armor rating mitigates it. LEVEL and STRENGTH
			// are part of the client's own curve - it normalises armour against a per-level target and adds Strength at
			// 0.1/point above 10 - so both are passed rather than mitigating on the raw rating alone.
			// The curve wants the attribute at the ×10 WIRE scale (its `(attribute - 10) / 10` term converts back to
			// display units), so go through AttributeWireScore rather than the display score.
			var damage = CombatResolver.ApplyPhysicalMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level, npc.RangedAttacker), target.ArmorRating, character.Level,
				target.AttributeWireScore(CharacterStatId.Strength));
			// #621: the player's shield rose finally does something in PvE. The direction is the zone the MOB SWINGS
			// FROM - the same five the player's own direction abilities pick - not where the mob is standing. The
			// player defender takes the gentler Combat_PvP* column, matching CombatService's rule, and it is applied
			// AFTER armour, like the player-swing path.
			//
			// ⚠️ DRAWN ONCE, spent twice (#673). The swing's direction is on the wire - the hit result's type word IS
			// it - so the ack below has to state the same zone the mitigation charged for. Two draws made the packet
			// and the damage disagree on every swing. The MITIGATION only sees the guard arc (three), the WIRE gets
			// the full zone (five), which is why the draw is the zone and the arc falls out of it.
			var direction = CombatShieldRose.RollSwingDirection(Random.Shared);
			damage = (int)CombatShieldRose.ApplyArcMitigation((uint)damage, target.ShieldDistribution, CombatShieldRose.ArcOf(direction), playerVersusPlayer: true);
			// Play the NPC's swing animation (the decoded attack-animation ack) BEFORE the HP move lands, so the
			// hit reads as "swing -> damage". Visual only; the authoritative health change is the PushVitals below.
			// The swing ALWAYS plays - .godmode mobs still visibly attack, they just deal no damage.
			// #623: retail puts a combo on 427 of 1,504 captured NPC melee acks, so the mob combos on some swings and
			// swings plain on the rest. Rolled per swing rather than cycled, because the corpus shows no ordering -
			// a mob alternates its generic and class combos with plain hits in no pattern. 0 = plain.
			// #675: and when it does, a mob whose weapon answers its archetype swings a CLASS row about as often as
			// its generic one (the corpus splits 92/65 over the two wired weapons). Which rank is drawn from the rows
			// its level allows - retail's level-75 mobs still swing the rank-1 row, so there is no "highest wins".
			//
			// A SHOT takes the other branch entirely (#676): there is no `combo_npc_generic_bow` row, so an archer
			// either carries a ranger row or swings plain, at the lower rate retail shows for ranged (13.6% against
			// melee's 28.4%).
			var classCombo = npc.ClassComboIds.Length > 0
				? npc.ClassComboIds[Random.Shared.Next(npc.ClassComboIds.Length)]
				: 0;

			uint comboId;
			if (npc.RangedAttacker)
			{
				comboId = classCombo != 0 && Random.Shared.NextDouble() < NpcWeapon.RangedComboShare
					? classCombo
					: 0;
			}
			else
			{
				comboId = npc.MeleeComboId != 0 && Random.Shared.NextDouble() < NpcWeapon.MeleeComboShare
					? npc.MeleeComboId
					: 0;
				if (comboId != 0 && classCombo != 0 && Random.Shared.NextDouble() < NpcWeapon.ClassComboShare)
					comboId = classCombo;
			}

			await _output.SendNpcAttackAnimation(client!, npc.Handle, (uint)damage, ++npc.AttackSwingCounter, direction, comboId, npc.RangedAttacker);

			if (target.GodMode) // .godmode: the NPC swings and aggros normally, but the hit deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}.", character.Name, npc.Name);
		}

		// Retail throws a rider off their mount under fire (#142): a mounted player who soaks enough damage gets the
		// ride buff's end-twin with no press of their own. The damage accumulates on the entity rather than testing a
		// single hit because the corpus rules a per-hit threshold out - a 200,496 hit leaves the rider seated while a
		// 10,944 one throws them - so the meter is the shape that survives the evidence even though its CEILING is a
		// placeholder (MountCatalog.ThrowOffDamage, #653).
		private async Task ThrowFromMountIfBattered(WorldEntity target, int damage)
		{
			if (!target.IsMounted || damage <= 0)
				return;

			target.MountDamageTaken += damage;
			if (target.MountDamageTaken < MountCatalog.ThrowOffDamage)
				return;

			_logger.LogInformation("Mount: {Player} was thrown from their mount after {Damage} damage.", target.Client?.Session.Character?.Name, target.MountDamageTaken);
			await _output.SendDismount(target.Client!);
		}

		// A caster NPC's tick action (#232): cast a spell at the target player (ApplySpell from the NPC handle) and
		// apply its damage, mirroring ApplyNpcAttack. The spell + effect payload come from the NPC's NpcSpell rows
		// (entity.Spells, loaded at zone load); a caster cycles through its spells. Damage is the provisional
		// melee-scaled value for now - the captured payload's magnitude isn't decoded, same interim as buffs.
		private async Task ApplyNpcCast(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null || npc.Spells.Count == 0)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			var spell = npc.Spells[(int)(npc.AttackSwingCounter % (uint)npc.Spells.Count)];
			var debuffMs = ActiveBuff.DurationMsFromPayload(spell.Payload);

			// Retail re-applies an NPC debuff only AFTER the prior one expires (the combat capture's ~7s
			// apply->end->re-apply cycle); the swings in between are NORMAL attacks. Re-sending the ApplySpell every 2s
			// swing makes the client STACK the icon (piling up new copies at 0s with no countdown). So if the target
			// already carries this debuff, swing normally this tick instead - the debuff keeps counting down and is
			// re-cast once the tick expires it. (#274)
			if (debuffMs > 0 && target.ActiveBuffs.ContainsKey(spell.SpellId))
			{
				await ApplyNpcAttack(npc, target);
				return;
			}

			npc.AttackSwingCounter++;
			// #401: an NPC cast is magic damage, so the player's worn-gear Protection mitigates it (the magical mirror
			// of armor mitigating melee) - same level-normalised curve, against TWK_TargetPerLevelProtection.
			// NOT `npc.RangedAttacker`: #671's factor was measured on melee HITS (DamageResultIIR), and it is the
			// mob's ARROW that lands soft, not its spell. A bow-carrying caster would otherwise have its cast nerfed
			// for holding a bow.
			var damage = CombatResolver.ApplyMagicMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level), target.Protection, character.Level,
				target.AttributeWireScore(CharacterStatId.Intelligence)); // the curve wants the ×10 wire scale
			await _output.SendNpcSpellCast(client!, npc.Handle, spell);

			// Track the NPC's cast as a timed DEBUFF on the player so it counts down and clears its icon at 0, like a
			// self-buff (#274). The duration is the cast payload's +8 field (centiseconds, see DurationMsFromPayload).
			// Only a real effect (duration > 0) is tracked - an instant damage spell carries none and leaves no icon.
			// CasterHandle = this NPC so the tick's removal twin mirrors the apply (applyTo=player, applyFrom=NPC).
			if (debuffMs > 0)
			{
				// Attach the DoT effect (Spell.DotDelta, captured StatDamageIIR) so the tick bleeds the player's health
				// while the debuff is active - Rend -8/tick etc. Looked up by base spell id (wire id >> 8). #274
				var (dotDelta, dotType, dotIntervalMs) = await DotEffect(spell.SpellId >> 8);
				target.ActiveBuffs[spell.SpellId] = new ActiveBuff(spell.SpellId, npc.Handle, DateTimeOffset.UtcNow, debuffMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = DateTimeOffset.UtcNow
				};
			}

			if (target.GodMode) // .godmode: the NPC still casts, but the spell deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}'s spell.", character.Name, npc.Name);
		}

		private async Task<(int Delta, uint Type, uint IntervalMs)> DotEffect(uint baseSpellId)
		{
			if (_dotEffects.TryGetValue(baseSpellId, out var cached))
				return cached;

			var spell = await _database.GetSpell(baseSpellId);
			var effect = (DisplayDotDelta(spell?.DotDelta ?? 0), spell?.DotType ?? 0, spell?.DotIntervalMs ?? 0);
			_dotEffects[baseSpellId] = effect;
			return effect;
		}

		// `Spell.DotDelta` is a captured `StatDamageIIR` delta, so it is a WIRE number - and everything it meets at
		// this layer is DISPLAY: `EntityVitals.Health` is the ÷100 vitals value, the melee path spends the already
		// converted `CombatResolver.ResolveNpcAttackDamage`, and `SendStatDamage` reports the delta beside that same
		// display `vitals.Health`. This is #572's unit mismatch one path over, and it bites harder: melee was a curve
		// we could reason about, while a DoT is a flat captured constant. `Rend` (3236937) ticks -54 every 1.5s, so a
		// level-1 Scavenger Lackey bled a level-1 player's 88-point pool dry in three ticks - the "level-1 mob kills a
		// level-1 player" report that #572 was filed for, still reproducing through the buff path after the melee half
		// was fixed. The corpus goes to -258,352 (`Chill (Rank 5)`), which no display pool survives at all.
		//
		// Converted HERE, at the one place a spell row becomes a live effect, rather than at the tick: the value then
		// reaches BOTH the health application and the wire already in display units, so the two cannot drift apart.
		// The stored column stays in wire units for the same reason #572 left its curve alone - the captured evidence
		// is wire, and rewriting it would make the provenance unreadable.
		//
		// Magnitude is floored at 1 rather than truncated, so a small DoT still does something: `Rend`'s -54 is 0 under
		// integer division, and a debuff that ticks for nothing would read as "DoTs are broken" instead of "scaled".
		// Sign is preserved - a positive delta is a heal-over-time, which floors at +1 by the same rule.
		private static int DisplayDotDelta(int wireDelta)
		{
			if (wireDelta == 0)
				return 0;

			var scaled = wireDelta / (int)CharacterStatId.HealthMax.GetScale();

			return scaled != 0 ? scaled : Math.Sign(wireDelta);
		}

		private async Task<MapRegion[]> GetMapAreasCached(uint mapId)
		{
			if (_mapAreas.TryGetValue(mapId, out var cached))
				return cached;

			var areas = await _database.GetAreaBannerRegions(mapId);
			_mapAreas[mapId] = areas;
			return areas;
		}

		// Regenerates each connected player's vitals by `elapsedSeconds` worth and pushes a 0x96B8DC59 update only when
		// a pool actually rose (a player at full HP/stamina/mana costs nothing). The rates are ATTRIBUTE-driven and
		// corpus-verified (RegenRates, #442) - they replaced flat fractions of the pool max, which retail does not use.
		private async Task RegenerateConnectedPlayers(double elapsedSeconds)
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character, pair.Value.GearBonuses, pair.Value.ClassBase);
				if (vitals.IsDead)
					continue; // corpses don't regen - the respawn timer revives them

				// HP + mana regen is OUT-OF-COMBAT only (retail's OOCHealth/ManaRegeneration): halt them while the player
				// has dealt/taken damage within CombatRegenPause. Stamina (the action resource) keeps regenerating.
				var entity = pair.Value;
				var inCombat = (DateTimeOffset.UtcNow - entity.LastCombatAt).TotalSeconds < CombatRegenPause;
				uint beforeH = vitals.Health, beforeM = vitals.Mana, beforeS = vitals.Stamina;

				// Retail's rates, from the character's attribute scores (RegenRates: the tweak table's own 0.15/0.38
				// modifiers, each confirmed against a captured RegenIIR delta at ×100 precision). Stamina keeps its
				// worn-gear Natural Stamina Regen on top - that item stat (0x385) is additive to the OOC rate.
				uint strength = entity.AttributeScore(CharacterStatId.Strength),
					constitution = entity.AttributeScore(CharacterStatId.Constitution),
					dexterity = entity.AttributeScore(CharacterStatId.Dexterity),
					intelligence = entity.AttributeScore(CharacterStatId.Intelligence),
					wisdom = entity.AttributeScore(CharacterStatId.Wisdom);
				var changed = vitals.Regenerate(inCombat
						? 0u
						: RegenRates.Gain(RegenRates.HealthPerSecond(constitution, strength), elapsedSeconds),
					RegenRates.Gain(RegenRates.StaminaPerSecond(constitution, strength, dexterity), elapsedSeconds) + (uint)(entity.NaturalStaminaRegen * elapsedSeconds), inCombat
						? 0u
						: RegenRates.Gain(RegenRates.ManaPerSecond(intelligence, wisdom, entity.NonCombatManaRegenBase), elapsedSeconds) + (uint)(entity.NaturalManaRegen * elapsedSeconds));
				if (!changed)
					continue;

				// Retail's compact RegenIIR delta (0x28D0AC99): the per-tick rise per pool, ×100 to the wire vitals
				// scale. The client's apply (FUN_0079dd10) reads stat -> adds the delta -> writes it back through the
				// SAME setter StatUpdateIIR's apply uses (entity+0x10c vtable +0xc), so this is retail's own channel
				// for moving the bars without a full stat block.
				await _output.SendRegen(client, entity.Handle, (int)(vitals.Health - beforeH) * 100, (int)(vitals.Mana - beforeM) * 100, (int)(vitals.Stamina - beforeS) * 100);
				// ...and then the AUTHORITATIVE 0x96B8DC59 block, which is what actually makes regen visible (#2, the
				// in-client-verified channel). Sending the delta ALONE was a regression: 2026-07-22 replaced this push
				// with the RegenIIR and regen stopped ticking in-client, even though the packet is byte-identical in
				// shape to all 1502 captured retail RegenIIRs (len 29, env 0, class 0xC350, three BE i32). Order
				// matters: the delta goes FIRST so the absolute value lands last and wins - the client can never
				// double-apply, and any client/server drift is corrected every tick. Retail is lighter (it pins a pool
				// with a StatUpdate only on the tick that tops it off - Hoxxling_mage-heraldofxotli_login_gameplay
				// 18:49:49.912 sends REGEN(2060,1992,882) + a StatUpdate setting stamina/mana to their max); thinning
				// ours down to that shape needs the delta channel proven in-client first (Gitea #2).
				await _output.PushVitals(client, vitals);
			}
		}

		// The server game loop (Gitea #404): a 10 Hz base tick that advances continuous systems
		// every tick with the REAL elapsed dt (NPC respawn/AI, player death timers, boundary-crossing tests) and runs
		// rate-based systems (vitals regen, ambient pools) via a TickAccumulator at their own interval, dt-scaled.
		// Wall-clock dt is measured so a late tick doesn't desync the rates. Cancelled on shutdown.
		//
		// Hang a system where its CADENCE belongs, not on whatever block already exists: a crossing test has to sample
		// fast to catch a boundary, a rate-based one must not. Bolting the two together silently handed discovery and
		// the area banners the regen interval as their sampling rate, which is a bug wearing a tick's clothes.
		private async Task RunTickLoop(CancellationToken cancellationToken)
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BaseTickSeconds));
			var regen = new TickAccumulator(RegenIntervalSeconds);
			var ambientMaintain = new TickAccumulator(AmbientMaintainIntervalSeconds);
			var interestSweep = new TickAccumulator(InterestSweepIntervalSeconds);
			var healthReport = new TickAccumulator(SlowTickReportSeconds);
			var lastTimestamp = Stopwatch.GetTimestamp();
			var consecutiveFailures = 0;
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var now = Stopwatch.GetTimestamp();
					var dt = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds;
					lastTimestamp = now;

					// ONE BAD TICK MUST NOT KILL THE WORLD. This try used to sit outside the while, so any exception
					// from any tick ended the loop for the process lifetime - and silently: the listeners keep
					// accepting clients while nothing moves, regenerates, respawns, aggroes or discovers anything. A
					// transient SQLite "database is locked" from DiscoverRespawnPoint was enough to do it (a stray
					// concurrent reader is enough to produce one), and the only trace was a single log line.
					try
					{
						// Continuous-time systems: every base tick, with the actual elapsed dt. The tick returns the
						// wire-visible effects (NPC respawns, loot expiry, NPC attacks) for us to broadcast.
						foreach (var effect in World.Tick(dt))
						{
							// A dead NPC must emit NO live broadcast (move / patrol / follow / heartbeat / chatter / attack /
							// combat-mode). A move effect generated the tick it died - or one processed across an await after
							// the player's kill ran OnNpcKilled and sent the death-pose - would otherwise override that
							// death-pose and walk / re-animate the corpse, so a patrolling NPC appears to walk off and vanish
							// instead of dying in place. Only the corpse-lifecycle effects (linger->loot, respawn, fade) apply
							// once dead.
							if (effect.Entity.IsDead && effect.Type is not (WorldTickEffectType.NpcCorpseLootDropped or WorldTickEffectType.NpcCorpseFaded or WorldTickEffectType.NpcCorpseQuit
								    or WorldTickEffectType.NpcCorpseRemoved or WorldTickEffectType.NpcRespawned or WorldTickEffectType.LootExpired))
								continue;

							switch (effect.Type)
							{
								case WorldTickEffectType.NpcRespawned:
								{
									await _output.BroadcastNpcRespawn(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcCorpseLootDropped:
								{
									await _output.DropCorpseLoot(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcCorpseFaded:
								{
									await _output.BroadcastCorpseFade(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcCorpseQuit:
								{
									await _output.BroadcastEntityDespawn(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcCorpseRemoved:
								{
									// The fade + quit have already gone out (the two stages before this one), so the
									// entity is off every client's screen; this only drops it from the model.
									World.Remove(effect.Entity);
									break;
								}

								case WorldTickEffectType.LootExpired:
								case WorldTickEffectType.TimedDespawn: // a summon's lifetime ran out (#140) - same teardown
								{
									await _output.BroadcastEntityFade(effect.Entity); // graceful fade-out (n3ToClientFade)
									World.Remove(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcAttack:
								{
									// Gated by NpcAutoAttackEnabled (the once-feared respawn/world-re-entry death-loop on a
									// dense zone); the swing now plays the decoded attack-animation ack (see ApplyNpcAttack).
									if (NpcAutoAttackEnabled)
										await ApplyNpcAttack(effect.Entity, effect.Target!);
									break;
								}

								case WorldTickEffectType.NpcCast:
								{
									if (NpcAutoAttackEnabled)
										await ApplyNpcCast(effect.Entity, effect.Target!);
									break;
								}

								case WorldTickEffectType.NpcPatrol:
								{
									await _output.BroadcastNpcPatrol(effect.Entity); // idle patrol - not gated on the combat sim
									break;
								}

								case WorldTickEffectType.NpcFollow:
								{
									// #827: bound to the LEADER, which is what makes the client walk her rather than
									// interpolate a bare segment and be re-anchored. Falls back to the plain step if the
									// leader is somehow gone by the time the effect is drained.
									if (effect.Target is { } leader)
										await _output.BroadcastNpcFollow(effect.Entity, leader);
									else
										await _output.BroadcastNpcMove(effect.Entity);

									break;
								}

								case WorldTickEffectType.NpcMove:
								{
									if (NpcAutoAttackEnabled)
										await _output.BroadcastNpcMove(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcEnterCombat:
								case WorldTickEffectType.NpcExitCombat:
								{
									// Draw (enter) or sheathe (exit) the NPC's weapon for nearby viewers - #218. Gated with the
									// rest of the combat sim since combat mode only has meaning alongside the auto-attack swing.
									if (NpcAutoAttackEnabled)
									{
										await _output.BroadcastNpcCombatMode(effect.Entity, effect.Type == WorldTickEffectType.NpcEnterCombat);
										// On engage, also push the aggro so the threat/hate is visible (#216).
										if (effect is { Type: WorldTickEffectType.NpcEnterCombat, Target: not null })
											await _output.BroadcastNpcAggro(effect.Entity, effect.Target);
									}

									break;
								}

								case WorldTickEffectType.NpcShieldRose:
								{
									// #20 directional combat: the defender's blue guard arcs. Gated with the combat sim - the
									// rose only means anything when the NPC is actually fighting back.
									if (NpcAutoAttackEnabled)
										await _output.BroadcastShieldRose(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcStopped:
								{
									// The NPC arrived home and stopped; close its move with a StopIIR (#190).
									if (NpcAutoAttackEnabled)
										await _output.BroadcastNpcStop(effect.Entity);
									break;
								}

									case WorldTickEffectType.NpcAmbientAnimation:
								{
									// #766: the ambient idle animation - a citizen leaning on a wall rather than standing
									// to attention. Cosmetic, so ungated like the heartbeat above; the tick already picked
									// which animation and how long it runs.
									if (NpcAmbientAnimationEnabled)
										await _output.BroadcastNpcAmbientAnimation(effect.Entity);
									break;
								}

							case WorldTickEffectType.EncounterBark:
								{
									// #502: the lure calls for help as a player closes. Cosmetic - it is the swap that
									// matters - but it is the tell that the encounter is armed, and it is a real captured
									// line (its `event(2)` channel is what marks these apart from ambient chatter).
									var lure = effect.Entity;
									if (lure.EncounterLines is not { Length: > 0 } barkLines)
										break;

									var heard = new HashSet<uint>();
									foreach (var near in World.ZoneForEntity(lure).PlayersInRange(lure.Position, SayRangeUnits, lure.Handle))
										heard.Add(near.Handle);
									if (heard.Count > 0)
										await _agents.NpcBark(lure.Handle, barkLines[Random.Shared.Next(barkLines.Length)], heard);
									break;
								}

								case WorldTickEffectType.EncounterTrigger:
								{
									// The trap springs: the lure is replaced by its boss on the same spot.
									await _output.SwapEncounterLureForBoss(effect.Entity);
									break;
								}

								case WorldTickEffectType.EncounterReset:
								{
									// The boss's corpse has cleared - put the lure back so it can fire again.
									await _output.RestoreEncounterLure(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcChatter:
								{
									// Ambient bark: the NPC local-says a random line from ITS OWN type's set (chat server
									// CharacterChat 0x0022) to the players in say-range - purely cosmetic. Sender = the NPC
									// handle so the client shows the bubble over it.
									//
									// The lines are per TEMPLATE, never a shared pool: six types carry any, each with the
									// client's own group - `RenownCrier` is Tatianus the Crier alone, so his "consider joining
									// a guild" line cannot come out of anyone else. (A group MAY span types where retail shares
									// one: the Nobleman and one Noblewoman both bark `RenownBystanderLovers`. That is the
									// client's grouping, not ours.)
									var npc = effect.Entity;
									if (npc.ChatterLines is not { Length: > 0 } lines)
										break;

									var recipients = new HashSet<uint>();
									foreach (var near in World.ZoneForEntity(npc).PlayersInRange(npc.Position, SayRangeUnits, npc.Handle))
										recipients.Add(near.Handle);
									if (recipients.Count > 0)
										await _agents.NpcBark(npc.Handle, lines[Random.Shared.Next(lines.Length)], recipients);
									break;
								}

								case WorldTickEffectType.NpcGreeting:
								{
									// #694: the crier noticed a player ARRIVE and greets them. Not the ambient tier - the cue
									// is the arrival, and the channel is `sendtell`, so it goes to THAT PLAYER ALONE rather
									// than everyone in earshot. That is also what makes the addressee slot coherent: the line
									// is "…, {DetectedChar.name}!", and there is exactly one name it can carry.
									var greeter = effect.Entity;
									if (effect.Target is not { } greeted || greeter.GreetingLines is not { Length: > 0 } greetings)
										break;

									await _agents.NpcBark(greeter.Handle, greetings[Random.Shared.Next(greetings.Length)], [greeted.Handle]);
									break;
								}
							}
						}

						// #678: the staged visual sequence a harvested world object plays - fade, mesh swap, collision
						// drop, and the regrowth ten seconds later. Retail paces these against the CLIENT's own fade,
						// so they are timed steps rather than one burst.
						foreach (var step in World.TickVisualSequences(dt))
							await _output.PlayDynelVisualStep(step);

						// Player death: push the release screen once; respawn is click-driven (RespawnMe), not timed.
						await _playerDeaths.TickPlayerDeaths();
						await TickKillChains();
						await TickBuffExpiry();
						await TickDots();
						await TickAuras();
						await TickNpcBuffs(); // #776: the same three jobs - expire, bleed, emit - for buffs sitting on a MOB
						await _output.CompleteDueTradeskills(); // #793: a craft/socket whose ~5 s retail bar has run out

						// Boundary CROSSING tests: every base tick (10 Hz). Both used to hang off the regen accumulator
						// below, which has nothing to do with either and sampled them at 1 Hz - a walking player covers
						// several units between samples, so both fired late, well inside the boundary. In-client that read
						// as having to stand on top of a resurrection point before it registered; the radius was never the
						// problem. Cheap enough to run hot: the points/areas and the per-player discovered set are all
						// cached, so a tick is a handful of distance checks per player, and only an actual discovery
						// touches the DB.
						await _playerDeaths.DiscoverNearbyRespawnPoints();
						await TickAreaBanners();

						// Rate-based systems: fire on their own cadence, scaled by the time actually elapsed since last fire.
						if (regen.Advance(dt, out var regenElapsed))
							await RegenerateConnectedPlayers(regenElapsed);

						// #778: the interest pass nobody else drives - the one for a player who is standing still while the
						// world walks past them. Every other pass is triggered by the PLAYER moving, entering or changing
						// quests, so without this an NPC that walked out of range stayed frozen on their screen for ever
						// and one that walked in never appeared.
						if (interestSweep.Advance(dt, out _))
						{
							foreach (var client in Players.Keys)
								await _output.ResolveInterest(client);
						}

						// #363: keep the ambient spawn-group pools at strength (respawn killed members). No-op when disabled.
						if (ambientMaintain.Advance(dt, out _))
							await _output.MaintainAmbientPools();

						consecutiveFailures = 0;
					}
					catch (OperationCanceledException)
					{
						throw; // shutdown - let the outer handler end the loop
					}
					catch (Exception ex)
					{
						// Log the first failure and then back off geometrically: a systemic fault (a broken query, a
						// permanently locked DB) would otherwise write a stack trace 10x a second, and the flood is
						// what buries the first one - the only line that says what actually broke.
						consecutiveFailures++;
						if (consecutiveFailures == 1 || consecutiveFailures % 100 == 0)
							_logger.LogError(ex, "Game tick failed ({Count} consecutive); the world keeps ticking.", consecutiveFailures);
					}

					// Measure the tick's WORK (start-of-work -> here), not the period - the period is the timer's and
					// says nothing about load. A failed tick is still a tick and is still measured: a loop that throws
					// instantly would otherwise report a perfectly healthy rate while doing nothing.
					Metrics.RecordTick(Stopwatch.GetElapsedTime(now).TotalSeconds);

					// Surface a loop that is not keeping up instead of leaving it to be noticed as "the world feels
					// laggy". Throttled to one line per SlowTickReportSeconds - the condition is by definition
					// persistent, so logging it per tick would bury it.
					if (healthReport.Advance(dt, out _))
					{
						var health = Metrics.Current;
						if (health.TicksPerSecond > 0 && health.TicksPerSecond < Metrics.NominalTicksPerSecond * SlowTickThreshold)
							_logger.LogWarning("World tick is behind: {Rate:F1} of a nominal {Nominal:F1} Hz ({Busy:P0} of the tick budget in use).",
								health.TicksPerSecond, Metrics.NominalTicksPerSecond, health.BusyFraction);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// server shutting down
			}
			catch (Exception ex)
			{
				// Only a failure of the TIMER itself reaches here now - a per-tick fault is caught above.
				_logger.LogError(ex, "Game tick loop terminated.");
			}
		}

		// #348: floats the area banner when a player CROSSES into a named sub-area. A map has many - Old Tarantia
		// fires seventeen - so this is a per-tick position test against the playfield's banner volumes, not a
		// per-playfield event; entering a playfield lands you in an area and that is simply the first crossing.
		//
		// The volumes are the CLIENT'S OWN (#513/#679/#680): exact concave polygons with a real height band, tested
		// with NO slack. They used to be a hull over the positions retail was seen firing each banner at, widened by a
		// slack radius because a hull can only cover ground a captured player actually walked - so banners fired early
		// or late at the edges, and areas nobody had walked stayed silent. That is gone, and with it the guesswork:
		// the server now fires on the boundary the client draws.
		//
		// The SMALLEST containing volume wins. The client's regions nest and overlap - a ward inside a district - and
		// the tighter one is the more specific claim.
		private async Task TickAreaBanners()
		{
			foreach (var (client, entity) in Players)
			{
				var areas = await GetMapAreasCached(entity.MapId);
				if (areas.Length == 0)
					continue;

				MapRegion? best = null;
				var bestVolume = double.MaxValue;
				foreach (var area in areas)
				{
					if (!EnvironmentService.Contains(area, entity.Position))
						continue;

					var volume = (double)(area.MaxX - area.MinX + 1) * (area.MaxZ - area.MinZ + 1);
					if (volume >= bestVolume)
						continue;

					bestVolume = volume;
					best = area;
				}

				var now = best?.AreaNameId ?? 0;
				if (now == entity.CurrentAreaNameId)
					continue; // still in the same area (or still in none) - a banner per tick would spam

				entity.CurrentAreaNameId = now;
				if (now == 0)
					continue; // left an area into unnamed ground - retail shows nothing

				await _output.SendAreaNameBanner(client, now);
				// Logged by id AND by the client's own name for the volume: the wire carries only the id (the client
				// renders the text from its own LDB), and the region name is what makes a line readable without a
				// lookup. 3373519 is "Bridge of Vilerus I" if you need to read one by hand.
				_logger.LogInformation("World: {Player} entered area {Id} ('{Region}') on map {Map}.", client.Session.Character.Name, now, best!.Name, entity.MapId);
			}
		}

		// Advances player death/respawn. A player whose vitals hit 0 starts a respawn countdown; when it elapses they
		// are revived to full and re-entered at the closest known resurrection point (RespawnPlayer). Living players are
		// untracked.
		// Drops expired buffs (those with a known duration, now imported from RDB field 0x008 into Spell.DurationMs and
		// armed at apply time, #274) and tells the client by sending the retail end-twin removal (SendBuffRemoval). The
		// client does NOT self-expire the icon - the combat captures show retail explicitly removes each timed buff
		// with the rank-1 empty-payload twin - so without this the icon counts to 0 and sticks.
		// #446: close a lapsed kill chain. Retail pairs every non-zero ChainKillIIR with a 0 about 9s later - 32 of 32
		// in the corpus - so the chain has a visible END, and without this the client would keep the last count on
		// screen forever. The kill side is CombatService.AnnounceKillChain; the window is
		// WorldEntity.ChainKillWindowSeconds, measured.
		private async Task TickKillChains()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				if (!pair.Value.TryLapseKillChain(now))
					continue;

				await _output.SendKillChain(pair.Key, pair.Value.Handle, 0);
			}
		}

		private async Task TickBuffExpiry()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				// Decide and remove in one snapshot pass BEFORE sending anything (WorldEntity.TakeExpiredBuffs). The old
				// shape - TryRemove inside a foreach over the live ConcurrentDictionary, with awaits in the body - could
				// skip a buff whose bucket neighbour had just been removed, so several buffs expiring in the same tick
				// left some behind with their icon stuck at 0s.
				var expired = pair.Value.TakeExpiredBuffs(now);
				if (expired.Count == 0)
					continue;

				// Log the BATCH before sending any of it: "several expired together and only some icons cleared" is the
				// reported failure mode, so the count here vs the per-buff lines below says immediately whether the tick
				// missed one (server side) or sent all of them and the client kept an icon (client side).
				if (expired.Count > 1)
					_logger.LogInformation("Buffs: {Count} expired together on {Player} ({Ids}).", expired.Count, pair.Key.Session.Character?.Name, string.Join(", ", expired.Select(b => $"0x{b.Key:X8}")));

				foreach (var buff in expired)
				{
					_logger.LogInformation("Buff 0x{Buff:X8} expired on {Player}.", buff.Key, pair.Key.Session.Character?.Name);
					// Mirror the apply: applyTo = the affected player, applyFrom = the caster (self for a self-buff,
					// the NPC for an NPC-cast debuff). CasterHandle carries the applyFrom (#274).
					await _output.SendBuffRemoval(pair.Key, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
				}

				// #367: the buffs' stat deltas (if any) come off with them. ONE sync after the batch - it recomputes
				// `gear + all remaining buffs` from scratch, so per-buff syncs were redundant work pushing the same
				// end state several times.
				await _output.SyncBuffStats(pair.Key);
			}
		}

		// This DoT's own tick interval, or the 2 s fallback where the captures never showed one.
		private static double IntervalOf(ActiveBuff buff)
		{
			return buff.DotIntervalMs > 0
				? buff.DotIntervalMs
				: DotTickSeconds * 1000;
		}

		// #772: the AURA - a buff that re-applies an effect to everyone around its holder, every period, for as long
		// as it is up. `Gangrenous Stench` is the shape: the cast puts a 25 s buff on the CASTER, and that buff hits a
		// 7 m radius every 4 s. The maintainer's framing of the mechanic, which is what separates it from everything else here:
		// the spell applies an effect to SELF, and that effect applies to enemies nearby - where `Life Leech` applies
		// to enemies once, at cast, and is done.
		//
		// ⚠️ This is a mechanic the server had NO form of. `TickDots` ticks a DoT on the entity that OWNS the buff;
		// nothing walked a buff's radius applying an effect to OTHERS. So every aura in the game was inert - the buff
		// icon appeared and did nothing, which is exactly what Gangrenous Stench did.
		//
		// The period is per AREA, not per buff (a spell may state several with different periods), so the stamp is
		// keyed by both. `LastDotTick` is deliberately not reused: a spell can carry a DoT AND an aura, and sharing one
		// stamp would make each suppress the other's cadence.
		private async Task TickAuras()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				var entity = pair.Value;
				var client = pair.Key;

				var vitals = entity.Vitals;
				if (vitals == null || vitals.IsDead)
					continue;

				await EmitAuraAreas(entity, client, now);
			}

			// A buff that ended leaves its stamp behind; drop the ones whose buff is gone so this cannot grow without
			// bound across a long session. Mobs count too since #776 - an infected one emits its own areas.
			if (_auraTicks.Count > 0)
			{
				var live = Players.Values.Concat(_buffedEntities.Values).SelectMany(e => e.ActiveBuffs.Keys.Select(id => (e.Handle, id))).ToHashSet();
				foreach (var key in _auraTicks.Keys.Where(k => !live.Contains((k.Holder, k.BuffId))).ToList())
					_auraTicks.Remove(key);
			}
		}

		// The periodic half of one entity's buffs: for every buff it carries that states a periodic area, re-apply
		// that area if its period has elapsed. Shared by the player aura tick and the #776 mob tick, because the
		// mechanic does not care who holds the buff - `Gangrenous Stench` is a player self-buff whose area lands a
		// debuff on mobs, and THAT debuff states an area of its own that spreads from the mob it is sitting on.
		//
		// Each emission is attributed to the buff's own caster, so a mob carrying debuffs from two players emits each
		// one for the player who put it there. `holderClient` is the fallback for a buff whose caster is not a
		// connected player - the holder themselves, which is what a player carrying an NPC-cast area debuff wants,
		// and null for a mob (there the caster is the only credit there is).
		private async Task EmitAuraAreas(WorldEntity holder, INetworkClient? holderClient, DateTimeOffset now)
		{
			foreach (var buff in holder.ActiveBuffs.Values.ToArray())
			{
				// The buff id is the WIRE form ((base << 8) | rank); the catalog is keyed by the base.
				var areas = await _spellAreas.AuraAreas(buff.BuffId >> 8);
				if (areas.Count == 0)
					continue;

				var client = ClientForHandle(buff.CasterHandle) ?? holderClient;
				if (client == null)
					continue;

				foreach (var area in areas)
				{
					var key = (holder.Handle, buff.BuffId, area.Sequence);
					if (_auraTicks.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < area.PeriodCentiseconds * 10)
						continue;

					_auraTicks[key] = now;

					// God mode blocks harm, and an aura is harm the holder is emitting rather than taking - but the
					// same reasoning as TickDots applies to the RECIPIENTS, and ApplySpellArea goes through
					// ApplyNpcDamage, which owns that check for the targets. Nothing extra is needed here.
					// ⚠️ Logged even when it reaches NOTHING. Guarding this on `hit > 0` made a tick that found no
					// targets indistinguishable from a tick that never ran, and that is exactly the question asked
					// of it first ("the aura only fired twice" - it had fired six times and killed everything in
					// range by the third).
					var hit = await ApplyAreaEffect(client, holder.Handle, buff.CasterHandle, area);
					_logger.LogDebug("Aura from buff 0x{Buff:X8} on 0x{Holder:X8} applied {Effect} to {Hit} target(s) within {Radius}m.", buff.BuffId, holder.Handle,
						area.EffectSpellId, hit, area.RadiusMetres);
				}

				// ⭐ #786: a `CastNano` chain edge can state a cadence too, and 263 of them do. It is NOT an area - it
				// re-runs on the HOLDER, with no radius - so it shares the tick and the `_auraTicks` stamp (the key
				// carries the row's own Sequence, so the two kinds cannot collide) but not `ApplyAreaEffect`.
				//
				// ⚠️ Only the `Repeats == -1` half is here. The other 220 interval-carrying edges state `Repeats == 1`,
				// which runs ONCE after a delay, and a delay needs a one-shot timer this tick does not have; running
				// them here would turn a single delayed cast into a permanent cadence, which is the over-apply #784
				// refused to guess at.
				foreach (var edge in await _spellAreas.RepeatingChains(buff.BuffId >> 8))
				{
					var key = (holder.Handle, buff.BuffId, edge.Sequence);
					if (_auraTicks.TryGetValue(key, out var lastChain) && (now - lastChain).TotalMilliseconds < edge.PeriodCentiseconds * 10)
						continue;

					_auraTicks[key] = now;

					// The same re-apply guard as every other landing: a holder still carrying the chained effect keeps
					// the live one counting down rather than re-asserting it, which is retail's own "re-apply only
					// after the previous has ended" (#274). So the cadence is a REFRESH schedule, not a stack.
					await ApplyEffectToEntity(client, holder.Handle, buff.CasterHandle, edge.EffectSpellId);
					_logger.LogDebug("Chain from buff 0x{Buff:X8} on 0x{Holder:X8} re-ran {Effect} (every {Period} cs).", buff.BuffId, holder.Handle,
						edge.EffectSpellId, edge.PeriodCentiseconds);
				}
			}
		}

		// #776: apply one of a spell's AREA elements WHOLE - the damage to everything in range, and then the effect
		// itself on each of them. `ApplySpellArea` used to be the entire operation and it only ever dealt the damage,
		// which is why `Gangrenous Stench` hit for its initial number and then did nothing: the 9.5 s effect record
		// the area names never landed on anybody, so it had no DoT to bleed, no stat deltas to apply, and its own
		// 4 m/3 s area was never reached.
		//
		// `sourceHandle` is what the area radiates from (the buff's holder); `casterHandle` is who gets the credit
		// and is the player for the whole chain, even where the emitter is an infected mob - a bleed that finishes a
		// mob has to name a killer the XP/loot lifecycle can pay.
		public async Task<int> ApplyAreaEffect(INetworkClient client, uint sourceHandle, uint casterHandle, SpellAreaEffect area)
		{
			var victims = await _output.ApplySpellArea(client, sourceHandle, area.EffectSpellId, area.RadiusMetres, area.MaxTargets);
			foreach (var handle in victims)
				await ApplyEffectToEntity(client, handle, casterHandle, area.EffectSpellId);

			return victims.Count;
		}

		// Land an effect spell on ONE non-player victim: the ApplySpell carrier so the client draws the icon on it,
		// and a tracked `ActiveBuff` so the tick can count it down, bleed its DoT and emit its own areas. The mirror
		// of what `ApplyNpcCast` does for a debuff an NPC puts on a player.
		private async Task ApplyEffectToEntity(INetworkClient client, uint victimHandle, uint casterHandle, uint effectSpellId, SpellChainWalk? chain = null)
		{
			if (PlayerWorldEntity(client) is not { } player || !World.ZoneForEntity(player).TryGet(victimHandle, out var victim) || victim.IsPlayer || victim.IsDead)
				return;

			// Seed the #784 chain walk with the record landing here, so an edge pointing back at it terminates. Null at
			// an original landing (an area applying its effect); non-null only when this call IS a chain edge.
			chain ??= new SpellChainWalk();
			chain.TryEnter(effectSpellId);

			var spell = await _database.GetSpell(effectSpellId);
			var durationMs = spell?.DurationMs ?? 0;
			var wireId = effectSpellId << 8;

			// A zero duration is an INSTANT: retail sends no ApplySpell for one, and one aborts the client's
			// ActivateBuff (#767). It still LANDED, so its chain runs below - the icon is what is skipped, not the
			// effect.
			if (durationMs > 0)
			{
				// Already carrying it: leave the live one counting down rather than re-sending the apply. Re-applying on
				// every period is what STACKS icons on the client (#274) - retail re-applies an effect only after the
				// previous one has ended - and the area's damage lands either way.
				//
				// ⚠️ The chain is skipped with it, deliberately: nothing NEW landed on this victim, so re-running the
				// edge would re-assert the chained debuff on every aura period.
				if (victim.ActiveBuffs.ContainsKey(wireId))
					return;

				var (dotDelta, dotType, dotIntervalMs) = await DotEffect(effectSpellId);
				var now = DateTimeOffset.UtcNow;
				victim.ActiveBuffs[wireId] = new ActiveBuff(wireId, casterHandle, now, durationMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = now
				};

				TrackBuffedEntity(victim);
				await _output.SendSpellApplication(client, victim.Handle, casterHandle, wireId, durationMs / 10);
				_logger.LogDebug("Effect {Effect} applied to {Npc} (0x{Handle:X8}) for {Duration}ms by 0x{Caster:X8}.", effectSpellId, victim.Name, victim.Handle, durationMs, casterHandle);
			}

			// #784: the `CastNano` chain - "this landed, so also run X" on the SAME victim, with no radius of its own.
			// `Gangrenous Stench`'s 221069 reaching `Physical Wrack` 3963058 is the headline case, and the reason the
			// spell's tooltip effect did nothing until now. Every edge runs ONCE here; the ones that state a CADENCE
			// re-run from the aura tick instead (#786, `EmitAuraAreas`).
			foreach (var link in await _spellAreas.ChainedSpells(effectSpellId))
			{
				// #813: the target's STACKING LINE is claimed with its id - ten edges into one line is a rank table,
				// not ten buffs. Same rule as the player-side walk in BuffService.
				if (!chain.TryEnter(link.EffectSpellId, await _spellAreas.StackingLineOf(link.EffectSpellId)))
					continue;

				_logger.LogDebug("Effect {Effect} chains {Chained} onto {Npc} (0x{Handle:X8}).", effectSpellId, link.EffectSpellId, victim.Name, victim.Handle);
				await ApplyEffectToEntity(client, victim.Handle, casterHandle, link.EffectSpellId, chain);
			}

			if (chain.Truncated)
				_logger.LogWarning("Effect {Effect}'s chain hit the {Cap}-application cap on {Npc} and was truncated.", effectSpellId, SpellChainWalk.MaxApplications, victim.Name);
		}

		// #776: the world tick for buffs on NON-PLAYER entities. `TickBuffExpiry`, `TickDots` and `TickAuras` all walk
		// `Players`, so a debuff a player put on a MOB had nothing to expire it, nothing to bleed it and nothing to
		// emit its own areas - it counted down on the client to 0 and stuck there for ever, which is exactly the
		// orphan-icon failure #274 fixed on the player side.
		//
		// Everything here is attributed to `buff.CasterHandle`'s player: if they have gone (logged out, changed
		// playfield), the buff is dropped rather than left ticking, because there is no client left to render its end
		// or to be paid for a kill it lands.
		// Registers a non-player entity whose `ActiveBuffs` the tick below has to service. The only writer is the
		// effect-application path above; it is a separate method so the registry stays private and so a test can put
		// an entity in front of one tick pass without a database behind it.
		internal void TrackBuffedEntity(WorldEntity entity)
		{
			_buffedEntities[entity.Handle] = entity;
		}

		internal async Task TickNpcBuffs()
		{
			if (_buffedEntities.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (handle, entity) in _buffedEntities.ToArray())
			{
				// Dead, despawned or clean: it leaves the registry. A corpse renders no icon, and a DoT that kept
				// ticking into a kill lifecycle that has already run would pay the XP twice.
				if (entity.ActiveBuffs.IsEmpty || entity.IsDead || !World.ZoneForEntity(entity).TryGet(handle, out _))
				{
					entity.ActiveBuffs.Clear();
					_buffedEntities.TryRemove(handle, out _);
					continue;
				}

				// Expire first, in one snapshot pass, for the same reason TickBuffExpiry does it that way.
				foreach (var expired in entity.TakeExpiredBuffs(now))
				{
					if (ClientForHandle(expired.Value.CasterHandle) is { } owner)
						await _output.SendBuffRemoval(owner, entity.Handle, expired.Value.CasterHandle, expired.Key);

					_logger.LogDebug("Effect 0x{Buff:X8} expired on {Npc} (0x{Handle:X8}).", expired.Key, entity.Name, entity.Handle);
				}

				foreach (var buff in entity.ActiveBuffs.Values.ToArray())
				{
					var owner = ClientForHandle(buff.CasterHandle);
					if (owner == null)
					{
						entity.ActiveBuffs.TryRemove(buff.BuffId, out _);
						continue;
					}

					if (buff.DotDelta != 0 && (now - buff.LastDotTick).TotalMilliseconds >= IntervalOf(buff))
					{
						buff.LastDotTick = now;
						await _output.ApplyNpcDotTick(owner, entity.Handle, buff.CasterHandle, buff.DotDelta, buff.BuffId >> 8);
						if (entity.IsDead)
							break; // the bleed landed the killing blow; the sweep above retires it next tick
					}
				}

				// The chain: an effect record can state an area of its OWN, so an infected mob spreads it to its
				// neighbours. Same op the player aura uses, centred on the mob.
				if (!entity.IsDead)
					await EmitAuraAreas(entity, null, now);
			}
		}

		// The connected player whose world entity carries `handle`, or null if nobody does (they logged out, or the
		// handle is a mob's). The reverse of PlayerWorldEntity, and the tick's way back to a client from a buff's
		// recorded caster.
		private INetworkClient? ClientForHandle(uint handle)
		{
			foreach (var pair in Players)
			{
				if (pair.Value.Handle == handle)
					return pair.Key;
			}

			return null;
		}

		private async Task TickDots()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				var entity = pair.Value;
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = entity.Vitals;
				if (vitals == null || vitals.IsDead)
					continue;

				foreach (var buff in entity.ActiveBuffs.Values)
				{
					if (buff.DotDelta == 0 || (now - buff.LastDotTick).TotalMilliseconds < IntervalOf(buff))
						continue;

					buff.LastDotTick = now;

					// .godmode has to cover DAMAGE OVER TIME, not just the swing that applied it. The two direct
					// NPC-damage paths check GodMode; this one did not, so a GM in god mode was killed by a bleed -
					// and by one the NPC cast path had applied THROUGH god mode, since it creates the buff before its
					// own GodMode return. The tick is the single choke point every DoT source funnels through, which
					// is why the guard belongs here rather than at each application site.
					//
					// The stamp above still advances, so a debuff that ticked through god mode does not burst-catch-up
					// the moment it is switched off. HEALS over time are unaffected: god mode blocks harm, not healing.
					if (entity.GodMode && buff.DotDelta < 0)
						continue;

					vitals.ApplyHealthDelta(buff.DotDelta);
					// #770: `SpellBaseId` is ZERO, like every other StatDamageIIR retail sends (see
					// CombatResolver.RetailSpellBaseId). This used to pass `buff.BuffId >> 8` - the DoT's own spell id -
					// which is why it survived the #767 fix to the cast path: that IS a real id, so the client loads it
					// without complaint and nothing ever looked wrong. It is simply not what retail states.
					await _output.SendStatDamage(client, entity.Handle, vitals.Health, buff.DotDelta, buff.DotType, buff.CasterHandle,
						CombatResolver.RetailSpellBaseId);
					await _output.PushVitals(client, vitals);

					if (vitals.IsDead)
					{
						entity.LastAttackerHandle = buff.CasterHandle; // a bleed can land the killing blow
						_logger.LogInformation("Combat: {Player} bled out from buff 0x{Buff:X8}.", character.Name, buff.BuffId);
						break;
					}
				}
			}
		}

	}
}
