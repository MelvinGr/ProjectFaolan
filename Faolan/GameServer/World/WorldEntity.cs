using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// One live entity in the world simulation - a player or an NPC - identified by its session-scoped wire handle.
	// This is the authoritative, in-memory state the server owns (position/rotation, map), as opposed to the
	// per-session captured state the replay path used. The roadmap (docs/server-architecture-roadmap.md) promotes the
	// scattered per-session maps into this shared model. Vitals/AI/buffs will hang off this as those systems land.
	public sealed class WorldEntity
	{
		public WorldEntity(uint handle, uint mapId, bool isPlayer, string name, INetworkClient client)
		{
			Handle = handle;
			MapId = mapId;
			IsPlayer = isPlayer;
			Name = name;
			Client = client;
		}

		// Convenience factory for an NPC (no owning client) placed at a fixed transform. The transform is also recorded
		// as the spawn point the NPC returns to on respawn. Optional vitals make it killable + respawnable by the tick.
		public static WorldEntity ForNpc(uint handle, uint mapId, Vector3 position, Vector3 rotation, string name, EntityVitals vitals = null)
		{
			return new WorldEntity(handle, mapId, false, name, null)
			{
				Position = position,
				Rotation = rotation,
				SpawnPosition = position,
				SpawnRotation = rotation,
				Vitals = vitals
			};
		}

		// Default time a killed NPC stays dead before the tick respawns it.
		public const double DefaultRespawnDelaySeconds = 15.0;

		// The wire handle other entities address this one by (the player's client instance / an NPC's spawn handle).
		public uint Handle { get; }

		// The owning connection for a player (so the broadcaster can send packets to it); null for NPCs.
		public INetworkClient Client { get; }

		public bool IsPlayer { get; }
		public string Name { get; }

		// Live transform + map. Mutable: updated by Move (players) and the AI tick (NPCs, later).
		public uint MapId { get; set; }
		public Vector3 Position { get; set; }
		public Vector3 Rotation { get; set; }

		// For a player: the handles this player currently sees (has been sent a spawn for). Interest management diffs
		// against this set to decide what to spawn/despawn as entities move in and out of range. Unused for NPCs.
		public HashSet<uint> Known { get; } = new HashSet<uint>();

		// The FA015807 spawn BODY (the bytes after the RPC header) that renders this entity on a client - for a player,
		// captured when they entered the world; for an NPC, built from its NpcSpawn row at load. Re-framed per recipient
		// by the broadcaster (cross-player spawn, NPC respawn). Null until captured / for entities with no spawn.
		public byte[] SpawnBody { get; set; }

		// Live vitals (NPCs are seeded at load; null for entities the combat/world layer doesn't track here yet).
		// The world is the authoritative owner - combat applies damage to this, and the tick respawns off it.
		public EntityVitals Vitals { get; set; }

		// Where the entity respawns (its original spawn point) - set for NPCs by ForNpc.
		public Vector3 SpawnPosition { get; set; }
		public Vector3 SpawnRotation { get; set; }

		// How long this entity stays dead before the tick respawns it.
		public double RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

		// Dead = has vitals and they hit zero. A dead entity is excluded from combat and counts down to respawn.
		public bool IsDead => Vitals?.IsDead == true;

		// Remaining time (seconds) until respawn while dead; meaningless when alive.
		private double _respawnRemaining;

		// Marks the entity dead (call once its vitals reach 0) and starts the respawn countdown. Idempotent per death.
		public void MarkDead()
		{
			_respawnRemaining = RespawnDelaySeconds;
		}

		// Advances the respawn countdown for a DEAD entity by dt; when it elapses, revives the entity (vitals back to
		// full, transform back to its spawn point) and returns true so the caller can re-spawn it for nearby players.
		// No-op (false) for a living entity or one with no vitals.
		public bool TickRespawn(double dt)
		{
			if (!IsDead)
				return false;

			_respawnRemaining -= dt;
			if (_respawnRemaining > 0)
				return false;

			Vitals.Health = Vitals.MaxHealth;
			Position = SpawnPosition;
			Rotation = SpawnRotation;
			return true;
		}
	}
}
