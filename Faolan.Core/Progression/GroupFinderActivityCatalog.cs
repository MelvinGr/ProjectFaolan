using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The group finder's activity catalog: which playfield an ActivityId enters, and where in it.
	/// </summary>
	/// <remarks>
	///     Read out of the client's own <c>GroupFinderActivitiesTable</c> - RDB 1000627, 81 records, little-endian,
	///     whose grammar is written up in <c>docs/rdb/client-data-tables.md</c>.
	///     <para>
	///         The ActivityId is the record's 1-based sequence number, and the capture proves it rather than the table
	///         asserting it: the one signup in the corpus carries <c>CategoryId=4;ActivityId=38</c>, and record seq 38
	///         is playfield 1092 (The Amphitheatre of Karutonia) carrying the destination <c>(112, 70, 39)</c> - the
	///         exact arrival the captured <c>N3TeleportIIR</c> put the player at.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json the 20:22 entry]
	///     </para>
	///     <para>
	///         The id is not relative to the category, which is the reading the table's trailing 94-entry section
	///         invites and which the same capture refutes: counting activity 38 within category 4's own block lands on
	///         record 65 (Oasis of Zaara), not on 1092. That section is a secondary structure - the finder window's
	///         category/child layout - and is deliberately not walked here, because the flat sequence already answers
	///         the only question the server asks of this table.
	///     </para>
	///     <para>
	///         Only the records carrying an explicit <c>[x][y][z][heading]</c> are listed. The rest of the six-man
	///         block addresses its destination by a landmark name instead (<c>Frost_Swamp_Zone_In</c>,
	///         <c>Imirian_Ravine_Zone_In</c>, <c>Oasis_of_Zaara_Zone_In</c>, <c>StartPlace</c>), which needs a
	///         per-playfield landmark table we do not hold - so those activities resolve to a playfield with no
	///         arrival and are left out rather than entered at an invented point. The 24-man <c>LFRTeleport</c> raid
	///         rows (seq 2..11) are the raid finder, a different queue, and carry no coordinate either.
	///     </para>
	/// </remarks>
	public static class GroupFinderActivityCatalog
	{
		/// <summary>
		///     Every activity whose destination the client's table states outright, keyed by ActivityId.
		/// </summary>
		public static readonly ImmutableDictionary<uint, GroupFinderActivity> Activities = new GroupFinderActivity[]
		{
			new(29, 3001, 688.0f, 41.0f, 191.0f, 180, "Black Castle"),
			new(33, 1026, 160.0f, 0.0f, 42.0f, 0, "Cradle of Decay"),
			new(34, 1024, 323.0f, 16.0f, 106.0f, 2, "Toirdealbach's Tomb"),
			new(35, 2030, 96.0f, 18.0f, 146.0f, 90, "The Cistern"),
			new(36, 2032, 362.0f, 64.0f, 466.0f, 180, "The Main System"),
			new(37, 2033, 355.0f, 41.0f, 272.0f, 270, "The Catacombs"),
			// The captured one - this row is what pins ActivityId == seq for all the others.
			new(38, 1092, 112.0f, 70.0f, 39.0f, 45, "The Amphitheatre of Karutonia"),
			new(39, 1082, 279.0f, 52.0f, 346.0f, 189, "Halls of Eternal Frost"),
			new(40, 3035, 143.0f, 11.0f, 185.0f, 270, "The Scorpion Cave"),
			new(41, 3033, 349.0f, 13.0f, 384.0f, 180, "Caravan Raider's Hideout")
		}.ToImmutableDictionary(a => a.ActivityId);

		/// <summary>
		///     Every playfield this table sends a group finder queue into.
		/// </summary>
		public static readonly ImmutableHashSet<uint> DestinationMapIds = Activities.Values.Select(a => a.MapId).ToImmutableHashSet();

		/// <summary>
		///     True when a playfield is one the group finder queues into - which is the only classification the corpus
		///     supports for <c>DispatchInDungeonPlayfield</c>.
		/// </summary>
		/// <remarks>
		///     The corpus carries three frames of that message and it is a per-playfield flag, not a constant: both
		///     <c>01</c> frames land seconds after a <c>DispatchRedirectToServer</c> into a group-finder activity's
		///     instance - Black Castle (3001) and The Amphitheatre of Karutonia (1092), which are activities 29 and 38
		///     of this very table - while the lone <c>00</c> is an ordinary city world-entry with no redirect at all.
		///     So "the finder queues into it" is what separates the two populations, and it is the whole of what the
		///     evidence separates them by.
		///     <para>
		///         This is therefore narrower than "is a dungeon". A dungeon a player walks into on foot certainly
		///         sends <c>01</c> too, but nothing in the corpus shows one, and there is no per-playfield dungeon flag
		///         in the client data we hold to widen it with. Widening it would be inventing the classification
		///         rather than reading it. [V0: the dungeon flag for a playfield the finder does not queue into]
		///     </para>
		/// </remarks>
		public static bool IsFinderDestination(uint mapId)
		{
			return DestinationMapIds.Contains(mapId);
		}

		/// <summary>
		///     The activity an id names, or null when the table gives it no explicit destination (a landmark-addressed
		///     row, a raid row, or an id outside the table).
		/// </summary>
		public static GroupFinderActivity? ActivityFor(uint activityId)
		{
			return Activities.TryGetValue(activityId, out var activity)
				? activity
				: null;
		}
	}

	/// <summary>One group-finder activity, as RDB 1000627 states it.</summary>
	/// <param name="ActivityId">The id the client sends - the record's 1-based sequence number.</param>
	/// <param name="MapId">The destination playfield (the record's word E). All ten resolve in our own Maps table.</param>
	/// <param name="X">The record's own destination, in playfield coordinates.</param>
	/// <param name="Y">See <paramref name="X" />.</param>
	/// <param name="Z">See <paramref name="X" />.</param>
	/// <param name="HeadingDegrees">
	///     The arrival facing, in degrees, as the table carries it. Not applied yet: the teleport path takes a
	///     position and faces the map's default, so this is recorded where it was read rather than dropped.
	/// </param>
	/// <param name="MapName">The playfield's name, for logs - the same string our Maps row carries.</param>
	public sealed record GroupFinderActivity(uint ActivityId, uint MapId, float X, float Y, float Z, uint HeadingDegrees, string MapName)
	{
		/// <summary>The destination as the teleport path wants it.</summary>
		public Vector3 Arrival => new Vector3(X, Y, Z);
	}
}
