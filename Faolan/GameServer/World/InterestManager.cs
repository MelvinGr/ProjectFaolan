using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Faolan.GameServer.World
{
	public enum InterestEventType
	{
		Spawn, // the recipient should now SEE the subject (send its FA015807 spawn)
		Move, // the recipient already sees the subject, which moved (forward the subject's Move)
		Despawn // the subject left the recipient's range (send a remove)
	}

	// One thing the broadcaster must send: `Recipient` (always a player) should Spawn/Move/Despawn `Subject`.
	public readonly record struct InterestEvent(WorldEntity Recipient, WorldEntity Subject, InterestEventType Type);

	// Computes the spawn/move/despawn packets needed to keep each player's view of the world correct as entities move
	// (interest management - docs/archive/server-architecture-roadmap.md). Pure over the world model + each player's Known set;
	// the caller turns the returned events into wire packets. Range is a flat radius for now; a grid/cell scheme is the
	// scaling step.
	public static class InterestManager
	{
		// Recompute interest after `mover` changed position (or just entered). Updates Known sets and returns the
		// events to dispatch. Symmetric: handles both what the mover now sees and who now sees the mover.
		// `includeNonPlayers` widens the mover's own view to NPCs (not just players): a player walking through the
		// zone streams the NPCs it comes into range of and is sent despawns for the ones it leaves. NPCs already in
		// the mover's Known set - the ones the world-burst replay rendered at entry, seeded by the caller - are
		// skipped so they are never double-spawned. Off at entry (the burst already populated the load), on for moves.
		public static List<InterestEvent> ResolveMove(Zone zone, WorldEntity mover, float range, bool includeNonPlayers = false)
		{
			var events = new List<InterestEvent>();

			// (1) The mover's own view (only a player receives packets): entities it came into range of are spawned,
			//     ones it left are despawned. (Others moving is handled by their own ResolveMove call.) With
			//     includeNonPlayers, "entities" is players + NPCs; otherwise just players.
			if (mover.IsPlayer)
			{
				var candidates = includeNonPlayers
					? zone.EntitiesInRange(mover.Position, range, mover.Handle)
					: zone.PlayersInRange(mover.Position, range, mover.Handle);
				var inRange = new HashSet<uint>();
				foreach (var other in candidates)
				{
					if (other.IsLoot)
						continue; // loot bags have their own open/fade lifecycle, not interest streaming
					inRange.Add(other.Handle);
					if (mover.Known.Add(other.Handle))
						events.Add(new InterestEvent(mover, other, InterestEventType.Spawn));
				}

				foreach (var knownHandle in mover.Known.ToArray())
				{
					if (inRange.Contains(knownHandle))
						continue;

					// A player-only pass (includeNonPlayers=false, e.g. world entry) computes inRange from PLAYERS
					// only, so every NPC the mover knows would look "out of range" here and be wrongly despawned -
					// wiping the entry seeding and re-streaming the entire zone on the first move (the loader flood).
					// Only prune NPCs on passes that actually manage NPCs; players are always managed.
					if (!includeNonPlayers && zone.TryGet(knownHandle, out var known) && !known.IsPlayer)
						continue;

					mover.Known.Remove(knownHandle);
					if (zone.TryGet(knownHandle, out var gone))
						events.Add(new InterestEvent(mover, gone, InterestEventType.Despawn));
				}
			}

			// (2) Every other player's view of the mover: spawn it if newly in range, forward its move if already
			//     known, despawn it if it left.
			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == mover.Handle)
					continue;

				if (Vector3.Distance(viewer.Position, mover.Position) <= range)
				{
					events.Add(new InterestEvent(viewer, mover, viewer.Known.Add(mover.Handle)
						? InterestEventType.Spawn
						: InterestEventType.Move));
				}
				else if (viewer.Known.Remove(mover.Handle))
					events.Add(new InterestEvent(viewer, mover, InterestEventType.Despawn));
			}

			return events;
		}

		// The mover left the world (disconnect / despawn): despawn it for everyone who could see it, and forget what
		// it knew.
		public static List<InterestEvent> ResolveRemove(Zone zone, WorldEntity leaver)
		{
			var events = new List<InterestEvent>();
			foreach (var viewer in zone.Entities)
			{
				if (viewer.IsPlayer && viewer.Handle != leaver.Handle && viewer.Known.Remove(leaver.Handle))
					events.Add(new InterestEvent(viewer, leaver, InterestEventType.Despawn));
			}

			leaver.Known.Clear();
			return events;
		}
	}
}
