using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Progression;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     A static world object as it exists in a loaded zone: its database row plus the wire handle this zone
	///     streamed it under.
	/// </summary>
	/// <remarks>
	///     The handle is live state, minted by <see cref="Zone.AllocateHandle" /> when the zone loads its objects, and
	///     must never become a column: it is session-volatile, so it cannot be a persistence key, and a captured handle
	///     does not even identify an object uniquely. A row is inert data; the handle belongs to the live copy of the
	///     map, the same way an NPC's does.
	/// </remarks>
	public sealed record LiveWorldObject(uint Handle, WorldObject Row)
	{
		public Vector3 Position => Row.Position;

		public uint SubOpcode => Row.SubOpcode;

		/// <summary>
		///     A harvest node's remaining health. Null for everything that is not a harvest node, so it doubles as
		///     "is this harvestable at all".
		/// </summary>
		/// <remarks>
		///     Live state rather than a column, for the same reason the handle is: it is per-loaded-zone. Retail
		///     streams each node with whatever health the shared world has left it at, and nodes regrow; neither the
		///     sharing nor a respawn timer is modelled, so a node starts full on every map load. That is a deliberate
		///     simplification - no respawn cadence has been measured.
		/// </remarks>
		public uint? Health { get; set; }

		/// <summary>
		///     The object's door state (proto field 12).
		/// </summary>
		/// <remarks>
		///     Live state for the same reason <see cref="Health" /> is: a capture is evidence of what a door was doing
		///     when the packet was sent, not of how it was placed - the map 4020 "Wharf Door" is captured closed in one
		///     session and open in another - which is why it has no column. Closed is the resting state, and what all
		///     2400 captured objects were streamed in. Nothing mutates it yet, because opening a door is not modelled,
		///     but the world owns the value, so wiring that up is a one-line change rather than a schema one.
		/// </remarks>
		public bool IsClosed { get; set; } = true;

		/// <summary>
		///     True while this node still has something to give. Health bottoms out at 1 in every capture rather than
		///     0, and the node despawns there, so exhausted is at or below the exhausted floor rather than zero.
		/// </summary>
		public bool IsHarvestable => HarvestCatalog.IsHarvestable(Row.DisplayCategory, Row.DisplayId) && (Health ?? HarvestCatalog.NodeMaxHealth) > HarvestCatalog.ExhaustedHealth;

		/// <summary>
		///     The quest gate travels with the row: an object no player quest reveals is absent entirely.
		/// </summary>
		public bool SpawnsFor(IReadOnlySet<uint> quests)
		{
			return Row.SpawnsFor(quests);
		}
	}
}
