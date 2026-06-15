using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The wire side of the world simulation: the operations the tick needs to push to clients. Implemented by the
	// GameServer listener (which owns the packet framing + send), so the simulation depends on this narrow port
	// rather than on the whole listener. Lets the simulation own world STATE + TIMING without owning networking.
	public interface IWorldOutbound
	{
		// Gracefully fade an entity out for the players in range (n3ToClientFade) - used for expired loot.
		Task BroadcastEntityFade(WorldEntity entity);

		// Re-render a respawned NPC for the players in range (its stored FA015807 spawn body).
		Task BroadcastNpcRespawn(WorldEntity npc);

		// Convert a dead NPC's corpse to a lootable bag in place (after its death-animation linger), or despawn it if
		// no loot bag can be built for the map.
		Task ConvertCorpseToLoot(WorldEntity npc);

		// Push a 0x96B8DC59 vitals update for the player (regen tick, NPC-attack damage).
		Task PushVitals(INetworkClient client, EntityVitals vitals);

		// Re-run the world-entry render path for the player (used to respawn them at a resurrection point).
		Task ReenterWorld(INetworkClient client);

		// Push the death/release screen: DispatchValidResurrectionSpots with the death position + the spots the
		// player may resurrect at. The client replies with RespawnMe(spawnPointId) (handled inbound).
		Task SendResurrectionScreen(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots);
	}

	// The live in-memory world (docs/archive/server-architecture-roadmap.md): owns the world model + the connected players'
	// authoritative transforms/vitals, and drives the 10 Hz simulation tick (NPC respawn/AI, vitals regen, player
	// death/respawn, resurrection-point discovery). Extracted out of GameServerListener so the simulation's state +
	// timing are no longer fused with the networking front-door; it reaches the wire through IWorldOutbound. Packet
	// CONSTRUCTION (spawn/move/loot broadcasts) and persistence still live on the listener for now (see the
	// architecture roadmap's later decomposition increments).
	public sealed class WorldSimulation
	{
		// How far a player perceives other players (world-entity interest radius; wider than say range).
		public const float PlayerInterestRange = 150f;

		// NPC auto-attack damage. The hostile-NPC swing is delivered as model-only damage via the 0x96B8DC59 vitals
		// packet; the attack ANIMATION ack is still undecoded (see protocol_stats_and_combat), so the hit is invisible.
		// CAVEAT (why this was gated off): on a zone with many hostiles (Tortage Beach has ~400) a player who keeps
		// dying can hit a death-loop (re-enters the world every ~10s, never finishes loading). Re-enabled to exercise
		// real combat death (and the death/release screen #214); the proper fix is decoding the attack-animation ack.
		private const bool NpcAutoAttackEnabled = true;

		// Base game-loop cadence: 10 Hz. Continuous systems run every base tick with the real elapsed dt so they stay
		// smooth; slower rate-based systems run via a TickAccumulator at their own interval. The loop measures
		// wall-clock dt, not the nominal period, so a late tick doesn't desync the rates.
		private const double BaseTickSeconds = 0.1;

		// How long a killed player lies dead before respawning at the closest known resurrection point.
		private const double PlayerRespawnDelaySeconds = 10.0;

		// Vitals regen cadence (rate-based; dt-scaled by the real elapsed time when it fires).
		private const double RegenIntervalSeconds = 1.0;

		// How close a player must get to discover a resurrection point.
		private const float RespawnDiscoveryRange = 15f;

		// Local-say range in world units. Built-NPC tests showed a +4-unit offset renders "beside" the player, so
		// units are ~metres; ~50 is a generous "nearby" for /say. Tunable; same-map is the hard filter either way.
		private const float SayRangeUnits = 50f;

		// Players currently dead, with the seconds remaining until respawn (counted down every tick).
		private readonly ConcurrentDictionary<INetworkClient, double> _playerRespawnRemaining = new ConcurrentDictionary<INetworkClient, double>();
		private readonly ConcurrentDictionary<uint, HashSet<uint>> _discoveredRespawnByCharacter = new ConcurrentDictionary<uint, HashSet<uint>>();

		// Per-map resurrection points + per-character discovered sets, cached to keep the discovery tick off the DB.
		private readonly ConcurrentDictionary<uint, RespawnPoint[]> _respawnPointsByMap = new ConcurrentDictionary<uint, RespawnPoint[]>();
		private readonly IAgentServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		private readonly IWorldOutbound _output;

		private Task _tickTask = Task.CompletedTask;
		public ConcurrentDictionary<INetworkClient, WorldEntity> Players { get; } = new ConcurrentDictionary<INetworkClient, WorldEntity>();

		// The live world model + the connected players' authoritative world entities (transform + vitals).
		public WorldState World { get; } = new WorldState();

		public WorldSimulation(IWorldOutbound output, IDatabaseRepository database, IAgentServerDispatcher agents, ILogger logger)
		{
			_output = output;
			_database = database;
			_agents = agents;
			_logger = logger;
		}

		// The player's live world entity for this connection (authoritative transform + vitals), or null if not in the
		// world yet.
		public WorldEntity PlayerWorldEntity(INetworkClient client)
		{
			return Players.TryGetValue(client, out var entity)
				? entity
				: null;
		}

		// Registers (or relocates) the player in the live world after world entry, SEEDING the authoritative WorldEntity
		// transform + vitals from the persisted Character. After this the WorldEntity is the source of truth for live
		// position/vitals; the Character is only re-read on a fresh entry/teleport. Idempotent (GetOrAdd keeps the
		// existing entity, so vitals aren't reset across re-entry/teleport/respawn).
		public void RegisterPlayerInWorld(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null)
				return;

			var handle = client.Session.Account.ClientInstance;
			var entity = Players.GetOrAdd(client, _ => new WorldEntity(handle, character.MapId, true, character.Name ?? $"Character{handle}", client) { Vitals = EntityVitals.FromCharacter(character) });
			// The DB rotation is a quaternion; the live sim/movement layer is Vector3 (Euler degrees) - bridge it.
			var zone = World.Place(entity, character.MapId, character.Position, character.Rotation.ToVector3());
			var nearby = zone
				.PlayersInRange(character.Position, PlayerInterestRange, handle)
				.Count();
			_logger.LogInformation("World: player {Name} (0x{Handle:X8}) entered map {Map} at {Pos}; {Nearby} other player(s) in range.", entity.Name, handle, character.MapId, character.Position, nearby);
		}

		// Removes the player from the world model on disconnect. The caller broadcasts the despawn FIRST (while the
		// leaver is still in the zone so its viewers resolve), then calls this. Returns false if the player wasn't in
		// the world.
		public bool RemovePlayer(INetworkClient client, out WorldEntity entity)
		{
			if (!Players.TryRemove(client, out entity))
				return false;

			World.Remove(entity);
			_playerRespawnRemaining.TryRemove(client, out _);
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
				if (sender.Session?.Character != null)
					ids.Add(sender.Session.Character.Id);
				return ids;
			}

			ids.Add(entity.Handle);
			foreach (var near in World
				         .ZoneFor(entity.MapId)
				         .PlayersInRange(entity.Position, SayRangeUnits, entity.Handle))
				ids.Add(near.Handle);

			return ids;
		}

		// Starts the simulation tick on the given lifetime token (owned by the listener so startup/shutdown stay in one
		// place). The task is held so StopAsync can await a clean exit.
		public void Start(CancellationToken cancellationToken)
		{
			_tickTask = RunTickLoop(cancellationToken);
		}

		// Awaits the tick loop's (cancelled) exit. The loop swallows its own OperationCanceledException, so this
		// completes normally once the lifetime token has been cancelled by the listener.
		public Task StopAsync()
		{
			return _tickTask;
		}

		// Applies an aggroed NPC's swing to the target player: reduce the player's live vitals and push a 0x96B8DC59
		// update so their HP bar drops. The NPC's attack ANIMATION packet isn't decoded yet (see
		// protocol_stats_and_combat), so this delivers the damage effect only.
		private async Task ApplyNpcAttack(WorldEntity npc, WorldEntity target)
		{
			var client = target?.Client;
			var character = client?.Session?.Character;
			if (character == null)
				return;

			var vitals = target.Vitals ??= EntityVitals.FromCharacter(character);
			if (vitals.IsDead)
				return;

			var damage = (int)CombatResolver.ResolveNpcAttackDamage(npc.Level);
			vitals.ApplyHealthDelta(-damage);
			await _output.PushVitals(client, vitals);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {Npc}.", character.Name, npc.Name);
		}

		// Discovers any resurrection point a living player has walked near (so they can respawn there later). Run at the
		// regen cadence (1 Hz) to keep it cheap; always-known points need no discovery. Persists + notifies on a new find.
		private async Task DiscoverNearbyRespawnPoints()
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var character = client.Session?.Character;
				if (character == null)
					continue;

				var entity = pair.Value;
				var points = await GetRespawnPointsCached(entity.MapId);
				if (points.Length == 0)
					continue;

				var discovered = await GetDiscoveredRespawnCached(character.Id);
				foreach (var point in points)
				{
					if (point.AlwaysKnown || discovered.Contains(point.Id))
						continue;
					if (Vector3.Distance(point.Position, entity.Position) > RespawnDiscoveryRange)
						continue;

					await _database.DiscoverRespawnPoint(character.Id, point.Id);
					discovered.Add(point.Id);
					await _agents.SendSystemMessage(character.Id, $"Resurrection point discovered: {point.Name}");
					_logger.LogInformation("World: {Player} discovered resurrection point '{Point}'.", character.Name, point.Name);
				}
			}
		}

		// A character's discovered respawn-point ids, cached (loaded once, updated on discovery) to keep the discovery
		// tick off the DB.
		private async Task<HashSet<uint>> GetDiscoveredRespawnCached(uint characterId)
		{
			if (_discoveredRespawnByCharacter.TryGetValue(characterId, out var cached))
				return cached;

			var set = new HashSet<uint>(await _database.GetDiscoveredRespawnPointIds(characterId));
			_discoveredRespawnByCharacter[characterId] = set;
			return set;
		}

		// A map's resurrection points, cached on first use (rarely changes within a run).
		private async Task<RespawnPoint[]> GetRespawnPointsCached(uint mapId)
		{
			if (_respawnPointsByMap.TryGetValue(mapId, out var cached))
				return cached;

			var points = await _database.GetRespawnPoints(mapId);
			_respawnPointsByMap[mapId] = points;
			return points;
		}

		// Regenerates each connected player's vitals by `elapsedSeconds` worth and pushes a 0x96B8DC59 update only when
		// a pool actually rose (a player at full HP/stamina/mana costs nothing). Rates are provisional fractions of max
		// per second, scaled by the elapsed time so the cadence is independent of the base tick rate.
		private async Task RegenerateConnectedPlayers(double elapsedSeconds)
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var character = client.Session?.Character;
				if (character == null)
					continue;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character);
				if (vitals.IsDead)
					continue; // corpses don't regen - the respawn timer revives them

				var changed = vitals.Regenerate(RegenAmount(vitals.MaxHealth, 0.05, elapsedSeconds), // ~5% hp / sec (provisional)
					RegenAmount(vitals.MaxStamina, 0.10, elapsedSeconds), // ~10% stamina / sec
					RegenAmount(vitals.MaxMana, 0.05, elapsedSeconds)); // ~5% mana / sec
				if (changed)
					await _output.PushVitals(client, vitals);
			}
		}

		// The player's "release/resurrect" click (GameCharAgent.RespawnMe 0x2024) - retail respawn is click-driven,
		// the tick timer is only the fallback. Respawns immediately if the player is actually dead; returns false
		// (and does nothing) otherwise.
		public async Task<bool> TryRespawnNow(INetworkClient client)
		{
			if (!Players.TryGetValue(client, out var entity) || client.Session?.Character == null)
				return false;

			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			_playerRespawnRemaining.TryRemove(client, out _);
			await RespawnPlayer(client, vitals);
			return true;
		}

		// True while the player is dead and awaiting resurrection (used to route the c2s 0x2024 to RespawnMe rather
		// than the login ReadyForPlayScreen - they share the opcode).
		public bool IsAwaitingRespawn(INetworkClient client) =>
			Players.TryGetValue(client, out var entity) && entity.Vitals?.IsDead == true;

		// The player clicked a spot on the release screen: RespawnMe(spawnPointId) (c2s 0x2024). spawnPointId is the
		// RespawnPoint.Id we sent in DispatchValidResurrectionSpots, echoed back. Revive at that point; if the id
		// doesn't resolve, fall back to the closest-known logic (RespawnPlayer). No-op if the player isn't dead.
		public async Task<bool> RespawnAtChosenPoint(INetworkClient client, uint spawnPointId)
		{
			if (!Players.TryGetValue(client, out var entity) || client.Session?.Character == null)
				return false;
			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			var point = await _database.GetRespawnPoint(spawnPointId);
			if (point == null)
			{
				await TryRespawnNow(client); // unknown id - closest-known fallback
				return true;
			}

			_playerRespawnRemaining.TryRemove(client, out _);
			var character = client.Session.Character;
			vitals.ReviveToFull();
			character.Position = point.Position;
			character.Rotation = point.Rotation;
			character.MapId = point.MapId;
			_logger.LogInformation("Combat: {Player} resurrected at chosen point '{Point}' (map {Map}).", character.Name, point.Name, point.MapId);
			await _output.ReenterWorld(client);
			return true;
		}

		// Revives a dead player: full vitals, moved to the CLOSEST resurrection point they know (the always-known
		// starter + any discovered; RespawnResolver), and re-entered (the proven world-entry render path, same as a
		// teleport) so they reappear alive away from whatever killed them. Falls back to the map's spawn point if the
		// map has no points.
		private async Task RespawnPlayer(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			vitals.ReviveToFull();

			var deathPos = PlayerWorldEntity(client)
				?.Position ?? character.Position;
			var points = await GetRespawnPointsCached(character.MapId);
			var target = points.Length > 0
				? RespawnResolver.ClosestKnown(deathPos, points, await GetDiscoveredRespawnCached(character.Id))
				: null;

			if (target != null)
			{
				character.Position = target.Position;
				character.Rotation = target.Rotation;
				_logger.LogInformation("Combat: {Player} respawned at '{Point}' (map {Map}).", character.Name, target.Name, character.MapId);
			}
			else
			{
				// No point the player KNOWS - fall back to the map's default spawn (AlwaysKnown starter, else any
				// point on the map; the points are already loaded above). If the map defines none, respawn in place.
				var fallback = points.FirstOrDefault(p => p.AlwaysKnown) ?? points.FirstOrDefault();
				if (fallback != null)
				{
					character.Position = fallback.Position;
					character.Rotation = fallback.Rotation;
					_logger.LogInformation("Combat: {Player} respawned at the default spawn '{Point}' (map {Map}; no known resurrection point).", character.Name, fallback.Name, character.MapId);
				}
				else
					_logger.LogInformation("Combat: {Player} respawned in place (map {Map} has no resurrection point).", character.Name, character.MapId);
			}

			await _output.ReenterWorld(client);
		}

		// On death: show the client the release screen (DispatchValidResurrectionSpots) so the player can pick where to
		// resurrect, instead of silently sitting at 0 HP until the auto-timer fires. We send the single closest known
		// point (the captures only ever carried one spot; the multi-spot framing isn't pinned). The client replies with
		// RespawnMe(spawnPointId), handled inbound. If the map has no point, skip (the auto-timer still revives).
		private async Task SendDeathScreen(INetworkClient client, Character character, Vector3 deathPosition)
		{
			var points = await GetRespawnPointsCached(character.MapId);
			if (points.Length == 0)
				return;

			var discovered = await GetDiscoveredRespawnCached(character.Id);
			var spot = RespawnResolver.ClosestKnown(deathPosition, points, discovered)
				?? points.FirstOrDefault(p => p.AlwaysKnown)
				?? points[0];
			await _output.SendResurrectionScreen(client, deathPosition, new[] { spot });
		}

		// The server game loop (docs/archive/server-architecture-roadmap.md): a 10 Hz base tick that advances continuous systems
		// every tick with the REAL elapsed dt (NPC respawn/AI, player death timers) and runs rate-based systems (vitals
		// regen) via a TickAccumulator at their own interval, dt-scaled. Wall-clock dt is measured so a late tick
		// doesn't desync the rates. Cancelled on shutdown.
		private async Task RunTickLoop(CancellationToken cancellationToken)
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BaseTickSeconds));
			var regen = new TickAccumulator(RegenIntervalSeconds);
			var lastTimestamp = Stopwatch.GetTimestamp();
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var now = Stopwatch.GetTimestamp();
					var dt = Stopwatch.GetElapsedTime(lastTimestamp, now)
						.TotalSeconds;
					lastTimestamp = now;

					// Continuous-time systems: every base tick, with the actual elapsed dt. The tick returns the
					// wire-visible effects (NPC respawns, loot expiry, NPC attacks) for us to broadcast.
					foreach (var effect in World.Tick(dt))
					{
						switch (effect.Type)
						{
							case WorldTickEffectType.NpcRespawned:
								await _output.BroadcastNpcRespawn(effect.Entity);
								break;
							case WorldTickEffectType.NpcCorpseLootable:
								await _output.ConvertCorpseToLoot(effect.Entity);
								break;
							case WorldTickEffectType.LootExpired:
								await _output.BroadcastEntityFade(effect.Entity); // graceful fade-out (n3ToClientFade)
								World.Remove(effect.Entity);
								break;
							case WorldTickEffectType.NpcAttack:
								// Gated off until the attack animation is decoded - see NpcAutoAttackEnabled. Invisible
								// model damage was killing players in a respawn/world-re-entry death-loop.
								if (NpcAutoAttackEnabled)
									await ApplyNpcAttack(effect.Entity, effect.Target);
								break;
						}
					}

					// Player death/respawn timers run every tick (continuous), scaled by real dt.
					await TickPlayerDeaths(dt);
					TickBuffExpiry();

					// Rate-based systems: fire on their own cadence, scaled by the time actually elapsed since last fire.
					if (regen.Advance(dt, out var regenElapsed))
					{
						await RegenerateConnectedPlayers(regenElapsed);
						await DiscoverNearbyRespawnPoints();
					}
				}
			}
			catch (OperationCanceledException)
			{
				// server shutting down
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Game tick loop failed.");
			}
		}

		// Advances player death/respawn. A player whose vitals hit 0 starts a respawn countdown; when it elapses they
		// are revived to full and re-entered at the closest known resurrection point (RespawnPlayer). Living players are
		// untracked.
		// Drops expired buffs (those with a known duration - none until the RDB spell defs land, but the model is
		// live). No packet: the client expires its own icon from the ApplySpell data.
		private void TickBuffExpiry()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				foreach (var buff in pair.Value.ActiveBuffs)
				{
					if (buff.Value.Expired(now) && pair.Value.ActiveBuffs.TryRemove(buff.Key, out _))
						_logger.LogInformation("Buff 0x{Buff:X8} expired on {Player}.", buff.Key, pair.Key.Session?.Character?.Name);
				}
			}
		}

		private async Task TickPlayerDeaths(double dt)
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var character = client.Session?.Character;
				if (character == null)
					continue;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character);
				if (!vitals.IsDead)
				{
					_playerRespawnRemaining.TryRemove(client, out _);
					continue;
				}

				if (!_playerRespawnRemaining.TryGetValue(client, out var remaining))
				{
					_playerRespawnRemaining[client] = PlayerRespawnDelaySeconds;
					_logger.LogInformation("Combat: {Player} died; respawning in {Delay}s.", character.Name, PlayerRespawnDelaySeconds);
					await SendDeathScreen(client, character, pair.Value.Position);
					continue;
				}

				remaining -= dt;
				if (remaining > 0)
				{
					_playerRespawnRemaining[client] = remaining;
					continue;
				}

				_playerRespawnRemaining.TryRemove(client, out _);
				await RespawnPlayer(client, vitals);
			}
		}

		// The per-`elapsedSeconds` regen for one pool: `fraction` of max per second, scaled by elapsed. Rounds up to at
		// least 1 so a pool below max always ticks toward it (never stalls on rounding).
		private static uint RegenAmount(uint max, double fraction, double elapsedSeconds)
		{
			var amount = (uint)(max * fraction * elapsedSeconds);
			return amount > 0
				? amount
				: 1;
		}
	}
}
