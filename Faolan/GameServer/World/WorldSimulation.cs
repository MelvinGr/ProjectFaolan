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

		// Vitals regen cadence (rate-based; dt-scaled by the real elapsed time when it fires).
		private const double RegenIntervalSeconds = 1.0;


		// Local-say range in world units. Built-NPC tests showed a +4-unit offset renders "beside" the player, so
		// units are ~metres; ~50 is a generous "nearby" for /say. Tunable; same-map is the hard filter either way.
		private const float SayRangeUnits = 50f;
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
			WorldState world, PlayerRegistry players, PlayerDeathService playerDeaths, BuffTickService buffTicks, NpcCombatService npcCombat, TerrainService? terrain = null)
		{
			_spellAreas = spellAreas;
			_database = database;
			_agents = agents;
			_logger = logger;
			Terrain = terrain;
			World = world;
			Players = players;
			_playerDeaths = playerDeaths;
			_buffTicks = buffTicks;
			_npcCombat = npcCombat;
		}

		// A player dying, releasing and coming back. Forwarded to below so the listener's call sites are unchanged;
		// the tick drives its two per-tick halves directly. See World/PlayerDeathService.cs.
		private readonly PlayerDeathService _playerDeaths;

		// The per-tick life of an applied buff: expiry, DoT damage, aura areas. Driven by the tick below; its
		// DotEffect lookup is also what attaches a DoT to an NPC's debuff. See World/BuffTickService.cs.
		private readonly BuffTickService _buffTicks;

		// An aggroed NPC's swing and cast. Driven by the tick below, which resolves the attacker and the victim and
		// hands both over. See World/NpcCombatService.cs.
		private readonly NpcCombatService _npcCombat;

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
			// The CLIENT's copy of the vitals is reseeded from the world-entry snapshot, not from a push - so what we
			// last pushed says nothing about what it now holds, and a current that happens to match would never be
			// restated. Forget it here so the first push after entry states all three (#902).
			entity.Vitals?.ForgetPushedCurrents();

			// Re-apply the account-persisted GM toggles (#279): the live entity is fresh on each world entry (a map
			// change is a new world connection), so these would otherwise be lost on every zone. Only GMs ever carry them.
			if (client.Session.Account is { } account)
			{
				entity.GodMode = account.HasGmPrivilege(GmPrivileges.GodMode);
				entity.Invisible = account.HasGmPrivilege(GmPrivileges.InvisibleToMonsters);
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

		// ⭐ Relocate a live NON-PLAYER entity within its own zone (#917). The player twin is `RelocateSameMap` above;
		// this is the one for anything the world model holds that is not a character - an NPC, a corpse, a loot bag.
		//
		// **The ticket's open question, answered: a large jump is not "a move OR a despawn/respawn pair", it is BOTH,
		// and which one a client gets is a property of the VIEWER, not of the jump.** Three populations:
		//
		//   - a viewer who keeps the entity in sight gets the mode-02 snap (`BroadcastEntitySnap`);
		//   - a viewer who loses it gets a despawn;
		//   - a player standing near the DESTINATION, who has never seen it, gets a spawn.
		//
		// The last two are exactly what an interest pass produces, so this does not re-implement them: it re-runs
		// `ResolveInterest` for every player in the zone. That is the #778 sweep, whose whole subject is "an entity
		// moved ITSELF and the player did not" - a teleport is that, at maximum step size. Sweeping the zone rather
		// than the two radii is deliberate: the union of "was in range of the origin" and "is in range of the
		// destination" is what has to be re-resolved, and the pass is a cheap set-difference against `Known` that
		// sends nothing to a player whose view did not change.
		//
		// `MoveFrom` is reset with `Position` on purpose. It is the FROM point of the segment the tick's broadcaster
		// sends, so leaving it at the old spot would make the next ordinary step render as a walk all the way back
		// across the map.
		//
		// ⚠️ This is LIVE ONLY - it does not touch `NpcSpawn`, which is the placement the entity respawns at. Move a
		// creature and kill it, and it comes back where it was placed. That is the right default for repositioning a
		// stuck mob or staging a scene, and it means this is not a world-editing primitive.
		//
		// ⚠️ Same zone only. Crossing to another map means leaving one `Zone` and entering another, with a handle that
		// every viewer of the old zone still holds; nothing here does that.
		//
		// Returns the number of players whose interest was re-resolved.
		public async Task<int> RelocateEntity(WorldEntity entity, Vector3 destination)
		{
			ArgumentNullException.ThrowIfNull(entity);

			var zone = World.ZoneForEntity(entity);
			entity.Position = destination;
			entity.MoveFrom = destination;

			await _output.BroadcastEntitySnap(entity);

			var resolved = 0;
			foreach (var player in zone.Entities)
			{
				if (!player.IsPlayer || player.Client == null)
					continue;

				await _output.ResolveInterest(player.Client);
				resolved++;
			}

			_logger.LogInformation("World: {Entity} (handle {Handle}) relocated to {Dest} on map {Map}; {Resolved} player interest pass(es).", entity.Name, entity.Handle, destination,
				entity.MapId, resolved);
			return resolved;
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
			_buffTicks.BindOutbound(output);
			_npcCombat.BindOutbound(output);
			_tickTask = RunTickLoop(cancellationToken);
		}

		// The binding on its own, for tests that drive tick methods directly instead of running the loop
		// (InternalsVisibleTo Faolan.Tests is global). Production goes through Start.
		internal void BindOutbound(IWorldOutbound output)
		{
			_output = output;
			_playerDeaths.BindOutbound(output);
			_buffTicks.BindOutbound(output);
			_npcCombat.BindOutbound(output);
		}

		// Awaits the tick loop's (cancelled) exit. The loop swallows its own OperationCanceledException, so this
		// completes normally once the lifetime token has been cancelled by the listener.
		public Task StopAsync()
		{
			return _tickTask;
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
						: RegenRates.Gain(RegenRates.HealthPerSecond(constitution, strength, vitals.MaxHealth), elapsedSeconds),
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
										await _npcCombat.ApplyNpcAttack(effect.Entity, effect.Target!);
									break;
								}

								case WorldTickEffectType.NpcCast:
								{
									if (NpcAutoAttackEnabled)
										await _npcCombat.ApplyNpcCast(effect.Entity, effect.Target!);
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

						// #921: a harvest node shows the "being gathered" pin until 10 s after its last swing.
						foreach (var (zone, node) in World.TickGatheringPins(dt))
							await _output.RestoreNodeWaypointType(zone, node);

						// Player death: push the release screen once; respawn is click-driven (RespawnMe), not timed.
						await _playerDeaths.TickPlayerDeaths();
						await TickKillChains();
						await _buffTicks.TickBuffExpiry();
						await _buffTicks.TickDots();
						await _buffTicks.TickAuras();
						await _buffTicks.TickNpcBuffs(); // #776: the same three jobs - expire, bleed, emit - for buffs sitting on a MOB
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

	}
}
