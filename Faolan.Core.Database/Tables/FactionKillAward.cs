using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// What killing one NPC of `VictimFactionId` at encounter `Tier` is worth, per faction it moves.
	// Straight from `data/FactionRelations.xml`'s `<opposing_faction>` lines - the standard section is tier 0 and
	// `override_relations` carries tiers 1..5, a relation id being `factionBase + tier`.
	//
	//   tier 0 solo normal 120 · 1 solo boss 180 · 2 group normal 720 · 3 group boss 1800 · 4 raid normal 1440
	//   · 5 raid boss 2400
	//
	// The file states its own design rule in a header comment: *"Faction points are usually proportional to 60.
	// This way if people make a team and start grinding reputation, there is no situation where an NPC is worth more
	// in team than alone."* So the group-normal 720 is six shares of 120, and raid normal is 1440 because a 24-player
	// raid clears in groups of six. A consumer that hands the whole row to every member would break that rule; the
	// row is what the encounter is worth, not what one player receives.
	//
	// Key is the natural (VictimFactionId, Tier, FactionId) - the three things one line states - so a re-harvest is a
	// clean per-line upsert and needs no surrogate.
	[PrimaryKey(nameof(VictimFactionId), nameof(Tier), nameof(FactionId))]
	public class FactionKillAward
	{
		// The faction the killed NPC belongs to (NpcStatCore.FactionId).
		public uint VictimFactionId { get; set; }

		[ForeignKey(nameof(VictimFactionId))]
		public virtual Faction VictimFaction { get; set; } = null!;

		// The encounter tier, 0..5 - the remainder of the relation id over 10.
		public int Tier { get; set; }

		// The faction whose standing moves. Not a foreign key: the award list is free to name a faction that carries
		// no standard entry of its own, and dropping such a line to satisfy a constraint would silently change the
		// payout matrix.
		public uint FactionId { get; set; }

		// Signed: negative with the victim's own faction, positive with its rival.
		public int Points { get; set; }
	}
}
