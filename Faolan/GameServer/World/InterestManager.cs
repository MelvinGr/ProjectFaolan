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
	// (interest management - Gitea #404). Pure over the world model + each player's Known set;
	// the caller turns the returned events into wire packets. Range is a flat radius for now; a grid/cell scheme is the
	// scaling step.
	public static class InterestManager
	{
		/// <summary>
		///     How far past the interest range an entity must go before it is DESPAWNED, having been spawned at the
		///     range itself. A plain radius has no memory, so an entity sitting on the line is streamed in and out
		///     every pass.
		/// </summary>
		/// <remarks>
		///     ⚠️ **This is OURS, not retail's.** The interest range it widens is itself unmeasured (Gitea #856), and
		///     retail's own answer to this family of problem is a GRID with hysteresis rather than a radius with a
		///     band (#529, for world objects). So this is not a decoded constant and must not be documented as one -
		///     it is the minimum honest fix for a churn that a flat radius creates by construction.
		///     <para />
		///     Sized against the thing that churns: a route walker moves at about 2.2 u/s and the standing-player
		///     sweep runs at 1 Hz (#778), so 15 u is a walker spending some seven seconds in the band before the
		///     despawn lands. Small enough that a genuine departure still despawns promptly, wide enough that a beat
		///     running along the edge crosses once instead of once per lap.
		///     <para />
		///     It is a DESPAWN-side band only. Nothing is ever spawned beyond the range because of it: an entity in
		///     the band is kept if the viewer already holds it and ignored if it does not. Otherwise the effective
		///     range would simply be 165 u with the same churn moved outward.
		/// </remarks>
		public const float DespawnHysteresis = 15f;

		// Resolves which of the zone's static world objects the player should have rendered right now, updating the
		// player's non-sticky known set: returns the ones to SPAWN (newly in range) and the handles to DESPAWN (walked
		// out of range). Shared by the entry pass and the per-move pass so both obey one rule.
		//
		// This is retail's model, measured rather than assumed (#424):
		//   * only a SUBSET arrives at entry - in Hoxxling_..._gameplay2, 58 of 158 object handles, with 100 more
		//     following as the player walked and the last one 687s in;
		//   * leaving range DESPAWNS the object - 212 of that capture's n3ToClientQuit records carry world-object
		//     handles, each with its OWN class word (0xC878 SimpleDynel, 0xC748 ZoneLabel/Door, ...), not Character;
		//   * returning re-sends the FULL spawn record verbatim - 565 of 648 repeated sends are byte-identical and the
		//     83 that differ vary only in the optional kdtree field. Nothing stateful is ever pushed on a re-send, so
		//     there is no diffing to do: in range -> whole record, out of range -> despawn.
		public static (List<LiveWorldObject> Spawn, List<uint> Despawn) ResolveWorldObjects(Zone zone, WorldEntity player, float range, IReadOnlySet<uint> quests)
		{
			var spawn = new List<LiveWorldObject>();
			var inRange = new HashSet<uint>();

			foreach (var obj in zone.WorldObjectsInRange(player.Position, range))
			{
				// #262 quest-gated objects: absent entirely for a player who does not hold the quest. The set is passed in
				// rather than read off the entity, because at ENTRY the entity's copy is not populated until
				// BuildEntryNpcBurst runs - which is after this - and reading it there would silently hide every gated
				// object on login.
				if (!obj.SpawnsFor(quests))
					continue;

				inRange.Add(obj.Handle);
				if (player.Knowledge.KnownObjects.Add(obj.Handle))
					spawn.Add(obj);
			}

			var despawn = player.Knowledge.KnownObjects.Where(h => !inRange.Contains(h)).ToList();
			foreach (var handle in despawn)
				player.Knowledge.KnownObjects.Remove(handle);

			return (spawn, despawn);
		}

		// Recompute interest after `mover` changed position (or just entered). Updates Known sets and returns the
		// events to dispatch. Symmetric: handles both what the mover now sees and who now sees the mover.
		// `includeNonPlayers` widens the mover's own view to NPCs (not just players): a player walking through the
		// zone streams the NPCs it comes into range of and is sent despawns for the ones it leaves. NPCs already in
		// the mover's Known set - the ones the world-burst replay rendered at entry, seeded by the caller - are
		// skipped so they are never double-spawned. Off at entry (the burst already populated the load), on for moves.
		// `isEntry` marks the mover (re)joining the world rather than moving within it: the caller dispatches entry with
		// no Move payload (move=null), so part (2) must SPAWN the mover to every viewer, never forward a Move. Without
		// this, a viewer still carrying the mover's handle in a stale Known set (a prior map-change / re-enter that
		// didn't clean-remove it) gets a Move that entry drops - the re-entrant stays invisible until the viewer next
		// moves (#323). At entry we forget the mover on each viewer first, forcing a fresh Spawn + client re-poll.
		public static List<InterestEvent> ResolveMove(Zone zone, WorldEntity mover, float range, bool includeNonPlayers = false, bool isEntry = false)
		{
			var events = new List<InterestEvent>();

			// The despawn band (see DespawnHysteresis). Both halves below query at the WIDER radius so an entity in
			// the band is visible to the pass at all - it is then KEPT if already known and ignored if not, which is
			// what makes the band a despawn-side rule rather than a bigger range.
			var keepRange = range + DespawnHysteresis;

			// (1) The mover's own view (only a player receives packets): entities it came into range of are spawned,
			//     ones it left are despawned. (Others moving is handled by their own ResolveMove call.) With
			//     includeNonPlayers, "entities" is players + NPCs; otherwise just players.
			if (mover.IsPlayer)
			{
				var candidates = includeNonPlayers
					? zone.EntitiesInRange(mover.Position, keepRange, mover.Handle)
					: zone.PlayersInRange(mover.Position, keepRange, mover.Handle);
				var inRange = new HashSet<uint>();
				foreach (var other in candidates)
				{
					// In the hysteresis band: hold on to it if the mover already has it, and do not introduce it
					// otherwise. `inRange` drives the prune below, so adding the handle without adding it to Known is
					// exactly "keep what is rendered, spawn nothing new".
					var beyondRange = Vector3.Distance(mover.Position, other.Position) > range;
					if (beyondRange && !mover.Knowledge.Known.Contains(other.Handle))
						continue;

					if (other.IsLoot)
						continue; // loot bags have their own open/fade lifecycle, not interest streaming

					if (other.IsDead)
					{
						// A dead NPC the mover ALREADY knows is a corpse still in its death-animation linger (pre-conversion):
						// keep it in range so the move-prune below does NOT despawn it early - before it has hit the floor -
						// which happens whenever the player moves during the ~3s linger. Do NOT add a dead NPC the mover does
						// NOT know: interest would spawn it ALIVE at its death spot and that re-add to Known would gate out the
						// real respawn's relocate-to-spawn (#246). The corpse's quit removes the handle from Known, so an
						// awaiting-respawn NPC (past its corpse sequence) is no longer in Known and is correctly skipped here.
						if (mover.Knowledge.Known.Contains(other.Handle))
							inRange.Add(other.Handle);
						continue;
					}

					if (!other.IsVisibleTo(mover))
						continue; // #261: a quest-gated NPC the mover doesn't yet qualify for - treat as not in range

					inRange.Add(other.Handle);
					if (mover.Knowledge.Known.Add(other.Handle))
						events.Add(new InterestEvent(mover, other, InterestEventType.Spawn));
				}

				foreach (var knownHandle in mover.Knowledge.Known.ToArray())
				{
					if (inRange.Contains(knownHandle))
						continue;

					// A player-only pass (includeNonPlayers=false, e.g. world entry) computes inRange from PLAYERS
					// only, so every NPC the mover knows would look "out of range" here and be wrongly despawned -
					// wiping the entry seeding and re-streaming the entire zone on the first move (the loader flood).
					// Only prune NPCs on passes that actually manage NPCs; players are always managed.
					if (!includeNonPlayers && zone.TryGet(knownHandle, out var known) && !known.IsPlayer)
						continue;

					mover.Knowledge.Known.Remove(knownHandle);
					// #245: only despawn what the client actually CONFIRMED constructing (polled via 0x2020). A
					// still-loading client churns through CharDCMove position updates while settling; an entity spawned
					// at the entry point that briefly leaves range here would otherwise get an n3ToClientQuit for a handle
					// it hasn't built yet -> pcDynel-NULL loader crash. Unconfirmed handles are dropped from Known
					// silently (no despawn); if the client later renders + re-ranges them, normal flow re-spawns them.
					if (mover.Knowledge.Confirmed.Remove(knownHandle) && zone.TryGet(knownHandle, out var gone))
						events.Add(new InterestEvent(mover, gone, InterestEventType.Despawn));
				}
			}

			// (2) Every other player's view of the mover: spawn it if newly in range, forward its move if already
			//     known, despawn it if it left.
			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == mover.Handle)
					continue;

				// The same band, from the other side: a mover that has drifted past the range is kept for a viewer
				// that already holds it (and merely forwarded a Move, since Known.Add then returns false), and is not
				// introduced to one that does not. Reading `Known` BEFORE the isEntry block below matters - that block
				// clears the handle to force a fresh spawn, which would otherwise make every entering mover look
				// unknown here and lose its band.
				var distance = Vector3.Distance(viewer.Position, mover.Position);
				var withinBand = distance <= range || (distance <= keepRange && viewer.Knowledge.Known.Contains(mover.Handle));

				if (mover.IsVisibleTo(viewer) && withinBand)
				{
					// #323: on entry, forget any stale build of the mover on this viewer so the mover is SPAWNED (not
					// forwarded as a Move the null-move entry dispatch would drop); the viewer re-polls the fresh spawn.
					if (isEntry)
					{
						viewer.Knowledge.Known.Remove(mover.Handle);
						viewer.Knowledge.Confirmed.Remove(mover.Handle);
					}

					events.Add(new InterestEvent(viewer, mover, viewer.Knowledge.Known.Add(mover.Handle)
						? InterestEventType.Spawn
						: InterestEventType.Move));
				}
				else if (viewer.Knowledge.Known.Remove(mover.Handle) && viewer.Knowledge.Confirmed.Remove(mover.Handle))
					// #245: same gate for a viewer losing sight of the mover - never despawn a handle the viewer's client
					// hasn't confirmed building (a still-loading viewer would crash on the unknown handle).
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
				if (!viewer.IsPlayer || viewer.Handle == leaver.Handle || !viewer.Knowledge.Known.Remove(leaver.Handle))
					continue;

				// #322: the SAME #245 gate ResolveMove applies (:95) - never send an n3ToClientQuit for a handle the
				// viewer's client hasn't CONFIRMED building (polled via 0x2020). A viewer still loading when the leaver
				// disconnects has the leaver in Known but not Confirmed; despawning that unbuilt handle crashes it
				// (pcDynel == null). Repro: A in zone, B connects + is sent A's spawn (Known) but hasn't polled yet, A
				// disconnects -> without this gate B gets a despawn for A's unbuilt handle.
				//
				// ⚠️ For a PLAYER leaver the poll gate cannot be the test: the client renders another player's spawn
				// WITHOUT ever polling 0x2020 (measured live 2026-08-26 - zero polls for the other member's handle
				// across a whole PvP match, model visibly rendered), so Confirmed never holds a player handle and the
				// gate left a PHANTOM standing in the minigame after its owner left. What #322 actually needs is
				// "the viewer is not mid-load", which is the session's own WorldEntered flag - so a player despawn
				// goes to every loaded viewer that knew the handle, and the still-loading case stays protected.
				var confirmed = viewer.Knowledge.Confirmed.Remove(leaver.Handle);
				if (confirmed || (leaver.IsPlayer && viewer.Client?.Session.WorldEntered == true))
					events.Add(new InterestEvent(viewer, leaver, InterestEventType.Despawn));
			}

			leaver.Knowledge.Known.Clear();
			return events;
		}
	}
}
