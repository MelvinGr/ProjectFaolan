using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     What a player's client has been told and what the player is on: the entity handles it has spawned
	///     (<see cref="Known" />, <see cref="Confirmed" />) and the world objects it has been streamed, plus the quest
	///     id sets and the pending reach goals the tick resolves against its position.
	/// </summary>
	/// <remarks>A component of <see cref="WorldEntity" />; an NPC holds an empty one.</remarks>
	public sealed class PlayerKnowledge
	{
		/// <summary>
		///     The quest ids the player currently has active, which drive quest-gated NPC visibility. Refreshed at world
		///     entry and whenever the player's quest state changes - accept, advance or complete.
		/// </summary>
		public HashSet<uint> ActiveQuestIds { get; } = [];

		/// <summary>
		///     The quest ids the player has completed, which drive "hide once done" NPC gating - Casilda is gone from
		///     the beach once her escort quest is completed. Loaded alongside <see cref="ActiveQuestIds" />.
		/// </summary>
		public HashSet<uint> CompletedQuestIds { get; } = [];

		/// <summary>
		///     The handles the client has confirmed constructing, by polling 0x2020 with the entity handle as the
		///     request id, which the server answers with a 0x2008 SpawnCheck that renders it.
		/// </summary>
		/// <remarks>
		///     <see cref="Known" /> means the spawn was sent; this means the client built it. A despawn
		///     (n3ToClientQuit) for a known-but-unconfirmed handle hits a null-dynel loader crash on a still-loading
		///     client, so interest only despawns confirmed handles.
		/// </remarks>
		public HashSet<uint> Confirmed { get; } = [];

		/// <summary>
		///     The handles this player has been sent a spawn for. Interest management diffs against this set to decide
		///     what to spawn and despawn as entities move in and out of range. Unused for NPCs.
		/// </summary>
		public HashSet<uint> Known { get; } = [];

		/// <summary>
		///     The static world objects - props, doors, vines, destructibles - this player currently has rendered: the
		///     interest known-set for <see cref="Zone.WorldObjects" />, keyed by the object's handle within this
		///     playfield.
		/// </summary>
		/// <remarks>
		///     Deliberately separate from <see cref="Known" />, because the rule is the opposite. <see cref="Known" />
		///     is sticky - an NPC sent at entry is never culled mid-load, and it means "was ever sent" - while world
		///     objects are culled: retail despawns them on range exit and re-sends the full spawn record verbatim on
		///     re-entry (measured across the login captures: 565 of 648 repeated sends are byte-identical, the rest
		///     differ only in the optional kdtree field, and 212 n3ToClientQuit records carry world-object handles with
		///     their own class). Sharing one set would force one of the two rules onto both.
		///     <para>
		///         Handles are per-playfield rather than global - the same small handle names a different object on
		///         another map - so this set is cleared on every map change along with the rest of the entity's
		///         per-zone state.
		///     </para>
		/// </remarks>
		public HashSet<uint> KnownObjects { get; } = [];

		/// <summary>
		///     The pending ReachArea quest goals: a location objective is solved when the player moves within range of
		///     its point.
		/// </summary>
		/// <remarks>
		///     Loaded once at world entry - null until then - so the per-move check is a pure in-memory distance test
		///     with no database hit. Entries are removed as they are reached, and an empty list means nothing to watch.
		/// </remarks>
		public List<ReachGoalTarget> PendingReachGoals { get; set; } = null!;
	}
}
