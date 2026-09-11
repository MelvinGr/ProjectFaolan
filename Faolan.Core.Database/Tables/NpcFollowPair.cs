using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One NPC type that walks with another: a creature retail binds to a second creature's movement rather
	// than giving it a path of its own. This is what the maintainer's *"giner &amp; scruffy should be chasing each
	// other"* annotation names - and the wire says it is not a chase and not mutual. `Ginger` gets an ordinary
	// multi-waypoint path and `Scruffy` gets a `MoveTaskIIR` whose task is a **formation-follow** naming Ginger's
	// handle, so one leads and the other is bound to it.
	//
	// Keyed by the type at both ends, like NpcChatter and NpcAmbientAnimation: the corpus states the pair as a
	// creature-to-creature relationship and repeats it across captures and across placements (four separate
	// `Ginger`/`Scruffy` pairs in three captures, five separate `Nemedian Deathstalker` groups in one).
	//
	// A composite key over both ends, not a `FollowsTemplateId` column on the follower, because a follower type
	// legitimately has more than one leader type: `Slave of Acheron` was captured behind `Black Ring Hellsworn`
	// (10 records) and behind `Black Ring Defender` (9). A single column would have to pick one and silently drop
	// the other, which is exactly the first-record-wins truncation the model conventions warn about.
	//
	// The formation offset is deliberately not a column. It is per-instance, not per-type: the four
	// `Black Dragon Guard Patroller`s that share one captain each hold a different corner of a 3x3 grid - x in
	// {-1, 0, +1}, z in {0, -1, -3} - with their own slot index 0..7. Stamping one of them on the type would stack
	// every follower of that type in the same spot. The runtime derives its own slot from the follower's handle,
	// which is the same deterministic fan-out a player's pets already use.
	//
	// Natural composite key, no surrogate: the importer delete+reinserts these rows on every re-harvest.
	[PrimaryKey(nameof(FollowerTemplateId), nameof(LeaderTemplateId))]
	public class NpcFollowPair
	{
		// The creature that walks with another (loader reads via NpcTemplate.Follows).
		public uint FollowerTemplateId { get; set; }

		[ForeignKey(nameof(FollowerTemplateId))]
		public virtual NpcTemplate FollowerTemplate { get; set; } = null!;

		// The creature it walks with - the one that keeps its own path.
		public uint LeaderTemplateId { get; set; }

		[ForeignKey(nameof(LeaderTemplateId))]
		public virtual NpcTemplate LeaderTemplate { get; set; } = null!;
	}
}
