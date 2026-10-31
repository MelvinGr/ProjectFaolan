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
using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The live in-memory world: what is in it and what it is doing this tick. Owns the world model and the
	///     connected players' authoritative transforms and vitals, and drives the 10 Hz simulation tick - NPC respawn
	///     and AI, vitals regen, player death and respawn, resurrection-point discovery.
	/// </summary>
	/// <remarks>
	///     It holds no networking of its own and reaches the wire through <see cref="IWorldOutbound" />, bound at
	///     <see cref="Start" />.
	/// </remarks>
	public sealed class WorldSimulation
	{
		/// <summary>
		///     The world-entity interest radius - how far a player is streamed spawns and despawns of everything
		///     else. Every viewer-set decision in <c>WorldBroadcaster</c> and <c>InterestManager</c> turns on it.
		/// </summary>
		/// <remarks>
		///     Measured, not invented (<c>--report interest-radius</c>). The distribution is a radius rather than a smear -
		///     zero samples below 20 u, a mode at 45-55 u and a decaying tail - and the player's speed separates its two
		///     halves: the median rises from 68 u at 2-4 u/s to 391 u at 12-20 u/s while the p10 does not move, which is
		///     what a boundary plus a variable sweep overshoot predicts. Read off the slow buckets, where the overshoot is
		///     a few units, the boundary lands at ~60 u.
		///     <para>
		///         This is the despawn radius. Retail may run hysteresis, spawning nearer than it despawns, and only the
		///         despawn side is measurable - nothing bulk-despawns at login, which is what makes it clean. Using one
		///         constant for both streams a little more eagerly than retail on the way in, which is the safe direction.
		///     </para>
		/// </remarks>
		public const float PlayerInterestRange = 60f;

		// NPC auto-attack. The hostile-NPC swing plays the decoded attack-animation ack (the second BccAck, with actor set
		// to the NPC handle - ApplyNpcAttack into IWorldOutbound.SendNpcAttackAnimation, docs/subsystems/combat.md) and
		// then the 0x96B8DC59 vitals move for the HP drop. The kill switch exists because on a zone with many hostiles
		// (Tortage Beach has ~400) a player who keeps dying can hit a death loop, re-entering the world every ~10 s and
		// never finishing loading.
		private const bool NpcAutoAttackEnabled = true;

		// Play each creature's ambient idle animations (BehaviorId.Emote) so a standing NPC looks alive rather than frozen
		// on its spawn pose. Confirmed-gated in the broadcaster, so it never reaches a loading client. The kill switch is
		// for the in-client pass, in case the animations want disabling rather than re-timing.
		private const bool NpcAmbientAnimationEnabled = true;

		// Ambient spawn-pool maintenance cadence. This interval doubles as the respawn delay - a killed pool member is
		// replaced on the next pass - so it tracks the DefaultRespawnDelaySeconds placed mobs use rather than being a
		// number of its own.
		//
		// The corpus is not settled on that. Splitting `--report respawn-interval`'s 23 matches by whether the creature
		// has a placement separates them cleanly: the placed ones cluster (Brutal Pirate 149.9-150.2 across three
		// captures, Wild Cow and Wild Pig 119.5-120.0), while the eleven with no placement - ambient or summoned - run a
		// 3.4 s to 99.6 s continuum (eight Hatchlings 3.4-15.4, three Panthers 20.4/60.2/99.6). Retail's ambient
		// population may well come back far faster than its placed one, but a continuum is not a timer's signature - a
		// real interval piles up at one value, which is what distinguishes the two clusters - and a nest adding spawns
		// during a fight produces the same shape.
		private const double AmbientMaintainIntervalSeconds = NpcRoute.DefaultRespawnDelaySeconds;

		// How often a standing player's interest is re-resolved, so entities that moved themselves stream in and out.
		// Slow on purpose: the subjects walk at ~2.2 u/s against a 60 u range, so a second's granularity is two units of
		// travel, and the pass costs one distance check per entity in the player's zone.
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
		private readonly ConcurrentDictionary<uint, MapRegion[]> _mapAreas = new ConcurrentDictionary<uint, MapRegion[]>(); // per-map banner volumes, read every tick

		private readonly IChatServerDispatcher _agents;
		private readonly ICharacterRepository _characters;
		private readonly IWorldRepository _worldData;
		private readonly ILogger _logger;

		// Bound at Start, never in the constructor. Everything this port reaches - WorldBroadcaster above all - is built
		// on the simulation, so demanding it in the constructor closes a WorldSimulation/WorldBroadcaster cycle that no
		// container can build. Binding it at Start means the simulation constructs with no outbound dependency at all,
		// and the tick, the only thing that uses it, does not run until Start.
		private IWorldOutbound _output = null!;

		/// <summary>
		///     Sends what <paramref name="entity" /> has walked but not yet had broadcast, and ends its move.
		/// </summary>
		/// <remarks>
		///     The tick throttles a chase into ~6 u segments (<c>MoveBroadcastSegment</c>) and flushes the remainder in its
		///     own <c>EndMove</c> when the entity arrives. Death is the one way a move ends without going through that, so
		///     a creature killed mid-chase leaves up to 6 u the client never saw - and the death pose states the true
		///     position, so the corpse jumps that far onto its killer before playing its death animation. This lives here
		///     because the tick owns the segment bookkeeping it is finishing.
		/// </remarks>
		public async Task FlushPendingMove(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			if (entity.TakePendingMoveSegment())
				await _output.BroadcastNpcMove(entity);
		}

		private Task _tickTask = Task.CompletedTask;

		// Each zone's tick clocks, failure count and measured rate. Touched only by that zone's own tick, on its loop.
		private readonly ConcurrentDictionary<Zone, ZoneClocks> _zoneClocks = new ConcurrentDictionary<Zone, ZoneClocks>();
		public PlayerRegistry Players { get; }

		// Server-side terrain (RDB heightmap): ground-height snapping and terrain line-of-sight. Reached by the
		// broadcaster for a loot bag's Y, and by the WorldState tick for aggro line-of-sight.
		public TerrainService? Terrain { get; }

		// The live world model + the connected players' authoritative world entities (transform + vitals).
		public WorldState World { get; }

		// How the tick loop is actually running (achieved rate + busy fraction). Read by the keep-alive Ping reply for
		// the client's dev "Server Framerate" overlay, and by the slow-tick warning below. See TickMetrics.
		public TickMetrics Metrics { get; } = new TickMetrics(1.0 / BaseTickSeconds);

		// `World` and `Players` arrive by injection rather than being created here. Both are shared with
		// PlayerDeathService, which must not hold this class, so the state they both read is what gets passed rather than
		// a reference back to its owner.
		public WorldSimulation(ICharacterRepository characters, IWorldRepository worldData, IChatServerDispatcher agents, ILogger logger,
			WorldState world, PlayerRegistry players, PlayerDeathService playerDeaths, BuffTickService buffTicks, NpcCombatService npcCombat, TerrainService? terrain = null)
		{
			_characters = characters;
			_worldData = worldData;
			_agents = agents;
			_logger = logger;
			Terrain = terrain;
			World = world;
			Players = players;
			_playerDeaths = playerDeaths;
			_buffTicks = buffTicks;
			_npcCombat = npcCombat;
		}

		// A player dying, releasing and coming back. Forwarded to below so callers keep one collaborator; the tick drives
		// its two per-tick halves directly. See World/PlayerDeathService.cs.
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

		/// <summary>
		///     Runs <paramref name="work" /> for a player on the loop of the zone they are in: how anything that is not
		///     already that zone's own work - a delayed action, a per-player loop, another listener - reaches a player's
		///     world state. Nothing runs once the player has left the world: a delay that outlived its player is over.
		/// </summary>
		/// <remarks>
		///     No loop waits on a zone's work, so that no two loops can each wait on the other: from inside another loop's
		///     work (another zone, the CS tier) this queues rather than awaits. Off every loop - chat, MCP, a timer - it is
		///     awaited.
		/// </remarks>
		public Task RunInZoneOf(INetworkClient client, Func<Task> work)
		{
			return client.IsStopped || PlayerWorldEntity(client) is not { } entity
				? Task.CompletedTask
				: RunOnLoop(World.ZoneForEntity(entity).Loop, work);
		}

		/// <summary>
		///     Runs <paramref name="work" /> as the player's: on their zone's loop once they are in the world, and on the
		///     caller's flow before that, when no zone owns them yet (the login handshake, the create flow). The packet
		///     path and every other listener's call into a player go through here.
		/// </summary>
		/// <remarks>The same no-waiting rule as <see cref="RunInZoneOf(INetworkClient,Func{Task})" /> applies.</remarks>
		public Task RunForPlayer(INetworkClient client, Func<Task> work)
		{
			return PlayerWorldEntity(client) is { } entity
				? RunOnLoop(World.ZoneForEntity(entity).Loop, work)
				: work();
		}

		/// <summary>
		///     <see cref="RunInZoneOf(INetworkClient,Func{Task})" /> for an entity (an NPC's delayed action): runs on its
		///     zone's loop, and not at all once it has left its zone.
		/// </summary>
		public Task RunInZoneOf(WorldEntity entity, Func<Task> work)
		{
			var zone = World.ZoneForEntity(entity);
			return zone.TryGet(entity.Handle, out _)
				? RunOnLoop(zone.Loop, work)
				: Task.CompletedTask;
		}

		/// <summary>Runs <paramref name="work" /> on <paramref name="zone" />'s loop, under the same no-waiting rule.</summary>
		public Task RunInZone(Zone zone, Func<Task> work)
		{
			return RunOnLoop(zone.Loop, work);
		}

		/// <summary>
		///     The zone a player's world entry will place them in: the instance <see cref="RegisterPlayerInWorld" /> would
		///     resolve, decided up front so the entry can run on that zone's loop. Pass its instance back as
		///     <c>resolvedInstance</c>, since resolving twice would mint two copies of a Solo map.
		/// </summary>
		public Zone EntryZone(INetworkClient client, MapInstanceMode instanceMode, uint? teamId, bool epic, uint? pvpMatchId)
		{
			var character = client.Session.Character;
			Players.TryGetValue(client, out var existing);
			return World.ZoneFor(character.MapId, World.ResolveEntryInstance(existing, character.MapId, instanceMode, teamId, epic, pvpMatchId));
		}

		/// <summary>
		///     The player's own live vitals. The world model is the single source of truth: they live on the player's
		///     WorldEntity, as an NPC's do, seeded from CharacterMaxStats and never persisted - only CharacterMaxStats is.
		/// </summary>
		/// <remarks>
		///     Falls back to a transient seed only when the player is not in the world yet, which is defensive: no combat or
		///     regen happens then. It lives on this class because it reads nothing else; putting it on CombatService would
		///     make BuffService depend on CombatService for one call and close a construction cycle.
		/// </remarks>
		public EntityVitals GetOrAddPlayerVitals(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null)
				return EntityVitals.FromCharacter(client.Session.Character);

			return entity.Vitals ??= EntityVitals.FromCharacter(client.Session.Character);
		}

		/// <summary>
		///     Registers, or relocates, the player in the live world after world entry, seeding the authoritative
		///     WorldEntity transform and vitals from the persisted Character.
		/// </summary>
		/// <remarks>
		///     After this the WorldEntity is the source of truth for live position and vitals; the Character is re-read only
		///     on a fresh entry or teleport. Idempotent - GetOrAdd keeps the existing entity, so vitals are not reset across
		///     a re-entry, teleport or respawn.
		/// </remarks>
		public void RegisterPlayerInWorld(INetworkClient client, MapInstanceMode instanceMode = MapInstanceMode.Shared, IReadOnlyDictionary<CharacterStatId, int>? statBonuses = null,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null, IReadOnlyDictionary<uint, int>? spellBoosts = null,
			IReadOnlyDictionary<uint, uint>? spellScalingAttributes = null, IReadOnlyDictionary<uint, ComboFeatBonus>? comboBonuses = null,
			uint? teamId = null, bool epic = false, uint? pvpMatchId = null, IReadOnlyList<FeatProc>? featProcs = null, uint? resolvedInstance = null)
		{
			var character = client.Session.Character;

			var handle = character.Id;
			// The player's pre-existing entity, null on a fresh login, decides whether this is a re-entry that should
			// keep its current instance or a fresh placement that picks one from the map's mode.
			Players.TryGetValue(client, out var existing);
			// Seed the max vitals with the equipped-gear bonus so the regen bar fills past the class base. Only the
			// fresh-entry seed uses it; a re-entry keeps its existing, already gear-aware, vitals through GetOrAdd.
			var entity = Players.GetOrAdd(client, _ => new WorldEntity(handle, character.MapId, true, character.Name ?? $"Character{handle}", client)
			{
				Vitals = EntityVitals.FromCharacter(character, statBonuses, classBase),
				// Cache the worn-gear physical armor and magical Protection for incoming-damage mitigation.
				ArmorRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Armor) ?? 0),
				Protection = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Protection) ?? 0),
				// Cache the worn-gear Hit Rating (hit chance, not yet consumed) and the crit stats.
				HitRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.HitRating) ?? 0),
				CriticalRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalRating) ?? 0),
				CriticalDamageRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalDamageRating) ?? 0),
				CombatRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CombatRating) ?? 0),
				// Cache the worn-gear Magic Damage added flat to the player's spell hits.
				MagicDamage = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.MagicDamage) ?? 0),
				// Cache the worn-gear natural mana/stamina regen added onto the base regen tick.
				NaturalManaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalManaRegen) ?? 0),
				NaturalStaminaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalStaminaRegen) ?? 0),
				// Seed the live drawn weapon set from the persisted value so a mid-session swap starts from the set the
				// character last had out; the statBonuses above were aggregated for that same set.
				ActiveWeaponSet = character.ActiveWeaponSet,
				// Keep the gear bonuses themselves, not just their sum into the fields above: they are the baseline a
				// buff's stat delta is added to, and removed from, by BuffStatEffects.
				GearBonuses = statBonuses ?? new Dictionary<CharacterStatId, int>(),
				ClassBase = classBase ?? new Dictionary<CharacterStatId, int>(),
				// The feat boosts for this character's spells, already fanned out from line to rank.
				SpellBoosts = spellBoosts ?? new Dictionary<uint, int>(),
				// The caster's attribute scores and class, which is what the magic-damage scaling term reads, plus the
				// per-spell attribute overrides. Same inputs as the pools above (VitalAttributes.From), so the two never
				// disagree about what the character's Intelligence is.
				Attributes = VitalAttributes.From(classBase, statBonuses),
				// The character's level, which must be set: BuffStatEffects resolves a ramped buff delta off it (the
				// same buff is +887 health at level 20 and +17,500 at level 80), and RebaseFromAttributes passes it to
				// CharacterMaxStats.ClassBaseHundredths, which throws on 0.
				Level = character.Level,
				CharacterClass = character.Class,
				SpellScalingAttributes = spellScalingAttributes ?? new Dictionary<uint, uint>(),
				// What the character's feats do to their combos, resolved once here for the same reason the spell
				// boosts are: a swing must not turn into a query.
				ComboBonuses = comboBonuses ?? new Dictionary<uint, ComboFeatBonus>(),
				// And the procs those feats hang off the combos and the character's spell lines.
				Procs = featProcs ?? []
			});
			// The client's copy of the vitals is reseeded from the world-entry snapshot rather than from a push, so what
			// was last pushed says nothing about what it now holds and a current that happens to match would never be
			// restated. Forgetting it here makes the first push after entry state all three.
			entity.Vitals?.ForgetPushedCurrents();

			// Re-apply the account-persisted GM toggles: the live entity is fresh on each world entry, since a map change
			// is a new world connection, so these would otherwise be lost on every zone. Only GMs carry them.
			if (client.Session.Account is { } account)
			{
				entity.GodMode = account.HasGmPrivilege(GmPrivileges.GodMode);
				entity.Invisible = account.HasGmPrivilege(GmPrivileges.InvisibleToMonsters);
			}

			// `teamId` is what makes a group map route to the party's copy rather than to a private one. It is passed in
			// rather than looked up here, because the roster lives on the CS tier and the world model does not reach
			// across listeners; the caller crosses that seam.
			//
			// An epic entry routes to an epic copy of the map - the same content, never the same zone as a normal one -
			// and the zone remembers its difficulty so re-entry routing and stat tuning can ask it.
			var instanceId = resolvedInstance ?? World.ResolveEntryInstance(existing, character.MapId, instanceMode, teamId, epic, pvpMatchId);
			World.ZoneFor(character.MapId, instanceId).IsEpic = epic;
			// The DB rotation is a quaternion; the live sim/movement layer is Vector3 (Euler degrees) - bridge it.
			var zone = World.Place(entity, character.MapId, instanceId, character.Position, character.Rotation);
			var nearby = zone.PlayersInRange(character.Position, PlayerInterestRange, handle).Count();
			_logger.LogInformation("World: player {Name} (0x{Handle:X8}) entered map {Map} instance 0x{Instance:X8} at {Pos}; {Nearby} other player(s) in range.", entity.Name, handle, character.MapId,
				instanceId, character.Position, nearby);
		}

		/// <summary>
		///     A same-map (same-playfield) teleport: relocates the player to <paramref name="destination" /> on their
		///     current map with no loading screen, reusing the death-revive same-map path - an IntendedTeleport with the
		///     live playfield token, then an n3Teleport. The position is persisted so it survives a relog.
		/// </summary>
		/// <remarks>
		///     This is how a same-playfield zone-line door works: the Thirsty Dog Inn's UseObject (door handle 0xBEB)
		///     resolves to an IntendedTeleport whose destination map is the current map, plus an N3Teleport, with no
		///     reconnect (Hoxxling_..._gameplay2: door 0xBEB, arrival (45, 158.05, 124) on map 4020, with the player
		///     already on 4020). Contrast DoTeleport, which cross-maps through a full 0x2002 handoff and reconnect, and so
		///     shows a loading screen.
		/// </remarks>
		public async Task RelocateSameMap(INetworkClient client, Vector3 destination)
		{
			var entity = PlayerWorldEntity(client);
			var character = client.Session.Character;
			if (entity == null || character == null)
				return;

			entity.Position = destination;
			character.Position = destination;
			await _characters.SaveCharacterTransform(character.Id, destination, character.Rotation, character.MapId);

			var liveToken = World.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client!, entity.Handle, character.MapId, liveToken);
			await _output.SendReviveDestination(client!, entity.Handle, character.MapId, destination, character.Rotation);
			_logger.LogInformation("World: {Player} same-map relocated to {Dest} on map {Map} (token 0x{Token:X}).", character.Name, destination, character.MapId, liveToken);
		}

		/// <summary>
		///     Relocates a live non-player entity within its own zone - an NPC, a corpse, a loot bag. The player twin is
		///     <see cref="RelocateSameMap" />.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A large jump is both a move and a despawn/respawn pair, and which one a client gets is a property of the
		///         viewer rather than of the jump. A viewer who keeps the entity in sight gets the mode-02 snap
		///         (<c>BroadcastEntitySnap</c>); a viewer who loses it gets a despawn; and a player standing near the
		///         destination, who has never seen it, gets a spawn.
		///     </para>
		///     <para>
		///         The last two are what an interest pass already produces, so this re-runs <c>ResolveInterest</c> for every
		///         player in the zone rather than re-implementing them. Sweeping the whole zone is deliberate: the union of
		///         "was in range of the origin" and "is in range of the destination" is what has to be re-resolved, and the
		///         pass is a cheap set difference against <c>Known</c> that sends nothing to a player whose view did not
		///         change.
		///     </para>
		///     <para>
		///         <c>MoveFrom</c> is reset with <c>Position</c> on purpose: it is the from-point of the segment the tick's
		///         broadcaster sends, so leaving it at the old spot would make the next ordinary step render as a walk all
		///         the way back across the map.
		///     </para>
		///     <para>
		///         Live only - it does not touch <c>NpcSpawn</c>, which is the placement the entity respawns at, so a
		///         creature moved and then killed comes back where it was placed. Same zone only: crossing to another map
		///         means leaving one <c>Zone</c> and entering another, with a handle every viewer of the old zone still
		///         holds, and nothing here does that.
		///     </para>
		/// </remarks>
		/// <returns>The number of players whose interest was re-resolved.</returns>
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

		/// <summary>
		///     Removes the player from the world model on disconnect. The caller broadcasts the despawn first, while the
		///     leaver is still in the zone so its viewers resolve, and then calls this.
		/// </summary>
		/// <returns>False if the player was not in the world.</returns>
		public bool RemovePlayer(INetworkClient client, [MaybeNullWhen(false)] out WorldEntity entity)
		{
			if (!Players.TryRemove(client, out entity))
				return false;

			World.Remove(entity);
			_playerDeaths.ClearRespawnTimer(client);
			return true;
		}

		/// <summary>
		///     The character ids that should hear a proximity "say" from <paramref name="sender" />: same-map players within
		///     <c>SayRangeUnits</c> of the sender, plus the sender, who always hears themselves. Resolved against the live
		///     world model.
		/// </summary>
		public IReadOnlyCollection<uint> SayRecipients(INetworkClient sender)
		{
			var ids = new HashSet<uint>();
			var entity = PlayerWorldEntity(sender);
			if (entity == null)
			{
				// Not in the world model yet - fall back to the sender alone so they still see their own message.
				if (sender.Session.Character != null)
					ids.Add(sender.Session.Character.Id);
				return ids;
			}

			ids.Add(entity.Handle);
			foreach (var near in World.ZoneForEntity(entity).PlayersInRange(entity.Position, SayRangeUnits, entity.Handle))
				ids.Add(near.Handle);

			return ids;
		}

		/// <summary>
		///     Starts the simulation tick on the given lifetime token, owned by the listener so startup and shutdown stay in
		///     one place. The task is held so <see cref="StopAsync" /> can await a clean exit.
		/// </summary>
		/// <remarks>
		///     <paramref name="output" /> arrives here rather than in the constructor - see the <c>_output</c> field for
		///     why. Start is called from the listener's StartAsync, long after every collaborator exists, and nothing
		///     touches the port before it.
		/// </remarks>
		public void Start(CancellationToken cancellationToken, IWorldOutbound output)
		{
			BindOutbound(output);
			_playerDeaths.BindOutbound(output);
			_buffTicks.BindOutbound(output);
			_npcCombat.BindOutbound(output);
			_tickTask = RunTickLoop(cancellationToken);
		}

		// The binding on its own, for tests that drive tick methods directly instead of running the loop. Production goes
		// through Start.
		internal void BindOutbound(IWorldOutbound output)
		{
			_output = output;
			_playerDeaths.BindOutbound(output);
			_buffTicks.BindOutbound(output);
			_npcCombat.BindOutbound(output);
		}

		// Awaits the tick loop's (cancelled) exit. The loop swallows its own OperationCanceledException, so this
		// completes normally once the lifetime token has been cancelled by the listener.
		/// <summary>Completes once the tick beat has stopped (after the lifetime token is cancelled).</summary>
		public Task StopAsync()
		{
			return _tickTask;
		}

		/// <summary>Stops every zone's loop once the work already queued on it has run; the last step of shutdown.</summary>
		public Task StopZonesAsync()
		{
			return World.StopAsync();
		}

		/// <summary>
		///     The measured tick rate of the loop serving <paramref name="client" />: its zone's, once it is in the world,
		///     and the realm's beat before that (the loading screen pings too).
		/// </summary>
		public TickMetrics MetricsFor(INetworkClient client)
		{
			return PlayerWorldEntity(client) is { } entity && _zoneClocks.TryGetValue(World.ZoneForEntity(entity), out var clocks)
				? clocks.Metrics
				: Metrics;
		}

		private async Task<MapRegion[]> GetMapAreasCached(uint mapId)
		{
			if (_mapAreas.TryGetValue(mapId, out var cached))
				return cached;

			var areas = await _worldData.GetAreaBannerRegions(mapId);
			_mapAreas[mapId] = areas;
			return areas;
		}

		// Regenerates each connected player's vitals by `elapsedSeconds` worth and pushes a 0x96B8DC59 update only when a
		// pool actually rose, so a player at full health, stamina and mana costs nothing. The rates are attribute-driven
		// and corpus-verified (RegenRates); retail does not use flat fractions of the pool max.
		private async Task RegenerateConnectedPlayers(Zone zone, double elapsedSeconds)
		{
			foreach (var pair in Players.In(zone))
			{
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character, pair.Value.GearBonuses, pair.Value.ClassBase);
				if (vitals.IsDead)
					continue; // corpses do not regen - the respawn timer revives them

				// Health and mana regen are out-of-combat only (retail's OOCHealth/ManaRegeneration): halted while the
				// player has dealt or taken damage within CombatRegenPause. Stamina, the action resource, keeps going.
				var entity = pair.Value;
				var inCombat = (DateTimeOffset.UtcNow - entity.LastCombatAt).TotalSeconds < CombatRegenPause;
				uint beforeH = vitals.Health, beforeM = vitals.Mana, beforeS = vitals.Stamina;

				// Retail's rates, from the character's attribute scores (RegenRates: the tweak table's own 0.15 and 0.38
				// modifiers, each confirmed against a captured RegenIIR delta at x100 precision). Stamina keeps its
				// worn-gear Natural Stamina Regen on top - that item stat (0x385) is additive to the out-of-combat rate.
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

				// Retail's compact RegenIIR delta (0x28D0AC99): the per-tick rise per pool, x100 to the wire vitals
				// scale. The client's apply (FUN_0079dd10) reads the stat, adds the delta and writes it back through the
				// same setter StatUpdateIIR's apply uses (entity+0x10c vtable +0xc), so this is retail's own channel for
				// moving the bars without a full stat block.
				await _output.SendRegen(client, entity.Handle, (int)(vitals.Health - beforeH) * 100, (int)(vitals.Mana - beforeM) * 100, (int)(vitals.Stamina - beforeS) * 100);
				// Then the authoritative 0x96B8DC59 block, which is the in-client-verified channel and what actually
				// makes regen visible. The delta alone does not: regen stops ticking in-client even though the packet is
				// byte-identical in shape to all 1,502 captured retail RegenIIRs (len 29, env 0, class 0xC350, three
				// big-endian i32). Order matters - the delta goes first so the absolute value lands last and wins, which
				// means the client can never double-apply and any drift is corrected every tick.
				//
				// Retail is lighter, pinning a pool with a StatUpdate only on the tick that tops it off
				// (Hoxxling_mage-heraldofxotli_login_gameplay 18:49:49.912 sends REGEN(2060,1992,882) plus a StatUpdate
				// setting stamina and mana to their max); thinning this down to that shape needs the delta channel proven
				// in-client first.
				await _output.PushVitals(client, vitals);
			}
		}

		// The server game loop: a 10 Hz base tick that advances continuous systems every tick with the real elapsed dt
		// (NPC respawn and AI, player death timers, boundary-crossing tests) and runs rate-based systems (vitals regen,
		// ambient pools) through a TickAccumulator at their own dt-scaled interval. Wall-clock dt is measured so a late
		// tick does not desync the rates. Cancelled on shutdown.
		//
		// Hang a system where its cadence belongs, not on whatever block already exists: a crossing test has to sample
		// fast to catch a boundary, a rate-based one must not. Bolting the two together silently gives the crossing test
		// the rate-based interval as its sampling rate.
		private async Task RunTickLoop(CancellationToken cancellationToken)
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BaseTickSeconds));
			var healthReport = new TickAccumulator(SlowTickReportSeconds);
			var lastTimestamp = Stopwatch.GetTimestamp();
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var now = Stopwatch.GetTimestamp();
					var dt = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds;
					lastTimestamp = now;

					// The beat only hands each zone its tick: the zone's own loop runs it, between that zone's packet
					// handlers, so the tick and the handlers never touch the zone at once. A zone still busy with its
					// previous tick is skipped this beat, and its next tick advances by the time that really passed.
					foreach (var zone in World.Zones)
					{
						if (zone.TryBeginTick())
							zone.Loop.Post(() => TickZone(zone));
					}

					Metrics.RecordTick(Stopwatch.GetElapsedTime(now).TotalSeconds);

					if (healthReport.Advance(dt, out _))
						ReportSlowZones();
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Game tick loop terminated.");
			}
		}

		// One zone's tick, on that zone's loop.
		private async Task TickZone(Zone zone)
		{
			var clocks = _zoneClocks.GetOrAdd(zone, _ => new ZoneClocks());
			var now = Stopwatch.GetTimestamp();
			var dt = clocks.LastTick == 0
				? BaseTickSeconds
				: Stopwatch.GetElapsedTime(clocks.LastTick, now).TotalSeconds;
			clocks.LastTick = now;

			// One bad tick must not kill the zone, so this catches everything: an exception that escaped would only fail
			// this tick's work item, but the next beat must still find the zone's tick claim released.
			try
			{
				// Continuous-time systems: every base tick, with the actual elapsed dt. The tick returns the wire-visible
				// effects - NPC respawns, loot expiry, NPC attacks - to broadcast.
				foreach (var effect in World.Tick(zone, dt))
				{
					// A dead NPC must emit no live broadcast - move, patrol, follow, heartbeat, chatter, attack or
					// combat mode. A move effect generated the tick it died, or processed across an await after the
					// player's kill ran OnNpcKilled and sent the death pose, would override that pose and walk or
					// re-animate the corpse, so a patrolling NPC appears to walk off and vanish instead of dying in
					// place. Only the corpse-lifecycle effects - linger to loot, respawn, fade - apply once dead.
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
							// The fade and quit have already gone out in the two stages before this one, so the
							// entity is off every client's screen; this only drops it from the model.
							World.Remove(effect.Entity);
							break;
						}

						case WorldTickEffectType.LootExpired:
						case WorldTickEffectType.TimedDespawn: // a summon's lifetime ran out - same teardown
						{
							await _output.BroadcastEntityFade(effect.Entity); // graceful fade-out (n3ToClientFade)
							World.Remove(effect.Entity);
							break;
						}

						case WorldTickEffectType.NpcAttack:
						{
							// Gated by NpcAutoAttackEnabled; the swing plays the decoded attack-animation ack (see
							// ApplyNpcAttack).
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
							await _output.BroadcastNpcPatrol(effect.Entity); // idle patrol - not gated on the combat simulation
							break;
						}

						case WorldTickEffectType.NpcFollow:
						{
							// Bound to the leader, which is what makes the client walk the follower rather than
							// interpolate a bare segment and be re-anchored. Falls back to the plain step if the
							// leader is gone by the time the effect is drained.
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
							// Draw (enter) or sheathe (exit) the NPC's weapon for nearby viewers. Gated with the rest of
							// the combat simulation, since combat mode only has meaning alongside the auto-attack swing.
							if (NpcAutoAttackEnabled)
							{
								await _output.BroadcastNpcCombatMode(effect.Entity, effect.Type == WorldTickEffectType.NpcEnterCombat);
								// On engage, also push the aggro so the threat is visible.
								if (effect is { Type: WorldTickEffectType.NpcEnterCombat, Target: not null })
									await _output.BroadcastNpcAggro(effect.Entity, effect.Target);
							}

							break;
						}

						case WorldTickEffectType.NpcShieldRose:
						{
							// Directional combat: the defender's blue guard arcs. Gated with the combat simulation - the
							// rose only means anything when the NPC is actually fighting back.
							if (NpcAutoAttackEnabled)
								await _output.BroadcastShieldRose(effect.Entity);
							break;
						}

						case WorldTickEffectType.NpcStopped:
						{
							// The NPC arrived home and stopped; close its move with a StopIIR.
							if (NpcAutoAttackEnabled)
								await _output.BroadcastNpcStop(effect.Entity);
							break;
						}

							case WorldTickEffectType.NpcAmbientAnimation:
						{
							// The ambient idle animation - a citizen leaning on a wall rather than standing to
							// attention. Cosmetic, so ungated like the heartbeat above; the tick has already picked
							// which animation and how long it runs.
							if (NpcAmbientAnimationEnabled)
								await _output.BroadcastNpcAmbientAnimation(effect.Entity);
							break;
						}

					case WorldTickEffectType.EncounterBark:
						{
							// The lure calls for help as a player closes. Cosmetic - the swap is what matters - but
							// it is the tell that the encounter is armed, and it is a real captured line; its
							// `event(2)` channel is what marks these apart from ambient chatter.
							var lure = effect.Entity;
							if (lure.Presentation.EncounterLines is not { Length: > 0 } barkLines)
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
							// Ambient bark: the NPC local-says a random line from its own type's set (chat server
							// CharacterChat 0x0022) to the players in say-range - purely cosmetic. Sender = the NPC
							// handle so the client shows the bubble over it.
							//
							// The lines are per template, never a shared pool: six types carry any, each with the
							// client's own group - `RenownCrier` is Tatianus the Crier alone, so his "consider joining
							// a guild" line cannot come out of anyone else. (A group may span types where retail shares
							// one: the Nobleman and one Noblewoman both bark `RenownBystanderLovers`. That is the
							// client's grouping, not ours.)
							var npc = effect.Entity;
							if (npc.Presentation.ChatterLines is not { Length: > 0 } lines)
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
							// The crier noticed a player arrive and greets them. Not the ambient tier - the cue is
							// the arrival, and the channel is `sendtell`, so it goes to that player alone rather than
							// to everyone in earshot. That is also what makes the addressee slot coherent: the line
							// is "..., {DetectedChar.name}!", and there is exactly one name it can carry.
							var greeter = effect.Entity;
							if (effect.Target is not { } greeted || greeter.Presentation.GreetingLines is not { Length: > 0 } greetings)
								break;

							await _agents.NpcBark(greeter.Handle, greetings[Random.Shared.Next(greetings.Length)], [greeted.Handle]);
							break;
						}
					}
				}

				// The staged visual sequence a harvested world object plays - fade, mesh swap, collision drop,
				// and the regrowth ten seconds later. Retail paces these against the client's own fade, so they
				// are timed steps rather than one burst.
				foreach (var step in zone.TickVisualSequences(dt))
					await _output.PlayDynelVisualStep(step);

				foreach (var node in zone.TickGatheringPins(dt))
					await _output.RestoreNodeWaypointType(zone, node);

				await _playerDeaths.TickPlayerDeaths(zone);
				await TickKillChains(zone);
				await _buffTicks.TickBuffExpiry(zone);
				await _buffTicks.TickDots(zone);
				await _buffTicks.TickAuras(zone);
				await _buffTicks.TickNpcBuffs(zone); // the same three jobs - expire, bleed, emit - for buffs sitting on a mob
				await _output.CompleteDueTradeskills(zone); // a craft or socket whose ~5 s retail bar has run out

				await _playerDeaths.DiscoverNearbyRespawnPoints(zone);
				await TickAreaBanners(zone);

				if (clocks.Regen.Advance(dt, out var regenElapsed))
					await RegenerateConnectedPlayers(zone, regenElapsed);

				if (clocks.InterestSweep.Advance(dt, out _))
				{
					foreach (var client in Players.In(zone).Select(p => p.Key).ToList())
						await _output.ResolveInterest(client);
				}

				if (clocks.AmbientMaintain.Advance(dt, out _))
					await _output.MaintainAmbientPools(zone);

				clocks.ConsecutiveFailures = 0;
			}
			catch (Exception ex)
			{
				clocks.ConsecutiveFailures++;
				if (clocks.ConsecutiveFailures == 1 || clocks.ConsecutiveFailures % 100 == 0)
					_logger.LogError(ex, "Tick of zone {Map}/{Instance} failed ({Count} consecutive); the zone keeps ticking.", zone.MapId, zone.InstanceId, clocks.ConsecutiveFailures);
			}
			finally
			{
				clocks.Metrics.RecordTick(Stopwatch.GetElapsedTime(now).TotalSeconds);
				zone.EndTick();
			}
		}

		private void ReportSlowZones()
		{
			foreach (var (zone, clocks) in _zoneClocks)
			{
				var health = clocks.Metrics.Current;
				if (health.TicksPerSecond > 0 && health.TicksPerSecond < clocks.Metrics.NominalTicksPerSecond * SlowTickThreshold)
					_logger.LogWarning("Zone {Map}/{Instance} tick is behind: {Rate:F1} of a nominal {Nominal:F1} Hz ({Busy:P0} of the tick budget in use).",
						zone.MapId, zone.InstanceId, health.TicksPerSecond, clocks.Metrics.NominalTicksPerSecond, health.BusyFraction);
			}
		}

		private async Task TickAreaBanners(Zone zone)
		{
			foreach (var (client, entity) in Players.In(zone))
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
					continue; // still in the same area, or still in none - a banner per tick would spam

				entity.CurrentAreaNameId = now;
				if (now == 0)
					continue; // left an area into unnamed ground - retail shows nothing

				await _output.SendAreaNameBanner(client, now);
				// Logged by id and by the client's own name for the volume: the wire carries only the id, since the
				// client renders the text from its own LDB, and the region name is what makes the line readable without
				// a lookup. 3373519 is "Bridge of Vilerus I", to read one by hand.
				_logger.LogInformation("World: {Player} entered area {Id} ('{Region}') on map {Map}.", client.Session.Character.Name, now, best!.Name, entity.MapId);
			}
		}

		// Closes a lapsed kill chain. Retail pairs every non-zero ChainKillIIR with a 0 about 9 s later - 32 of 32 in the
		// corpus - so the chain has a visible end, and without this the client keeps the last count on screen forever.
		// The kill side is CombatService.AnnounceKillChain; the window is the measured
		// WorldEntity.ChainKillWindowSeconds.
		private async Task TickKillChains(Zone zone)
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players.In(zone))
			{
				if (!pair.Value.TryLapseKillChain(now))
					continue;

				await _output.SendKillChain(pair.Key, pair.Value.Handle, 0);
			}
		}

		// No loop waits on a zone's work: from another zone, or from the CS tier (which a zone may itself be waiting on),
		// the call is queued. Off every loop - chat, MCP, a timer - it is awaited.
		private static Task RunOnLoop(WorkLoop loop, Func<Task> work)
		{
			if (!loop.IsCurrent && WorkLoop.Current != null)
			{
				loop.Post(work);
				return Task.CompletedTask;
			}

			return loop.Run(work);
		}

		// The per-zone half of the tick's bookkeeping: what the global loop kept once, each zone now keeps for itself.
		private sealed class ZoneClocks
		{
			public long LastTick { get; set; }
			public int ConsecutiveFailures { get; set; }
			public TickAccumulator Regen { get; } = new TickAccumulator(RegenIntervalSeconds);
			public TickAccumulator InterestSweep { get; } = new TickAccumulator(InterestSweepIntervalSeconds);
			public TickAccumulator AmbientMaintain { get; } = new TickAccumulator(AmbientMaintainIntervalSeconds);
			public TickMetrics Metrics { get; } = new TickMetrics(1.0 / BaseTickSeconds);
		}
	}
}
