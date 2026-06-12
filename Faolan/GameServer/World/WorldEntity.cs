using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// One live entity in the world simulation - a player or an NPC - identified by its session-scoped wire handle.
	// This is the authoritative, in-memory state the server owns (position/rotation, map), as opposed to the
	// per-session captured state the replay path used. The roadmap (docs/archive/server-architecture-roadmap.md) promotes the
	// scattered per-session maps into this shared model. Vitals/AI/buffs will hang off this as those systems land.
	public sealed class WorldEntity
	{
		// Default time a killed NPC stays dead before the tick respawns it.
		public const double DefaultRespawnDelaySeconds = 15.0;

		// Time (seconds) until this NPC may swing again; counts down while it has a target in range.
		private double _attackCooldownRemaining;

		// Remaining time (seconds) until a loot bag expires.
		private double _expiryRemaining;

		// Remaining time (seconds) until respawn while dead; meaningless when alive.
		private double _respawnRemaining;

		// Whether this NPC attacks players on sight. Mobs are hostile; talk-to NPCs (quest-givers/vendors, i.e. those
		// with a conversation) are not. Set at zone load. Players are never hostile here.
		public bool Hostile { get; set; }

		// Dead = has vitals and they hit zero. A dead entity is excluded from combat and counts down to respawn.
		public bool IsDead => Vitals?.IsDead == true;

		// A transient loot bag (expires + despawns) rather than a player/NPC.
		public bool IsLoot { get; private init; }

		// Coins carried by a loot bag (copper), rolled when the bag drops; zeroed once looted (TryLootCash is
		// granted exactly once per bag).
		public uint CashCopper { get; set; }

		public bool IsPlayer { get; }

		// The FA015807 spawn BODY (the bytes after the RPC header) that renders this entity on a client - for a player,
		// captured when they entered the world; for an NPC, built from its NpcSpawn row at load. Re-framed per recipient
		// by the broadcaster (cross-player spawn, NPC respawn). Null until captured / for entities with no spawn.
		public byte[] SpawnBody { get; set; }

		// How long this entity stays dead before the tick respawns it.
		public double RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

		// Live vitals (NPCs are seeded at load; null for entities the combat/world layer doesn't track here yet).
		// The world is the authoritative owner - combat applies damage to this, and the tick respawns off it.
		public EntityVitals Vitals { get; set; }

		// Active buffs/effects on this entity, keyed by buff (spell) id - reapplying refreshes the entry.
		// Server-side bookkeeping only for now: the client tracks icon + duration itself from the ApplySpell
		// data, and retail sends NO removal IIR on CancelBuff (just a FormatFeedback text), so removal here is
		// what makes the cancel real. Durations come from the RDB spell defs once imported (TODO 5); entries
		// with one expire on the tick, the rest live until cancel/disconnect. Stat effects land with the same
		// spell-def work.
		public ConcurrentDictionary<uint, ActiveBuff> ActiveBuffs { get; } = new ConcurrentDictionary<uint, ActiveBuff>();

		// For a player: the handles this player currently sees (has been sent a spawn for). Interest management diffs
		// against this set to decide what to spawn/despawn as entities move in and out of range. Unused for NPCs.
		public HashSet<uint> Known { get; } = new HashSet<uint>();

		// The owning connection for a player (so the broadcaster can send packets to it); null for NPCs.
		public INetworkClient Client { get; }
		public string Name { get; }

		// The wire handle other entities address this one by (the player's client instance / an NPC's spawn handle).
		public uint Handle { get; }

		// The entity's level (NPCs: from their NpcSpawn row; drives the kill-XP award). 0 if unknown.
		public uint Level { get; set; }

		// Live transform + map. Mutable: updated by Move (players) and the AI tick (NPCs, later).
		public uint MapId { get; set; }
		public Vector3 Position { get; set; }
		public Vector3 Rotation { get; set; }

		// Where the entity respawns (its original spawn point) - set for NPCs by ForNpc.
		public Vector3 SpawnPosition { get; set; }
		public Vector3 SpawnRotation { get; set; }

		public WorldEntity(uint handle, uint mapId, bool isPlayer, string name, INetworkClient client)
		{
			Handle = handle;
			MapId = mapId;
			IsPlayer = isPlayer;
			Name = name;
			Client = client;
		}

		// Resets the attack cooldown (e.g. when no target is in range), so re-aggro doesn't swing instantly.
		public void ArmAttackCooldown(double interval)
		{
			_attackCooldownRemaining = interval;
		}

		// Marks the entity dead (call once its vitals reach 0) and starts the respawn countdown. Idempotent per death.
		public void MarkDead()
		{
			_respawnRemaining = RespawnDelaySeconds;
		}

		// Advances the attack cooldown by dt; returns true (and re-arms for `interval`) when a swing is ready. Used by
		// the AI tick so an aggroed NPC attacks at a fixed cadence regardless of the base tick rate.
		public bool TickAttackReady(double dt, double interval)
		{
			_attackCooldownRemaining -= dt;
			if (_attackCooldownRemaining > 0)
				return false;

			_attackCooldownRemaining = interval;
			return true;
		}

		// Counts down a loot bag's lifetime by dt; returns true the tick it expires (caller despawns + removes it).
		// No-op (false) for anything that isn't a loot bag.
		public bool TickExpiry(double dt)
		{
			if (!IsLoot)
				return false;

			_expiryRemaining -= dt;
			return _expiryRemaining <= 0;
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

		// A transient loot bag dropped on a kill (a world object, not a player/NPC): no vitals, no respawn; it sits at
		// its drop spot until its expiry elapses, then the tick despawns + removes it. `spawnBody` is its prebuilt
		// FA-family world-object body (re-framed per recipient like any spawn).
		public static WorldEntity ForLoot(uint handle, uint mapId, Vector3 position, Vector3 rotation, string name, byte[] spawnBody, double expirySeconds)
		{
			return new WorldEntity(handle, mapId, false, name, null)
			{
				Position = position,
				Rotation = rotation,
				SpawnBody = spawnBody,
				IsLoot = true,
				_expiryRemaining = expirySeconds
			};
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
	}
}
