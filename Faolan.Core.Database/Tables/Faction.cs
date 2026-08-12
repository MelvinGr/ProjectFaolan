using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One faction of the Khitai reputation system (Gitea #310), from the client's own
	// `data/FactionRelations.xml` (RDB 1010701/1) joined to its display name in `LDB[254]`.
	//
	// ⚠️ The ticket filed this as *"blocked on expansion content + captures"*, and it was blocked on neither: the
	// whole ruleset ships in the client, in plaintext XML, and the corpus has zero faction packets in either
	// direction. See docs/subsystems/factions.md for the recovery and FactionRelationTable for the grammar.
	//
	// KEY is the faction's own id (10, 20, … 330), which is what the wire carries and what the XML keys by - a
	// natural key, so a re-harvest reproduces it rather than churning a surrogate (the model conventions in
	// CLAUDE.md). `ValueGeneratedNever` on it, since 10 is a real id and not "please assign one".
	[PrimaryKey(nameof(Id))]
	public class Faction
	{
		public uint Id { get; set; }

		// The display name, from LDB[254]'s bare-name block. Falls back to the XML's authoring comment when the LDB
		// has no row - provenance rather than identity, which is why the two are joined by NAME in the importer and
		// the join is reported rather than assumed.
		public string Name { get; set; } = string.Empty;

		// The RDB resource id of the faction's icon, from the standard entry's `iconid`.
		public uint IconId { get; set; }

		// The faction this one OPPOSES, or null for the four non-Khitai ids and the two that have no rival. Derived
		// at import from the standard entry's award list: killing one of this faction's NPCs costs standing with it
		// and grants the same amount to exactly one other faction, which is the rival.
		//
		// ⚠️ NOT a foreign key, deliberately. 100 `The Hyrkanians` names 90 as its rival while 90 names 100 back, so
		// the relation is symmetric here - but the LDB gives 100 no rank ladder, so the rivalry is one-sided from the
		// PLAYER's side. A self-referencing FK would model a constraint the data does not carry.
		public uint? RivalFactionId { get; set; }

		// Whether a PLAYER can hold standing with this faction at all - true exactly for the nine ids that carry a
		// four-rank title ladder in LDB[254] (10..90). The other seven (100, 110, 120, 200, 310, 320, 330) look like
		// NPC-only reputations: no ladder, and the four non-Khitai ones carry no encounter-tier overrides either.
		public bool HasPlayerRanks { get; set; }

		public virtual ICollection<FactionKillAward> KillAwards { get; set; } = [];
	}
}
