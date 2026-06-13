using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Spawn;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Turns world-model events into wire packets: spawns/moves/despawns for interest management, NPC respawn/loot/mount
	// renders, loot-window and quest-journal packets (docs/archive/server-architecture-roadmap.md). Extracted out of the
	// GameServer listener so packet CONSTRUCTION for world events is its own collaborator; it reads the world model via
	// the WorldSimulation, loads templates from the DB, and frames every packet on the world conversation (WorldFraming:
	// sender = world-system 0x58, receiver = the recipient's GamePlayerType endpoint). The FA015807 body carries the
	// SUBJECT's handle (unchanged), so the recipient renders the subject as a separate entity. NEEDS IN-CLIENT
	// VERIFICATION with two connected players. Single-player is a no-op (no interest events).
	public sealed class WorldBroadcaster
	{
		// LootBagFullUpdateIIR_c - the world-object sub-opcode a loot bag spawns as.
		private const uint LootBagSubOpcode = 0xF096251E;

		// Maps whose NPCs have been loaded into the world model (once per map, on first entry).
		private readonly ConcurrentDictionary<uint, byte> _npcLoadedZones = new ConcurrentDictionary<uint, byte>();

		// Cached loot-bag template (a captured "Bag of loot" world object) per map; null values cache loot-less maps.
		private readonly ConcurrentDictionary<uint, WorldObject> _lootTemplateByMap = new ConcurrentDictionary<uint, WorldObject>();
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		private readonly WorldSimulation _sim;

		// Monotonic allocator for dropped-loot / summoned-entity handles, in a high range to avoid colliding with
		// player/NPC handles.
		private int _nextLootHandle = 0x40000000;

		public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_logger = logger;
		}

		// Despawn a world-owned entity (a killed NPC, an expired loot bag) for every player currently in range. These
		// are seen by everyone in the zone (burst / direct spawn), not tracked in per-player Known sets, so this is
		// range-based rather than ResolveRemove. Uses the same n3ToClientQuit packet as player despawns.
		public async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _sim
				         .World
				         .ZoneFor(entity.MapId)
				         .PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldDespawn(viewer.Client, viewer.MapId, entity.Handle);
			}
		}

		// Fade an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot bags
		// (consumed or expired). Loot bags carry the 0xC87A typetag.
		public async Task BroadcastEntityFade(WorldEntity entity)
		{
			var body = WorldLoot.BuildFadeBody(WorldLoot.LootTypeTag, entity.Handle, WorldLoot.DefaultFadeSeconds);
			foreach (var viewer in _sim
				         .World
				         .ZoneFor(entity.MapId)
				         .PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(viewer.Client), 0x2000)
					.WriteArrayUInt32Length(s => s.WriteArray(body))
					.SendAsync(viewer.Client);
			}
		}

		// An NPC respawned (its timer elapsed on the tick): re-render it for every player in range by re-sending its
		// stored FA015807 spawn body (re-framed + position-patched per recipient by SendWorldSpawn).
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim
				         .World
				         .ZoneFor(npc.MapId)
				         .PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, npc);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		public Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_sim.World.ZoneFor(leaver.MapId), leaver);
			return DispatchInterest(events, null);
		}

		// After world entry (spawn body now captured): spawn the player to nearby players and them to the player.
		// NPC streaming is deliberately NOT run here - the world-burst replay already rendered the entry-point NPCs
		// and piling more spawns onto the load can reshape the burst's TCP segmentation and stall the client. Instead
		// we seed EVERY in-range model NPC into the player's Known set (and register its handle -> name for
		// conversation routing), so the first move re-spawns none of them. The burst already rendered the NPCs retail
		// shows at this vantage; the remaining in-range model NPCs are cross-session capture dupes (the model is the
		// union of all sessions, ~2-4x retail's count) that retail does NOT render here - leaving them Known-but-unsent
		// keeps the entry view == the burst == retail and avoids the spawn flood that crashes the client on the first
		// move. NPCs OUTSIDE entry range (e.g. Casilda) are not seeded, so they still stream in as the player moves.
		public async Task BroadcastPlayerEntry(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity?.SpawnBody == null)
				return;

			var zone = _sim.World.ZoneFor(entity.MapId);

			// FAOLAN_NPC_INDIVIDUAL_SPAWN: NPCs were dropped from the burst (ReplayMapWorldBurst), so the interest
			// system is the SOLE NPC spawn path - stream every in-range NPC here at entry (includeNonPlayers), each
			// as its own FA015807 + name registration via DispatchInterest. No burst NPCs means no Known-seeding and
			// no double-spawn. Otherwise (default) the burst already rendered the entry NPCs: seed those into Known
			// so the first move doesn't re-spawn them, and only stream players here.
			if (Environment.GetEnvironmentVariable("FAOLAN_NPC_INDIVIDUAL_SPAWN") == "1")
			{
				var npcEvents = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, includeNonPlayers: true);
				await DispatchInterest(npcEvents, null);
				return;
			}

			var seeded = 0;
			foreach (var npc in zone.EntitiesInRange(entity.Position, WorldSimulation.PlayerInterestRange, entity.Handle))
			{
				if (npc.IsPlayer || npc.IsLoot || npc.Name == null)
					continue;

				// Mark it Known so the first move doesn't (re)spawn it, and register its handle -> name from the model
				// (authoritative) so the clicked-handle -> name -> conversation lookup resolves. Burst NPCs share the
				// burst wire handle (EnsureZoneNpcs aligns them by position), so this lines up with what the client
				// rendered; non-burst in-range dupes are simply never sent.
				entity.Known.Add(npc.Handle);
				client.Session.SpawnedNpcNamesByHandle[npc.Handle] = npc.Name;
				seeded++;
			}

			// Player-only interest (the burst already placed the NPCs; do NOT include non-players here or we re-stream
			// the whole zone). The ResolveMove player-only pass preserves the NPC Known entries seeded above.
			var events = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange);
			await DispatchInterest(events, null);
			_logger.LogInformation("World entry: {Name} seeded {Seeded} in-range NPC(s) into Known (suppresses first-move re-stream).", entity.Name, seeded);
		}

		// After a player's Move: update interest and broadcast the resulting spawns/moves to nearby players.
		public async Task BroadcastPlayerMove(INetworkClient client, uint moveSubop, uint dataObject, uint dataClientInstance, ushort movingType, Vector3 position, Vector3 rotation, byte[] tail)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			// Advance the player's model position to where they actually are BEFORE computing interest - otherwise it
			// stays frozen at the entry point and only entity ever within range of the wash-ashore spot streams (the
			// "most NPCs missing" bug). This is also what makes other players see this one at its live position.
			entity.Position = position;

			// includeNonPlayers: stream the zone's NPCs to this player as they move (the burst only rendered the NPCs
			// near the entry point; this fills in the rest of the map - e.g. Casilda, who is outside the captured
			// burst). Burst NPCs are already in the player's Known set (seeded at entry) so they are not re-spawned.
			var events = InterestManager.ResolveMove(_sim.World.ZoneFor(entity.MapId), entity, WorldSimulation.PlayerInterestRange, includeNonPlayers: true);
			var spawns = 0;
			var despawns = 0;
			foreach (var ev in events)
			{
				if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Spawn)
					spawns++;
				else if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Despawn)
					despawns++;
			}

			if (spawns > 0 || despawns > 0)
				_logger.LogInformation("Interest: {Player} at {Pos} -> streamed +{Spawns} / -{Despawns} entities (now knows {Known}).", entity.Name, entity.Position, spawns, despawns, entity.Known.Count);

			await DispatchInterest(events, new MoveForward(moveSubop, dataObject, dataClientInstance, movingType, position, rotation, tail));
		}

		// Stashes the player's own FA015807 body (the bytes after the RPC header) on their world entity, so the
		// broadcaster can re-frame it to spawn this player on other clients.
		public void CaptureOwnSpawnBody(INetworkClient client, byte[] spawnPacket)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null || spawnPacket.Length < 12)
				return;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(spawnPacket.AsSpan(8, 4));
			if (bodyStart <= spawnPacket.Length)
				entity.SpawnBody = spawnPacket[bodyStart..];
		}

		// Records the playfield token the client will associate with this zone, read from the 0x737A6DF9 snapshot we
		// actually send (so the despawn token matches what the client learned, by construction; see WorldDespawn). This
		// OVERRIDES the token WorldState minted at zone creation - for a replayed playfield the burst's value is the
		// authoritative one (the client learned it from the burst, not from us). Idempotent: the token is constant per
		// playfield, so a re-entry re-extracts the same value; logged only when it actually changes.
		public void CapturePlayfieldToken(uint mapId, ReadOnlySpan<byte> snapshotBody)
		{
			if (!WorldDespawn.TryExtractPlayfieldToken(snapshotBody, out var token))
				return;

			var zone = _sim.World.ZoneFor(mapId);
			if (zone.PlayfieldToken == token)
				return;

			zone.PlayfieldToken = token;
			_logger.LogInformation("World: map {Map} playfield token = 0x{Token:X8} (from burst; overrides minted fallback).", mapId, token);
		}

		// A player closed the loot window (top-level 0x2044): consume the bag - fade it out for nearby players and drop
		// it from the model.
		public async Task CloseLootBag(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim
				    .World
				    .ZoneFor(player.MapId)
				    .TryGet(bagHandle, out var bag) || !bag.IsLoot)
				return;

			await BroadcastEntityFade(bag);
			_sim.World.Remove(bag);
			_logger.LogInformation("Loot: {Name} closed loot bag 0x{Handle:X8}; faded + removed.", player.Name, bag.Handle);
		}

		// Loads a map's NPCs from NpcSpawns into the world model ONCE (on first entry), so the server has an
		// authoritative shared view of the map's NPCs - the model fix at the heart of the roadmap (NPCs are otherwise
		// only per-player burst replays the server doesn't track). The tick/AI/combat will drive these; clients still
		// render them from the burst for now, so this is model bookkeeping (no wire change).
		public async Task EnsureZoneNpcs(uint mapId)
		{
			if (!_npcLoadedZones.TryAdd(mapId, 0))
				return;

			var npcs = await _database.GetNpcSpawns(mapId);
			var zone = _sim.World.ZoneFor(mapId);

			// The handle the map's world-burst renders each NPC under, keyed by the NPC's POSITION. Burst NPCs MUST
			// carry their burst handle in the model so the per-player Known-set seeding lines up (entry seeds these)
			// and the first move never re-streams them. Matching by HANDLE is wrong: the burst is ONE capture session
			// while NpcSpawns is the union of all sessions, so the burst's wire handle equals NpcSpawns.CapturedHandle
			// for only ~18 of the beach's 42 burst NPCs - the other ~24 would be missed and re-streamed (the flood
			// behind the loader crash). Position is the stable identity (it is exactly how TryRebuildNpcSpawnFromDb
			// matches a burst packet to its row), so key the alignment on position instead.
			var burst = await _database.GetMapWorldBurst(mapId);
			var burstHandleByPosition = BuildBurstHandleByPosition(burst);

			// NpcSpawns accretes one row per capture observation, so the same physical NPC appears many times - the
			// same name at near-identical positions across sessions (e.g. Kalanthes captured 5×, all at his hideout).
			// Keyed-by-handle Add already collapses same-handle rows; this collapses the cross-session re-captures too,
			// by (name, coarse position cell), so the map isn't populated with stacked duplicates. Genuinely-separate
			// mobs of the same name (Pillagers spread across the beach) fall in different cells and all survive.
			// Burst-position rows are ordered FIRST so they win their cell - the kept entity is then the exact one the
			// burst rendered, and it inherits the burst's wire handle below so the two never stack.
			var seenCells = new HashSet<(string, int, int, int)>();
			foreach (var npc in npcs.OrderByDescending(n => burstHandleByPosition.ContainsKey(PositionKey(n.PositionX, n.PositionY, n.PositionZ))))
			{
				// Faction mobs (Community > 0) are legitimately MANY of the same name spread across the map - dedup by
				// (name, coarse cell): same-spot cross-session re-captures collapse, distinct mobs in different cells
				// survive. Friendly/neutral named NPCs (Community 0: quest-givers like Casilda/Kalanthes + a few neutral
				// critters) are UNIQUE - but their cross-session captures land at genuinely DIFFERENT spots (Casilda was
				// captured at ~361 AND ~448, 87 units apart), so cell dedup keeps phantom duplicates that bloat the
				// spawn set and confuse the client. Collapse those by NAME alone -> one instance (the burst-handle row
				// wins, ordered first, so the kept entity coincides with the burst-rendered one).
				const float cell = 8f;
				var dedupKey = npc.Community == 0
					? (npc.Name ?? "", 0, 0, 0)
					: (npc.Name ?? "", (int)(npc.PositionX / cell), (int)(npc.PositionY / cell), (int)(npc.PositionZ / cell));
				if (!seenCells.Add(dedupKey))
					continue;

				// Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
				// importer), falling back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
				// If the burst rendered an NPC at this position, adopt the handle the client already knows it by (so
				// Known-seeding matches and interest never re-spawns a duplicate); otherwise this NPC is burst-free and
				// uses its captured handle - it streams in on the player's first move (Casilda et al, outside the burst).
				var handle = burstHandleByPosition.TryGetValue(PositionKey(npc.PositionX, npc.PositionY, npc.PositionZ), out var bh)
					? bh
					: npc.CapturedHandle;
				var entity = WorldEntity.ForNpc(handle, mapId, new Vector3(npc.PositionX, npc.PositionY, npc.PositionZ), new Vector3(npc.RotationX, npc.RotationY, npc.RotationZ), npc.Name ?? "NPC", vitals);
				entity.Level = npc.Level;
				// Hostile = a faction creature (NpcSpawn.Community > 0, lifted from NpcStats.community by the importer).
				// community 0 is the neutral/friendly community (quest-givers like Casilda/Kalanthes AND neutral critters
				// like Scorpion/Guard Dog); faction mobs (poachers 59, scavengers 60, picts 317, crocs 374, ...) are > 0.
				entity.Hostile = npc.Community > 0;

				// Keep the NPC's FA015807 spawn body (addressed to the chosen handle) so interest/respawn can re-render
				// it for nearby players - built from the row by NpcSpawnBuilder, the same path the burst-replacement uses.
				if (NpcSpawnBuilder.TryBuild(npc, handle, out var spawnBody, freshSpawn: true))
					entity.SpawnBody = spawnBody;

				zone.Add(entity);
			}

			_logger.LogInformation("World: loaded {Count} NPC(s) into map {Map}'s zone ({Raw} rows, {Collapsed} cross-session re-captures collapsed; {BurstAligned} aligned to burst handles).", zone.Count, mapId, npcs.Length, npcs.Length - seenCells.Count, burstHandleByPosition.Count);
		}

		// Maps each FA015807 NPC the burst renders to (rounded position) -> the wire handle it renders under, by
		// rebuilding the burst record and reading the spawn body's transform. EnsureZoneNpcs uses this to give the
		// model NPC at that position the same handle the client already knows, so Known-seeding lines up.
		private static Dictionary<(int, int, int), uint> BuildBurstHandleByPosition(MapWorldBurst burst)
		{
			var map = new Dictionary<(int, int, int), uint>();
			if (burst?.Entities == null)
				return map;

			foreach (var e in burst.Entities)
			{
				if (e.IirType != 0xFA015807u)
					continue;

				var pkt = IirRecordCodec.Rebuild(e);
				if (pkt.Length < 12)
					continue;
				var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(pkt.AsSpan(8, 4));
				if (bodyStart + SpawnPacketBuilder.PosZOffset + 4 > pkt.Length)
					continue;

				var x = BinaryPrimitives.ReadSingleLittleEndian(pkt.AsSpan(bodyStart + SpawnPacketBuilder.PosXOffset, 4));
				var y = BinaryPrimitives.ReadSingleLittleEndian(pkt.AsSpan(bodyStart + SpawnPacketBuilder.PosYOffset, 4));
				var z = BinaryPrimitives.ReadSingleLittleEndian(pkt.AsSpan(bodyStart + SpawnPacketBuilder.PosZOffset, 4));
				map[PositionKey(x, y, z)] = e.Handle; // last write wins; the burst lists each entity once
			}

			return map;
		}

		// Quantizes a world position to a stable integer key (×100, rounded) - the same scheme RetailLoginReplay uses
		// to match burst packets to NpcSpawns rows, so the two agree on which NPC sits at a given spot.
		private static (int, int, int) PositionKey(float x, float y, float z)
		{
			return ((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), (int)MathF.Round(z * 100f));
		}

		// A player opened a world object (Interact UseObject). If it's a loot bag in their zone, OPEN ITS LOOT WINDOW
		// per the decoded flow (protocol_loot_interaction): assign the player as the current looter and send the
		// SimpleDynelVisualChange "Open" so the client shows the bag's contents. The bag STAYS in the world - it is
		// consumed on close (0x2044), not on open. Taking an item (0x2043 -> grant + LootRemoveItems) is the remaining,
		// in-client-gated piece. No-op for non-loot / unknown objects.
		public async Task OpenLootBag(INetworkClient client, uint objectHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim
				    .World
				    .ZoneFor(player.MapId)
				    .TryGet(objectHandle, out var bag) || !bag.IsLoot)
				return;

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldLoot.BuildNewCurrentLooterBody(bag.Handle, player.Handle)))
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldLoot.BuildOpenWindowBody(WorldLoot.LootTypeTag, bag.Handle)))
				.SendAsync(client);

			_logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
		}

		// On world entry: re-send the player's persisted active quests so their journal is restored.
		public async Task RestorePlayerQuests(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null)
				return;

			foreach (var quest in await _database.GetCharacterQuests(character.Id))
			{
				if (quest.State != QuestState.Active)
					continue;

				await SendQuestIir(client, quest.QuestId, WorldQuest.StateActive);
				if (quest.GoalRequired > 0)
					await SendQuestGoal(client, quest.QuestId, 0, quest.GoalProgress);
			}
		}

		// Sends a QuestGoal (objective progress) to the player.
		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldQuest.BuildQuestGoal(client.Session.Account.ClientInstance, questId, goalId, count)))
				.SendAsync(client);
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldQuest.BuildQuestIir(client.Session.Account.ClientInstance, questId, state)))
				.SendAsync(client);
		}

		// Spawns a mount entity (a captured FA015807 "Horse" NpcSpawn, e.g. "Fading Black Horse") at the player's
		// position - the summon's visible payoff. The cast is broadcast separately; the player actually RIDING
		// (the MountedRun movement transition) is the client's part and in-client-gated. The mount has no
		// vitals (not killable) and is non-hostile.
		public Task SpawnMountForPlayer(INetworkClient client, string mountName)
		{
			return SpawnNpcForPlayer(client, mountName, false);
		}

		// Spawns a captured NPC (any NpcSpawns row with spawn Data, matched by NAME - the stable key) at the
		// player's position and renders it for nearby players. Built byte-exact via NpcSpawnBuilder (like the
		// map-load NPC spawns), re-framed + position-patched per recipient by SendWorldSpawn. When killable, the
		// entity gets vitals/level/hostility from its row exactly like EnsureZoneNpcs, so it joins combat + loot
		// as a full world citizen. Returns the spawned entity, or null when no spawnable row matches the name.
		public async Task<WorldEntity> SpawnNpcForPlayer(INetworkClient client, string npcName, bool killable = true)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var npc = await _database.GetNpcSpawnByName(npcName);
			var handle = NextLootHandle(); // a fresh summoned-entity handle (high range, no player/NPC collision)
			if (npc == null || !NpcSpawnBuilder.TryBuild(npc, handle, out var body, freshSpawn: true))
			{
				_logger.LogInformation("Spawn: no spawnable '{Npc}' template found.", npcName);
				return null;
			}

			EntityVitals vitals = null;
			if (killable)
			{
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			}

			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npc.Name ?? npcName, vitals);
			entity.Level = npc.Level;
			entity.Hostile = killable && npc.Community > 0;
			entity.SpawnBody = body;
			_sim
				.World
				.ZoneFor(player.MapId)
				.Add(entity);

			foreach (var viewer in _sim
				         .World
				         .ZoneFor(player.MapId)
				         .PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Spawn: spawned {Npc} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
			return entity;
		}

		// Despawns a non-player entity by handle in the calling player's zone: the decoded n3ToClientQuit removal
		// to every player in range + drop from the model (the cleanup complement to SpawnNpcForPlayer; also works
		// on burst NPCs and loot bags). Returns the removed entity, or null when the handle is unknown or a player.
		public async Task<WorldEntity> DespawnEntityForPlayer(INetworkClient client, uint entityHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim
				    .World
				    .ZoneFor(player.MapId)
				    .TryGet(entityHandle, out var entity) || entity.IsPlayer)
				return null;

			foreach (var viewer in _sim
				         .World
				         .ZoneFor(player.MapId)
				         .PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldDespawn(viewer.Client, player.MapId, entityHandle);
			}

			_sim.World.Remove(entity);
			_logger.LogInformation("Despawn: removed {Name} 0x{Handle:X8} (by {Player}).", entity.Name, entityHandle, player.Name);
			return entity;
		}

		// A player clicked "loot all" (top-level 0x2043): hand the caller the bag's item templates to grant (via
		// DispatchAddItem) and clear them from the bag so each is granted exactly once. Empty when the bag is
		// unknown / not a loot bag / already emptied. Coins are a separate path (TryLootCash -> TakeLootCash).
		public IReadOnlyList<uint> TakeBagItems(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim
				    .World
				    .ZoneFor(player.MapId)
				    .TryGet(bagHandle, out var bag) || !bag.IsLoot || bag.LootItems.Count == 0)
				return Array.Empty<uint>();

			var taken = bag.LootItems.ToArray();
			bag.LootItems.Clear();
			_logger.LogInformation("Loot: {Player} loots {Count} item(s) from bag 0x{Handle:X8}.", player.Name, taken.Length, bagHandle);
			return taken;
		}

		// TryLootCash (0x2045): takes the bag's coins exactly once - returns the amount and zeroes it on the bag
		// (the caller credits the wallet + pushes the 0x2005 balance update). 0 = no bag / no coins / already taken.
		public uint TakeLootCash(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim
				    .World
				    .ZoneFor(player.MapId)
				    .TryGet(bagHandle, out var bag) || !bag.IsLoot)
				return 0;

			var cash = bag.CashTin;
			bag.CashTin = 0;
			return cash;
		}

		// On a kill, maybe drop a loot bag at the dead NPC's spot (LootResolver coin-flip). The bag is a world object
		// (LootBagFullUpdateIIR_c 0xF096251E) built from a captured "Bag of loot" row for the map, placed at the death
		// position with a fresh handle, added to the zone, and spawned for nearby players. It expires off the tick
		// (WorldState.Tick -> LootExpired -> despawn + remove). Opening/looting the bag is a separate (gated) step.
		public async Task TrySpawnLoot(WorldEntity deadNpc)
		{
			if (!LootResolver.RollsDrop(Random.Shared.NextDouble()))
				return;

			var template = await GetLootTemplate(deadNpc.MapId);
			if (template == null)
				return;

			var items = await RollLootItems(Random.Shared.Next(0, 3)); // 0-2 items on a kill
			var cash = LootResolver.RollCash(Random.Shared.NextDouble(), deadNpc.Level);
			var bag = await PlaceLootBag(template, deadNpc.MapId, deadNpc.Position, deadNpc.Rotation, cash, items);
			if (bag != null)
				_logger.LogInformation("Loot: dropped {Name} 0x{Handle:X8} at {Pos} on map {Map} ({Items} items, {Cash} tin; expires {Expiry}s).",
					bag.Name, bag.Handle, deadNpc.Position, deadNpc.MapId, items.Count, cash, LootResolver.LootExpirySeconds);
		}

		// Spawns a loot bag at the player's feet with a forced item/cash payload (the same bag a kill can drop) -
		// the .spawnbag test bootstrap. Null when the player isn't in the world or the map has no captured loot-bag
		// template to render from.
		public async Task<WorldEntity> SpawnLootBagForPlayer(INetworkClient client, IReadOnlyList<uint> itemTemplates, uint cashTin)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var template = await GetLootTemplate(player.MapId);
			if (template == null)
				return null;

			var bag = await PlaceLootBag(template, player.MapId, player.Position, player.Rotation, cashTin, itemTemplates);
			if (bag != null)
				_logger.LogInformation("Loot: .spawnbag {Name} 0x{Handle:X8} for {Player} ({Items} items, {Cash} tin).",
					bag.Name, bag.Handle, player.Name, itemTemplates.Count, cashTin);
			return bag;
		}

		// Builds the loot-bag world object from the map template, stamps its cash + item payload, adds it to the
		// zone, and renders it for nearby players. Returns the bag, or null if the body couldn't be built. Shared
		// by the kill-drop (TrySpawnLoot) and the .spawnbag command.
		private async Task<WorldEntity> PlaceLootBag(WorldObject template, uint mapId, Vector3 position, Vector3 rotation, uint cashTin, IReadOnlyList<uint> itemTemplates)
		{
			var handle = NextLootHandle();
			if (!WorldObjectBuilder.TryBuild(template, handle, position, new Quaternion(rotation.X, rotation.Y, rotation.Z, 1f), out var body))
				return null;

			var bag = WorldEntity.ForLoot(handle, mapId, position, rotation, template.Name ?? "Bag of loot", body, LootResolver.LootExpirySeconds);
			bag.CashTin = cashTin;
			if (itemTemplates != null)
				bag.LootItems.AddRange(itemTemplates);
			_sim
				.World
				.ZoneFor(mapId)
				.Add(bag);

			foreach (var viewer in _sim
				         .World
				         .ZoneFor(mapId)
				         .PlayersInRange(bag.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, bag);
			}

			return bag;
		}

		// A few random snapshot-buildable item templates (Archetype != 0) for a loot bag. Empty when count <= 0
		// or the item table has no buildable rows.
		private async Task<List<uint>> RollLootItems(int count)
		{
			if (count <= 0)
				return new List<uint>();

			var items = await _database.GetRandomBuildableItems(count);
			return items
				.Select(i => i.Id)
				.ToList();
		}

		// Dispatches interest events. `move` (the mover's Move components) is required to forward Move events and is
		// null on entry (entry produces only Spawn/Despawn).
		private async Task DispatchInterest(IReadOnlyList<InterestEvent> events, MoveForward? move)
		{
			foreach (var ev in events)
			{
				var recipient = ev.Recipient.Client;
				if (recipient == null)
					continue;

				switch (ev.Type)
				{
					case InterestEventType.Spawn when ev.Subject.SpawnBody != null:
						// Register a streamed-in NPC's handle -> name so clicking it routes to the right conversation
						// (the same map the world-burst spawns use; players are not conversation targets).
						if (!ev.Subject.IsPlayer && !ev.Subject.IsLoot && ev.Subject.Name != null)
							recipient.Session.SpawnedNpcNamesByHandle[ev.Subject.Handle] = ev.Subject.Name;
						await SendWorldSpawn(recipient, ev.Subject);
						break;
					case InterestEventType.Move when move.HasValue:
						await SendWorldMove(recipient, move.Value);
						break;
					case InterestEventType.Despawn:
						await SendWorldDespawn(recipient, ev.Recipient.MapId, ev.Subject.Handle);
						break;
				}
			}
		}

		// The captured loot-bag template (LootBagFullUpdateIIR_c) for a map, cached on first use. Null if the map has
		// no captured loot bag to template from.
		private async Task<WorldObject> GetLootTemplate(uint mapId)
		{
			if (_lootTemplateByMap.TryGetValue(mapId, out var cached))
				return cached;

			var objects = await _database.GetWorldObjects(mapId);
			WorldObject template = null;
			foreach (var o in objects)
			{
				if (o.SubOpcode == LootBagSubOpcode && o.Name != null && o.Name.Contains("loot", StringComparison.OrdinalIgnoreCase))
				{
					template = o;
					break;
				}
			}

			_lootTemplateByMap[mapId] = template; // cache misses too (null) so we don't re-query a loot-less map
			return template;
		}

		private uint NextLootHandle()
		{
			return (uint)Interlocked.Increment(ref _nextLootHandle);
		}

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (WorldDespawn). Stamped with the
		// recipient zone's playfield token: the burst's value for a replayed playfield (what the client learned at
		// entry), else WorldState's minted fallback. The token-is-0 guard is just defensive (ZoneFor always mints one).
		private Task SendWorldDespawn(INetworkClient recipient, uint recipientMapId, uint subjectHandle)
		{
			var token = _sim.World.ZoneFor(recipientMapId)
				.PlayfieldToken;
			if (token == 0)
			{
				_logger.LogDebug("World: skip despawn {Subject:X8} for a viewer on map {Map} (playfield token unknown).", subjectHandle, recipientMapId);
				return Task.CompletedTask;
			}

			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDespawn.BuildBody(subjectHandle, token)))
				.SendAsync(recipient);
		}

		// Forwards a player's Move to `recipient` (same body - the moving entity's handle + transform - only the
		// receiver differs), so the recipient sees the subject move.
		private Task SendWorldMove(INetworkClient recipient, MoveForward move)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), 0x2000)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(move.Subop)
					.WriteUInt32(move.DataObject)
					.WriteUInt32(move.DataClientInstance)
					.WriteUInt16(move.MovingType)
					.WriteVector3BigEndian(move.Position)
					.WriteVector3BigEndian(move.Rotation)
					.WriteArray(move.Tail))
				.SendAsync(recipient);
		}

		// Sends the subject's FA015807 spawn to `recipient`, re-framed to the recipient and patched to the subject's
		// CURRENT position (its captured spawn body holds its entry position, which may be stale).
		private Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject)
		{
			var body = (byte[])subject.SpawnBody.Clone();
			if (body.Length >= SpawnPacketBuilder.PosZOffset + 4)
			{
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosXOffset, 4), subject.Position.X);
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosYOffset, 4), subject.Position.Y);
				BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosZOffset, 4), subject.Position.Z);
			}

			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(recipient), 0x2000)
				.WriteArray(body)
				.SendAsync(recipient);
		}

		// The components of a player's Move, forwarded verbatim to other viewers (only the receiver differs).
		private readonly record struct MoveForward(uint Subop, uint DataObject, uint DataClientInstance, ushort MovingType, Vector3 Position, Vector3 Rotation, byte[] Tail);
	}
}
