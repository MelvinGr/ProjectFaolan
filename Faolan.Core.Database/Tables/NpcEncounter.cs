using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A proximity-triggered scripted encounter: a harmless LURE creature that swaps into a BOSS when a player gets
	// close (#499/#502). Decoded from the Damsel in Distress on Poitain - she cries for help at ~21 world units and,
	// at ~3, despawns while `Eerika of the Maidens` spawns on her exact spot in the same second.
	//
	// HAND-AUTHORED, not harvested: the client drives these from a brain script (the bark's token names
	// `RnBLadyInDistressBrainScript`), so nothing on the wire states the radii - they are measured off a capture's
	// player-movement track and written here. Same standing as the quest-wiring module's fields.
	//
	// Keyed by (map, LURE type) rather than by a placement: the behaviour belongs to the creature on that playfield,
	// so every Damsel on Poitain shares it, and the row survives the npcs module's wipe-and-rebuild of NpcSpawns.
	// Map-scoped because a lure type could plausibly be scripted differently elsewhere and nothing rules it out.
	[PrimaryKey(nameof(MapId), nameof(LureNpcTemplateId))]
	public class NpcEncounter
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The harmless creature standing in the world - an ordinary NpcSpawn placement of this type.
		public uint LureNpcTemplateId { get; set; }

		[ForeignKey(nameof(LureNpcTemplateId))]
		public virtual NpcTemplate LureNpcTemplate { get; set; } = null!;

		// What it becomes. A TYPE, not a placement: the boss has no NpcSpawn row and must not have one - she exists
		// only inside a triggered encounter (#501, which is why her type is harvested at all).
		public uint BossNpcTemplateId { get; set; }

		[ForeignKey(nameof(BossNpcTemplateId))]
		public virtual NpcTemplate BossNpcTemplate { get; set; } = null!;

		// Player distance (world units, X/Z) at which the lure calls for help. Measured: the captured bark fired at
		// 21.3 u while the player was closing.
		public float BarkRadius { get; set; }

		// Player distance at which the swap fires. Measured at 3.0 u - the player was practically on top of her. One
		// observation, so treat both radii as approximate.
		public float TriggerRadius { get; set; }

		// How long after the boss dies before the lure is restored, or null to restore as soon as the corpse clears.
		// The capture shows the lure back ~2 minutes later, but that is a single sighting and confounded with interest
		// streaming, so it is not asserted as a timer.
		public int? ResetSeconds { get; set; }
	}
}
