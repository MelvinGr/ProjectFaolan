using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One character's standing with one faction: a signed point total, clamped to the client's own
	// bounds. The rank is not stored - it is a pure function of the points through `FactionLadder`, and storing a
	// derived value is how the two drift apart.
	//
	// The scale is signed and symmetric, which is the part nothing but the client's tuning sheet stated: it runs
	// −1,100,000 … +1,100,000 with a ±5,000 neutral band in the middle, not 0 … 1,000,000. That is what makes the
	// rival-pair rule expressible - farming one side of a pair pushes you below the band with them, i.e. hostile,
	// while the same kills push you up with their rival. See FactionLadder and docs/subsystems/factions.md.
	//
	// Key is the natural (CharacterId, FactionId). A row exists only once a character has moved off zero with that
	// faction, so absence means neutral rather than unknown - which is why the read path treats a missing row as 0
	// instead of seeding sixteen rows per character.
	[PrimaryKey(nameof(CharacterId), nameof(FactionId))]
	public class CharacterFactionStanding
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		public uint FactionId { get; set; }

		[ForeignKey(nameof(FactionId))]
		public virtual Faction Faction { get; set; } = null!;

		// The standing, in the client's own points. Signed; kept inside FactionLadder's bounds by the award path.
		public int Points { get; set; }
	}
}
