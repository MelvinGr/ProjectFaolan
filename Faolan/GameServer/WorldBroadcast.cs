using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Spawn;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Turns interest-management events (InterestManager) into wire packets, so nearby players see each other
	// (docs/server-architecture-roadmap.md). All spawns/moves use the world conversation framing verified against the
	// captures: sender = LoginWorldSender, receiver = the recipient's GamePlayerType endpoint (LoginWorldReceiver);
	// the FA015807 body carries the SUBJECT's handle (unchanged), so the recipient renders the subject as a separate
	// entity. NEEDS IN-CLIENT VERIFICATION with two connected players. Single-player is a no-op (no events).
	public partial class GameServerListener
	{
		// The components of a player's Move, forwarded verbatim to other viewers (only the receiver differs).
		private readonly record struct MoveForward(uint Subop, uint DataObject, uint DataClientInstance, ushort MovingType, Vector3 Position, Vector3 Rotation, byte[] Tail);

		// Dispatches interest events. `move` (the mover's Move components) is required to forward Move events and is
		// null on entry (entry produces only Spawn/Despawn). Despawn isn't sent yet (the remove packet isn't built);
		// it's logged so a stale ghost is traceable.
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
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(recipient), 0x2000)
				.WriteArray(body)
				.SendAsync(recipient);
		}

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (WorldDespawn). Stamped with the
		// recipient zone's playfield token: the burst's value for a replayed playfield (what the client learned at
		// entry), else WorldState's minted fallback. The token-is-0 guard is just defensive (ZoneFor always mints one).
		// NEEDS IN-CLIENT VERIFICATION with two players (the only open question is whether the client enforces the
		// token match or removes regardless).
		private Task SendWorldDespawn(INetworkClient recipient, uint recipientMapId, uint subjectHandle)
		{
			var token = _simulation.World.ZoneFor(recipientMapId).PlayfieldToken;
			if (token == 0)
			{
				Logger.LogDebug("World: skip despawn {Subject:X8} for a viewer on map {Map} (playfield token unknown).", subjectHandle, recipientMapId);
				return Task.CompletedTask;
			}

			return new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(recipient), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDespawn.BuildBody(subjectHandle, token)))
				.SendAsync(recipient);
		}

		// Forwards a player's Move to `recipient` (same body - the moving entity's handle + transform - only the
		// receiver differs), so the recipient sees the subject move.
		private Task SendWorldMove(INetworkClient recipient, MoveForward move)
		{
			return new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(recipient), 0x2000)
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

		// Stashes the player's own FA015807 body (the bytes after the RPC header) on their world entity, so the
		// broadcaster can re-frame it to spawn this player on other clients.
		private void CaptureOwnSpawnBody(INetworkClient client, byte[] spawnPacket)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null || spawnPacket.Length < 12)
				return;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(spawnPacket.AsSpan(8, 4));
			if (bodyStart <= spawnPacket.Length)
				entity.SpawnBody = spawnPacket[bodyStart..];
		}

		// Loads a map's NPCs from NpcSpawns into the world model ONCE (on first entry), so the server has an
		// authoritative shared view of the map's NPCs - the model fix at the heart of the roadmap (NPCs are otherwise
		// only per-player burst replays the server doesn't track). The tick/AI/combat will drive these; clients still
		// render them from the burst for now, so this is model bookkeeping (no wire change).
		private async Task EnsureZoneNpcs(uint mapId)
		{
			if (!_npcLoadedZones.TryAdd(mapId, 0))
				return;

			var npcs = await Database.GetNpcSpawns(mapId);
			var zone = _simulation.World.ZoneFor(mapId);
			foreach (var npc in npcs)
			{
				// Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
				// importer), falling back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0 ? npc.Health : Combat.CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = Combat.EntityVitals.FromMax(maxHealth, 0, maxHealth);
				var entity = WorldEntity.ForNpc(npc.CapturedHandle, mapId,
					new Vector3(npc.PositionX, npc.PositionY, npc.PositionZ),
					new Vector3(npc.RotationX, npc.RotationY, npc.RotationZ),
					npc.Name ?? "NPC", vitals);
				entity.Level = npc.Level;
				// Hostile = a faction creature (NpcSpawn.Community > 0, lifted from NpcStats.community by the importer).
				// community 0 is the neutral/friendly community (quest-givers like Casilda/Kalanthes AND neutral critters
				// like Scorpion/Guard Dog); faction mobs (poachers 59, scavengers 60, picts 317, crocs 374, ...) are > 0.
				entity.Hostile = npc.Community > 0;

				// Keep the NPC's FA015807 spawn body so respawn can re-render it for nearby players (built byte-exact
				// from the row by NpcSpawnBuilder, the same path the burst-replacement uses).
				if (NpcSpawnBuilder.TryBuild(npc, npc.CapturedHandle, out var spawnBody))
					entity.SpawnBody = spawnBody;

				zone.Add(entity);
			}

			Logger.LogInformation("World: loaded {Count} NPC(s) into map {Map}'s zone.", npcs.Length, mapId);
		}

		// After a player's Move: update interest and broadcast the resulting spawns/moves to nearby players.
		private async Task BroadcastPlayerMove(INetworkClient client, uint moveSubop, uint dataObject, uint dataClientInstance, ushort movingType, Vector3 position, Vector3 rotation, byte[] tail)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null)
				return;

			var events = InterestManager.ResolveMove(_simulation.World.ZoneFor(entity.MapId), entity, World.WorldSimulation.PlayerInterestRange);
			await DispatchInterest(events, new MoveForward(moveSubop, dataObject, dataClientInstance, movingType, position, rotation, tail));
		}

		// After world entry (spawn body now captured): spawn the player to nearby players and them to the player.
		private async Task BroadcastPlayerEntry(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity?.SpawnBody == null)
				return;

			var events = InterestManager.ResolveMove(_simulation.World.ZoneFor(entity.MapId), entity, World.WorldSimulation.PlayerInterestRange);
			await DispatchInterest(events, move: null);
		}

		// Despawn a world-owned entity (a killed NPC, an expired loot bag) for every player currently in range. These
		// are seen by everyone in the zone (burst / direct spawn), not tracked in per-player Known sets, so this is
		// range-based rather than ResolveRemove. Uses the same n3ToClientQuit packet as player despawns.
		private async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _simulation.World.ZoneFor(entity.MapId).PlayersInRange(entity.Position, World.WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldDespawn(viewer.Client, viewer.MapId, entity.Handle);
			}
		}

		// An NPC respawned (its timer elapsed on the tick): re-render it for every player in range by re-sending its
		// stored FA015807 spawn body (re-framed + position-patched per recipient by SendWorldSpawn).
		private async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _simulation.World.ZoneFor(npc.MapId).PlayersInRange(npc.Position, World.WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, npc);
			}
		}

		// A player opened a world object (Interact UseObject). If it's a loot bag in their zone, OPEN ITS LOOT WINDOW
		// per the decoded flow (protocol_loot_interaction): assign the player as the current looter and send the
		// SimpleDynelVisualChange "Open" so the client shows the bag's contents. The bag STAYS in the world - it is
		// consumed on close (0x2044), not on open. Taking an item (0x2043 -> grant + LootRemoveItems) is the remaining,
		// in-client-gated piece. No-op for non-loot / unknown objects.
		private async Task OpenLootBag(INetworkClient client, uint objectHandle)
		{
			var player = PlayerWorldEntity(client);
			if (player == null || !_simulation.World.ZoneFor(player.MapId).TryGet(objectHandle, out var bag) || !bag.IsLoot)
				return;

			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(World.WorldLoot.BuildNewCurrentLooterBody(bag.Handle, player.Handle)))
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(World.WorldLoot.BuildOpenWindowBody(World.WorldLoot.LootTypeTag, bag.Handle)))
				.SendAsync(client);

			Logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
		}

		// A player clicked an item in the loot window (top-level 0x2043). The actual transfer - grant the item to the
		// player's inventory (ContainerAddItem) + LootRemoveItems to clear it from the bag - is the in-client-gated
		// piece not built yet (no live inventory-add path). Logged so the click is traceable.
		private Task TakeLootItem(INetworkClient client, uint bagHandle)
		{
			Logger.LogInformation("Loot: take-item on bag 0x{Handle:X8} (item grant not yet implemented).", bagHandle);
			return Task.CompletedTask;
		}

		// A player closed the loot window (top-level 0x2044): consume the bag - fade it out for nearby players and drop
		// it from the model.
		private async Task CloseLootBag(INetworkClient client, uint bagHandle)
		{
			var player = PlayerWorldEntity(client);
			if (player == null || !_simulation.World.ZoneFor(player.MapId).TryGet(bagHandle, out var bag) || !bag.IsLoot)
				return;

			await BroadcastEntityFade(bag);
			_simulation.World.Remove(bag);
			Logger.LogInformation("Loot: {Name} closed loot bag 0x{Handle:X8}; faded + removed.", player.Name, bag.Handle);
		}

		// Fade an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot bags
		// (consumed or expired). Loot bags carry the 0xC87A typetag.
		private async Task BroadcastEntityFade(WorldEntity entity)
		{
			var body = World.WorldLoot.BuildFadeBody(World.WorldLoot.LootTypeTag, entity.Handle, World.WorldLoot.DefaultFadeSeconds);
			foreach (var viewer in _simulation.World.ZoneFor(entity.MapId).PlayersInRange(entity.Position, World.WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;
				await new PacketStream()
					.WriteHeader(LoginWorldSender, LoginWorldReceiver(viewer.Client), 0x2000)
					.WriteArrayUInt32Length(s => s.WriteArray(body))
					.SendAsync(viewer.Client);
			}
		}

		// Spawns a mount entity (a captured FA015807 "Horse" NpcSpawn, e.g. "Fading Black Horse") at the player's
		// position and renders it for nearby players - the summon's visible payoff. Built byte-exact via NpcSpawnBuilder
		// (like NPC/loot spawns), re-framed + position-patched per recipient by SendWorldSpawn. The cast is broadcast
		// separately; the player actually RIDING (the MountedRun movement transition) is the client's part and
		// in-client-gated. The mount has no vitals (not killable) and is non-hostile.
		private async Task SpawnMountForPlayer(INetworkClient client, string mountName)
		{
			var player = PlayerWorldEntity(client);
			if (player == null)
				return;

			var mount = await Database.GetNpcSpawnByName(mountName);
			var handle = NextLootHandle(); // a fresh summoned-entity handle (high range, no player/NPC collision)
			if (mount == null || !Spawn.NpcSpawnBuilder.TryBuild(mount, handle, out var body))
			{
				Logger.LogInformation("Mount: no spawnable '{Mount}' template found.", mountName);
				return;
			}

			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, mountName);
			entity.SpawnBody = body;
			_simulation.World.ZoneFor(player.MapId).Add(entity);

			foreach (var viewer in _simulation.World.ZoneFor(player.MapId).PlayersInRange(player.Position, World.WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, entity);
			}

			Logger.LogInformation("Mount: spawned {Mount} 0x{Handle:X8} for {Player} at {Pos}.", mountName, handle, player.Name, player.Position);
		}

		// On a kill, maybe drop a loot bag at the dead NPC's spot (LootResolver coin-flip). The bag is a world object
		// (LootBagFullUpdateIIR_c 0xF096251E) built from a captured "Bag of loot" row for the map, placed at the death
		// position with a fresh handle, added to the zone, and spawned for nearby players. It expires off the tick
		// (WorldState.Tick -> LootExpired -> despawn + remove). Opening/looting the bag is a separate (gated) step.
		private async Task TrySpawnLoot(WorldEntity deadNpc)
		{
			if (!Combat.LootResolver.RollsDrop(System.Random.Shared.NextDouble()))
				return;

			var template = await GetLootTemplate(deadNpc.MapId);
			if (template == null)
				return;

			var handle = NextLootHandle();
			if (!Spawn.WorldObjectBuilder.TryBuild(template, handle, deadNpc.Position,
				    new System.Numerics.Quaternion(deadNpc.Rotation.X, deadNpc.Rotation.Y, deadNpc.Rotation.Z, 1f), out var body))
				return;

			var bag = WorldEntity.ForLoot(handle, deadNpc.MapId, deadNpc.Position, deadNpc.Rotation,
				template.Name ?? "Bag of loot", body, Combat.LootResolver.LootExpirySeconds);
			_simulation.World.ZoneFor(deadNpc.MapId).Add(bag);

			foreach (var viewer in _simulation.World.ZoneFor(deadNpc.MapId).PlayersInRange(bag.Position, World.WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, bag);
			}

			Logger.LogInformation("Loot: dropped {Name} 0x{Handle:X8} at {Pos} on map {Map} (expires in {Expiry}s).", bag.Name, handle, bag.Position, deadNpc.MapId, Combat.LootResolver.LootExpirySeconds);
		}

		// The captured loot-bag template (LootBagFullUpdateIIR_c) for a map, cached on first use. Null if the map has
		// no captured loot bag to template from.
		private async Task<Core.Database.Tables.WorldObject> GetLootTemplate(uint mapId)
		{
			if (_lootTemplateByMap.TryGetValue(mapId, out var cached))
				return cached;

			var objects = await Database.GetWorldObjects(mapId);
			Core.Database.Tables.WorldObject template = null;
			foreach (var o in objects)
			{
				if (o.SubOpcode == LootBagSubOpcode && o.Name != null && o.Name.Contains("loot", System.StringComparison.OrdinalIgnoreCase))
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
			return (uint)System.Threading.Interlocked.Increment(ref _nextLootHandle);
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		private Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(World.WorldQuest.BuildQuestIir(client.Session.Account.ClientInstance, questId, state)))
				.SendAsync(client);
		}

		// Sends a QuestGoal (objective progress) to the player.
		private Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count)
		{
			return new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s.WriteArray(World.WorldQuest.BuildQuestGoal(client.Session.Account.ClientInstance, questId, goalId, count)))
				.SendAsync(client);
		}

		// On world entry: re-send the player's persisted active quests so their journal is restored.
		private async Task RestorePlayerQuests(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null)
				return;

			foreach (var quest in await Database.GetCharacterQuests(character.Id))
			{
				if (quest.State != Core.Database.Tables.QuestState.Active)
					continue;

				await SendQuestIir(client, quest.QuestId, World.WorldQuest.StateActive);
				if (quest.GoalRequired > 0)
					await SendQuestGoal(client, quest.QuestId, 0, quest.GoalProgress);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		private Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_simulation.World.ZoneFor(leaver.MapId), leaver);
			return DispatchInterest(events, move: null);
		}

		// Records the playfield token the client will associate with this zone, read from the 0x737A6DF9 snapshot we
		// actually send (so the despawn token matches what the client learned, by construction; see WorldDespawn). This
		// OVERRIDES the token WorldState minted at zone creation - for a replayed playfield the burst's value is the
		// authoritative one (the client learned it from the burst, not from us). Idempotent: the token is constant per
		// playfield, so a re-entry re-extracts the same value; logged only when it actually changes.
		private void CapturePlayfieldToken(uint mapId, ReadOnlySpan<byte> snapshotBody)
		{
			if (!WorldDespawn.TryExtractPlayfieldToken(snapshotBody, out var token))
				return;

			var zone = _simulation.World.ZoneFor(mapId);
			if (zone.PlayfieldToken == token)
				return;

			zone.PlayfieldToken = token;
			Logger.LogInformation("World: map {Map} playfield token = 0x{Token:X8} (from burst; overrides minted fallback).", mapId, token);
		}
	}
}
