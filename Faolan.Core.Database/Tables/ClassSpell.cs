using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->spell ACCESS map: which character CLASSES may cast/learn a given spell. A character learns each spell
	// its class has access to when it reaches that spell's unlock level - and the unlock level lives on the Spell
	// (Spells.Level, from the RDB), NOT here: this table is membership + the create-grant flag. Keyed by (Class,
	// SpellId). Class 0 (default(Class)) = generic (every class). Seeded from the create-flow captures (only the
	// classes we have beach captures for) plus the class-aware wiki scrape.
	//
	// Supersedes ClassStartingSpell (which carried a redundant, always-1 Level column and only modelled the L1 grant).
	[PrimaryKey(nameof(Class), nameof(SpellId))]
	public class ClassSpell
	{
		// True = a create capture SHOWS this class being granted this spell at creation (0x200A), so it is part of the
		// starter kit regardless of the spell's level requirement. These are two INDEPENDENT facts and conflating them
		// was a real bug: retail grants the necro Ice Strike (Rank 1) at create even though the spell's requirement is
		// level 7 (the RDB and the ellatha scrape agree on 7). The starter kit is a fixed LIST, not a level query.
		//
		// This used to be encoded by PINNING Spells.Level = 1 for granted spells and pushing every other level-1 row of
		// that class to 2, so that a level-gated "Level <= charLevel" query happened to return the captured set. That
		// overwrote a correct level with a sentinel and made Level mean two things at once. Now the level stays true
		// and the grant is stated outright. (Melvin: the RDB leads on level data, including over captures.)
		public bool IsStarter { get; set; }

		// FK into the Spell catalog. The importer ensures a catalog row exists for every harvested spell, so this
		// navigation always resolves (load it with .Include(s => s.Spell) when the spell's params/level are needed).
		// virtual so the lazy-loading-proxy DbContext used in tests can build its model.
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		[ConstrainToEnumValues]
		public Class Class { get; set; }
	}
}
