using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One saved FEAT SPEC (the retail multi-spec feature - the client ships Data/Gui/Default/Flash/MultiSpecsFeats.swf).
	// A character's trained feats are not one flat list: the player spawn's pre-IIR scaffold carries a count-framed
	// sublist of specs, each with its OWN name and its own trained-feat list (SpawnPreFrameSublistEntry).
	// Six captured frames carry two or four named specs - "PvE - Lance" (23 feats), "PvP" (27), "PvP - Decap/Protection"
	// (28), "PvP - Decap/Armor" (28), "Hybrid" (27), "Berserker: No Decap" (26); every other captured character carries
	// exactly ONE spec with an EMPTY name, which is why the sublist read as a lead-in to a bare feat list for so long.
	// See Gitea #474 / #466 and docs/re/player-spawn-generation.md.
	//
	// Natural composite key (the importer delete+reinserts these, so no surrogate): the character plus the spec's own
	// wire index. Index is both the entry's `key` u32 and its `flag` byte on the wire - they are the same value on
	// every captured entry, which is why they are stored once here rather than carried twice.
	[PrimaryKey(nameof(CharacterId), nameof(Index))]
	public class CharacterFeatSpec
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The spec's position in the sublist, from 0. Wire order IS this order.
		public int Index { get; set; }

		// The player's own name for the spec. EMPTY on every single-spec character, which is the normal case and not
		// a missing value - retail sends a zero-length wire string there.
		public string Name { get; set; } = "";
	}
}
