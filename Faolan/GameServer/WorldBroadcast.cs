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
			var token = _world.ZoneFor(recipientMapId).PlayfieldToken;
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
			var zone = _world.ZoneFor(mapId);
			foreach (var npc in npcs)
			{
				// Seed killable vitals (level-scaled max) so combat can damage/kill it and the tick can respawn it.
				var vitals = Combat.EntityVitals.FromMax(Combat.CombatResolver.NpcMaxHealthForLevel(npc.Level), 0, Combat.CombatResolver.NpcMaxHealthForLevel(npc.Level));
				var entity = WorldEntity.ForNpc(npc.CapturedHandle, mapId,
					new Vector3(npc.PositionX, npc.PositionY, npc.PositionZ),
					new Vector3(npc.RotationX, npc.RotationY, npc.RotationZ),
					npc.Name ?? "NPC", vitals);
				entity.Level = npc.Level;

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

			var events = InterestManager.ResolveMove(_world.ZoneFor(entity.MapId), entity, PlayerInterestRange);
			await DispatchInterest(events, new MoveForward(moveSubop, dataObject, dataClientInstance, movingType, position, rotation, tail));
		}

		// After world entry (spawn body now captured): spawn the player to nearby players and them to the player.
		private async Task BroadcastPlayerEntry(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity?.SpawnBody == null)
				return;

			var events = InterestManager.ResolveMove(_world.ZoneFor(entity.MapId), entity, PlayerInterestRange);
			await DispatchInterest(events, move: null);
		}

		// An NPC died: despawn it for every player currently in range (NPCs are seen by everyone in the zone via the
		// burst, not tracked in per-player Known sets, so this is range-based rather than ResolveRemove). Uses the same
		// n3ToClientQuit packet as player despawns.
		private async Task BroadcastNpcDespawn(WorldEntity npc)
		{
			foreach (var viewer in _world.ZoneFor(npc.MapId).PlayersInRange(npc.Position, PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldDespawn(viewer.Client, viewer.MapId, npc.Handle);
			}
		}

		// An NPC respawned (its timer elapsed on the tick): re-render it for every player in range by re-sending its
		// stored FA015807 spawn body (re-framed + position-patched per recipient by SendWorldSpawn).
		private async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _world.ZoneFor(npc.MapId).PlayersInRange(npc.Position, PlayerInterestRange))
			{
				if (viewer.Client != null)
					await SendWorldSpawn(viewer.Client, npc);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		private Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_world.ZoneFor(leaver.MapId), leaver);
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

			var zone = _world.ZoneFor(mapId);
			if (zone.PlayfieldToken == token)
				return;

			zone.PlayfieldToken = token;
			Logger.LogInformation("World: map {Map} playfield token = 0x{Token:X8} (from burst; overrides minted fallback).", mapId, token);
		}
	}
}
