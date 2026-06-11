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
	// (interest management - docs/server-architecture-roadmap.md). Pure over the world model + each player's Known set;
	// the caller turns the returned events into wire packets. Range is a flat radius for now; a grid/cell scheme is the
	// scaling step.
	public static class InterestManager
	{
		// Recompute interest after `mover` changed position (or just entered). Updates Known sets and returns the
		// events to dispatch. Symmetric: handles both what the mover now sees and who now sees the mover.
		public static List<InterestEvent> ResolveMove(Zone zone, WorldEntity mover, float range)
		{
			var events = new List<InterestEvent>();

			// (1) The mover's own view (only a player receives packets): players it came into range of are spawned,
			//     ones it left are despawned. (Others moving is handled by their own ResolveMove call.)
			//     TRANSITIONAL: only PLAYERS here - NPCs are still rendered by the per-player world-burst replay, so
			//     spawning them via interest too would double them. When NPCs become model-driven (burst retired),
			//     widen this to EntitiesInRange so a player walking into a region streams its NPCs.
			if (mover.IsPlayer)
			{
				var inRange = new HashSet<uint>();
				foreach (var other in zone.PlayersInRange(mover.Position, range, mover.Handle))
				{
					inRange.Add(other.Handle);
					if (mover.Known.Add(other.Handle))
						events.Add(new InterestEvent(mover, other, InterestEventType.Spawn));
				}

				foreach (var knownHandle in mover.Known.ToArray())
				{
					if (inRange.Contains(knownHandle))
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
					events.Add(new InterestEvent(viewer, mover, viewer.Known.Add(mover.Handle)
						? InterestEventType.Spawn
						: InterestEventType.Move));
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
