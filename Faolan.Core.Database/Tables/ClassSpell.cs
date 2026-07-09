using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->spell ACCESS map: which character CLASSES may cast/learn a given spell. A character learns each spell
	// its class has access to when it reaches that spell's unlock level - and the unlock level lives on the Spell
	// (Spells.Level, backfilled from the ellatha list), NOT here: this table is pure membership. Keyed by (Class,
	// SpellId). Class 0 (default(Class)) = generic (every class). Seeded today from the create-flow captures (only the
	// classes/levels we have beach captures for - the L1 starters); a class-aware ellatha scrape fills the rest later.
	//
	// Supersedes ClassStartingSpell (which carried a redundant, always-1 Level column and only modelled the L1 grant).
	[PrimaryKey(nameof(Class), nameof(SpellId))]
	public class ClassSpell
	{
		// FK into the Spell catalog. The importer ensures a catalog row exists for every harvested spell, so this
		// navigation always resolves (load it with .Include(s => s.Spell) when the spell's params/level are needed).
		// virtual so the lazy-loading-proxy DbContext used in tests can build its model.
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; }

		[ConstrainToEnumValues]
		public Class Class { get; set; }
	}
}
