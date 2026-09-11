using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Faolan.GameServer.World
{
	public enum InterestEventType
	{
		Spawn, // the recipient should now see the subject: send its FA015807 spawn
		Move, // the recipient already sees the subject, which moved: forward the subject's Move
		Despawn // the subject left the recipient's range: send a remove
	}

	/// <summary>
	///     One thing the broadcaster must send: <c>Recipient</c>, always a player, should Spawn, Move or Despawn
	///     <c>Subject</c>.
	/// </summary>
	public readonly record struct InterestEvent(WorldEntity Recipient, WorldEntity Subject, InterestEventType Type);

	/// <summary>
	///     Computes the spawn, move and despawn packets needed to keep each player's view of the world correct as
	///     entities move.
	/// </summary>
	/// <remarks>
	///     Pure over the world model and each player's Known set; the caller turns the returned events into wire
	///     packets. Range is a flat radius, with a grid or cell scheme as the scaling step.
	/// </remarks>
	public static class InterestManager
	{
		/// <summary>
		///     How far past the interest range an entity must go before it is despawned, having been spawned at the
		///     range itself. A plain radius has no memory, so an entity sitting on the line is streamed in and out
		///     every pass.
		/// </summary>
		/// <remarks>
		///     This value is ours, not retail's: the interest range it widens is itself unmeasured, and retail's own
		///     answer to this family of problem is a grid with hysteresis rather than a radius with a band. It must not
		///     be documented as a decoded constant.
		///     <para />
		///     Sized against the thing that churns: a route walker moves at about 2.2 u/s and the standing-player sweep
		///     runs at 1 Hz, so 15 units is a walker spending some seven seconds in the band before the despawn lands.
		///     Small enough that a genuine departure still despawns promptly, wide enough that a beat running along the
		///     edge crosses once instead of once per lap.
		///     <para />
		///     It is a despawn-side band only, and nothing is ever spawned beyond the range because of it: an entity in
		///     the band is kept if the viewer already holds it and ignored if it does not. Otherwise the effective range
		///     would simply be wider, with the same churn moved outward.
		/// </remarks>
		public const float DespawnHysteresis = 15f;

		/// <summary>
		///     Resolves which of the zone's static world objects the player should have rendered right now, updating
		///     the player's known set. Shared by the entry pass and the per-move pass so both obey one rule.
		/// </summary>
		/// <remarks>
		///     This mirrors retail, measured rather than assumed:
		///     <list type="bullet">
		///         <item>
		///             only a subset arrives at entry - in Hoxxling_..._gameplay2, 58 of 158 object handles, with 100
		///             more following as the player walked and the last one 687 s in;
		///         </item>
		///         <item>
		///             leaving range despawns the object - 212 of that capture's n3ToClientQuit records carry
		///             world-object handles, each with its own class word (0xC878 SimpleDynel, 0xC748 ZoneLabel/Door,
		///             and so on), not Character;
		///         </item>
		///         <item>
		///             returning re-sends the full spawn record verbatim - 565 of 648 repeated sends are byte-identical
		///             and the 83 that differ vary only in the optional kdtree field. Nothing stateful is pushed on a
		///             re-send, so there is no diffing to do: in range means the whole record, out of range means a
		///             despawn.
		///         </item>
		///     </list>
		/// </remarks>
		/// <returns>The objects to spawn, newly in range, and the handles to despawn, walked out of range.</returns>
		public static (List<LiveWorldObject> Spawn, List<uint> Despawn) ResolveWorldObjects(Zone zone, WorldEntity player, float range, IReadOnlySet<uint> quests)
		{
			var spawn = new List<LiveWorldObject>();
			var inRange = new HashSet<uint>();

			foreach (var obj in zone.WorldObjectsInRange(player.Position, range))
			{
				// A quest-gated object is absent entirely for a player who does not hold the quest. The quest set is a
				// parameter rather than a read off the entity, because at entry the entity's copy is not populated
				// until BuildEntryNpcBurst runs, which is after this: reading it there would hide every gated object
				// on login.
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

		/// <summary>
		///     Recompute interest after <paramref name="mover" /> changed position, or just entered. Updates the Known
		///     sets and returns the events to dispatch.
		/// </summary>
		/// <remarks>
		///     Symmetric: it handles both what the mover now sees and who now sees the mover.
		/// </remarks>
		/// <param name="includeNonPlayers">
		///     Widens the mover's own view from players to all entities, so a player walking through the zone streams
		///     the NPCs it comes into range of and is sent despawns for the ones it leaves. NPCs already in the mover's
		///     Known set - the ones the world-burst replay rendered at entry, seeded by the caller - are skipped, so
		///     they are never double-spawned. Off at entry, because the burst already populated the load; on for moves.
		/// </param>
		/// <param name="isEntry">
		///     Marks the mover joining or rejoining the world rather than moving within it. The caller dispatches entry
		///     with no Move payload, so part (2) must spawn the mover to every viewer and never forward a Move:
		///     otherwise a viewer still carrying the mover's handle in a stale Known set gets a Move that entry drops,
		///     and the re-entrant stays invisible until the viewer next moves. At entry the mover is forgotten on each
		///     viewer first, forcing a fresh spawn and a client re-poll.
		/// </param>
		public static List<InterestEvent> ResolveMove(Zone zone, WorldEntity mover, float range, bool includeNonPlayers = false, bool isEntry = false)
		{
			var events = new List<InterestEvent>();

			// The despawn band (see DespawnHysteresis). Both halves below query at the wider radius so an entity in the
			// band is visible to the pass at all; it is then kept if already known and ignored if not, which is what
			// makes the band a despawn-side rule rather than a bigger range.
			var keepRange = range + DespawnHysteresis;

			// (1) The mover's own view - only a player receives packets: entities it came into range of are spawned,
			//     ones it left are despawned. Other entities moving is handled by their own ResolveMove call.
			if (mover.IsPlayer)
			{
				var candidates = includeNonPlayers
					? zone.EntitiesInRange(mover.Position, keepRange, mover.Handle)
					: zone.PlayersInRange(mover.Position, keepRange, mover.Handle);
				var inRange = new HashSet<uint>();
				foreach (var other in candidates)
				{
					// In the hysteresis band: hold on to the entity if the mover already has it, and do not
					// introduce it otherwise. inRange drives the prune below, so adding the handle without adding it
					// to Known keeps what is rendered while spawning nothing new.
					var beyondRange = Vector3.Distance(mover.Position, other.Position) > range;
					if (beyondRange && !mover.Knowledge.Known.Contains(other.Handle))
						continue;

					if (other.IsLoot)
						continue; // loot bags have their own open/fade lifecycle, not interest streaming

					if (other.IsDead)
					{
						// A dead NPC the mover already knows is a corpse still in its death-animation linger: keep it
						// in range so the prune below does not despawn it before it has hit the floor, which happens
						// whenever the player moves during the roughly 3 s linger. A dead NPC the mover does not know
						// must not be added: interest would spawn it alive at its death spot, and that re-add to Known
						// would gate out the real respawn's relocate-to-spawn. The corpse's quit removes the handle
						// from Known, so an NPC awaiting respawn is no longer in Known and is correctly skipped here.
						if (mover.Knowledge.Known.Contains(other.Handle))
							inRange.Add(other.Handle);
						continue;
					}

					if (!other.IsVisibleTo(mover))
						continue; // a quest-gated NPC the mover does not yet qualify for counts as out of range

					inRange.Add(other.Handle);
					if (mover.Knowledge.Known.Add(other.Handle))
						events.Add(new InterestEvent(mover, other, InterestEventType.Spawn));
				}

				foreach (var knownHandle in mover.Knowledge.Known.ToArray())
				{
					if (inRange.Contains(knownHandle))
						continue;

					// A player-only pass - includeNonPlayers false, as at world entry - computes inRange from players
					// alone, so every NPC the mover knows would look out of range here and be wrongly despawned,
					// wiping the entry seeding and re-streaming the whole zone on the first move. Only prune NPCs on
					// passes that manage NPCs; players are always managed.
					if (!includeNonPlayers && zone.TryGet(knownHandle, out var known) && !known.IsPlayer)
						continue;

					mover.Knowledge.Known.Remove(knownHandle);
					// Only despawn what the client confirmed constructing, by polling for it. A still-loading client
					// churns through position updates while settling, and an entity spawned at the entry point that
					// briefly leaves range here would otherwise get an n3ToClientQuit for a handle it has not built,
					// crashing the loader on a null dynel. Unconfirmed handles are dropped from Known without a
					// despawn; if the client later renders and re-ranges them, the normal flow re-spawns them.
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
				// that already holds it - merely forwarded a Move, since Known.Add then returns false - and is not
				// introduced to one that does not. Known must be read before the isEntry block below, which clears the
				// handle to force a fresh spawn and would otherwise make every entering mover look unknown here and
				// lose its band.
				var distance = Vector3.Distance(viewer.Position, mover.Position);
				var withinBand = distance <= range || (distance <= keepRange && viewer.Knowledge.Known.Contains(mover.Handle));

				if (mover.IsVisibleTo(viewer) && withinBand)
				{
					// On entry, forget any stale build of the mover on this viewer so the mover is spawned rather
					// than forwarded as a Move the null-move entry dispatch would drop; the viewer re-polls the
					// fresh spawn.
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
					// The same gate for a viewer losing sight of the mover: never despawn a handle the viewer's client
					// has not confirmed building, because a still-loading viewer crashes on the unknown handle.
					events.Add(new InterestEvent(viewer, mover, InterestEventType.Despawn));
			}

			return events;
		}

		/// <summary>
		///     The mover left the world, by disconnect or despawn: despawn it for everyone who could see it, and forget
		///     what it knew.
		/// </summary>
		public static List<InterestEvent> ResolveRemove(Zone zone, WorldEntity leaver)
		{
			var events = new List<InterestEvent>();
			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == leaver.Handle || !viewer.Knowledge.Known.Remove(leaver.Handle))
					continue;

				// The same gate ResolveMove applies: never send an n3ToClientQuit for a handle the viewer's client has
				// not confirmed building. A viewer still loading when the leaver disconnects has the leaver in Known
				// but not Confirmed, and despawning that unbuilt handle crashes it on a null dynel.
				//
				// For a player leaver the poll gate cannot be the test: the client renders another player's spawn
				// without ever polling for it - measured live, zero polls for the other member's handle across a whole
				// PvP match while the model was visibly rendered - so Confirmed never holds a player handle, and the
				// gate alone leaves a phantom standing where its owner left. The condition that is actually needed is
				// "the viewer is not mid-load", which is the session's own WorldEntered flag, so a player despawn goes
				// to every loaded viewer that knew the handle while the still-loading case stays protected.
				var confirmed = viewer.Knowledge.Confirmed.Remove(leaver.Handle);
				if (confirmed || (leaver.IsPlayer && viewer.Client?.Session.WorldEntered == true))
					events.Add(new InterestEvent(viewer, leaver, InterestEventType.Despawn));
			}

			leaver.Knowledge.Known.Clear();
			return events;
		}
	}
}
