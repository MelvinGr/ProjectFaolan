using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A resurrection point on a map - where a dead player can revive. Players must DISCOVER a point (walk near it)
	// before they can respawn there; the starter point on each map is always known (AlwaysKnown). On death a player
	// respawns at the CLOSEST point they know. Position/rotation come from Base3DEntity.
	public class RespawnPoint : Base3DEntity
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// True for the always-available starter point (e.g. Tortage Beach) - known without discovery.
		public bool AlwaysKnown { get; set; }

		// The RDB's `RestorationLevel` - a PERCENT (observed 7/10/20/25/30/50/70/100; 344 points are 10, 176 are 100),
		// so almost certainly the fraction of max health/mana you revive with at this point. Imported but NOT yet
		// applied by the revive path, which still restores a flat amount (#375).
		public float RestorationLevel { get; set; }

		// The RDB's world-editor object label ("ResurrectionAIPoint0", occasionally a designer name like "Outlaw
		// Camp"). NOT a display name and NOT unique - 95 points share "ResurrectionAIPoint0". The player-facing name
		// is not in the RDB at all; it lives in the LDB string index or server-side (#372). Treat this as a dev label.
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
