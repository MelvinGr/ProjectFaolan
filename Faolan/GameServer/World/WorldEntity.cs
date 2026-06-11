using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network;

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

		// Convenience factory for an NPC (no owning client) placed at a fixed transform.
		public static WorldEntity ForNpc(uint handle, uint mapId, Vector3 position, Vector3 rotation, string name)
		{
			return new WorldEntity(handle, mapId, false, name, null)
			{
				Position = position,
				Rotation = rotation
			};
		}

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

		// For a player: the FA015807 spawn BODY (the bytes after the RPC header) that renders this entity on another
		// client - captured when the player entered the world, re-framed per recipient by the broadcaster. Null until
		// captured / for entities with no spawn.
		public byte[] SpawnBody { get; set; }
	}
}
