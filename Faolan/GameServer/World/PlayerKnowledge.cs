using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	// What a PLAYER's client has been told and what the player is on: the entity handles it has spawned (`Known`,
	// `Confirmed`) and the world objects it has been streamed, plus the quest id sets and the pending reach goals the
	// tick resolves against its position. A component of WorldEntity (Gitea #928 item 10); an NPC holds an empty one.
	public sealed class PlayerKnowledge
	{
		// For a PLAYER: the quest ids the player currently has ACTIVE. Drives quest-gated NPC visibility (#261).
		// Refreshed at world entry and whenever the player's quest state changes (accept / advance / complete).
		public HashSet<uint> ActiveQuestIds { get; } = [];

		// For a PLAYER: the quest ids the player has COMPLETED. Drives "hide once done" NPC gating (#262) - e.g. Casilda
		// is gone from the beach once her escort quest is completed. Loaded alongside ActiveQuestIds.
		public HashSet<uint> CompletedQuestIds { get; } = [];

		// For a player: the handles the client has actually CONFIRMED constructing - i.e. polled via 0x2020 (the poll's
		// requestId is the entity handle; the server answers with a 0x2008 SpawnCheck that renders it). Known means "spawn
		// sent"; Confirmed means "client built it". A despawn (n3ToClientQuit) for a Known-but-not-Confirmed handle hits a
		// pcDynel-NULL loader crash on a still-loading client (#245), so interest only despawns Confirmed handles.
		public HashSet<uint> Confirmed { get; } = [];

		// For a player: the handles this player currently sees (has been sent a spawn for). Interest management diffs
		// against this set to decide what to spawn/despawn as entities move in and out of range. Unused for NPCs.
		public HashSet<uint> Known { get; } = [];

		// The STATIC world objects (props/doors/vines/destructibles) this player currently has rendered - the interest
		// known-set for Zone.WorldObjects, keyed by the object's handle within this playfield (#424).
		//
		// Deliberately separate from `Known` above, because the rule is the OPPOSITE. `Known` is sticky: an NPC sent at
		// entry is never culled mid-load (#245), and Known means "was ever sent". World objects are culled - retail
		// despawns them on range exit and re-sends the full spawn record verbatim on re-entry (measured across the login
		// captures: 565 of 648 repeated sends are byte-identical, the rest differ only in the optional kdtree field, and
		// 212 n3ToClientQuit records carry world-object handles with their own class). Sharing one set would force one
		// of the two rules onto both.
		//
		// Handles are per-PLAYFIELD, not global - the same small handle names a different object on another map - so
		// this set is cleared on every map change along with the rest of the entity's per-zone state.
		public HashSet<uint> KnownObjects { get; } = [];

		// For a PLAYER: the pending ReachArea quest goals (#262) - a location objective is solved when the player moves
		// within range of its point. Loaded once at world entry (null = not yet loaded) so the per-move check is a pure
		// in-memory distance test, no DB hit; entries are removed as they're reached. Empty list = nothing to watch.
		public List<ReachGoalTarget> PendingReachGoals { get; set; } = null!;
	}
}
