using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A resurrection point on a map - where a dead player can revive. Players must DISCOVER a point (walk near it)
	// before they can respawn there; the starter point on each map is always known (AlwaysKnown). On death a player
	// respawns at the CLOSEST point they know. Position/rotation come from Base3DEntity.
	public class RespawnPoint : Base3DEntity
	{
		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// True for the always-available starter point (e.g. Tortage Beach) - known without discovery.
		public bool AlwaysKnown { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }
		public uint MapId { get; set; }
	}
}
