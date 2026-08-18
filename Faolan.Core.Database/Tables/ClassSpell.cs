using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->spell ACCESS map: which character CLASSES may cast/learn a given spell. A character learns each spell
	// its class has access to when it reaches that spell's unlock level - and the unlock level lives on the Spell
	// (Spells.Level, from the RDB), NOT here: this table is membership + the create-grant flag. Keyed by (Class,
	// SpellId). Class 0 (default(Class)) = generic (every class).
	//
	// MEMBERSHIP comes from the client's own catalog (#296): a Spell.GuiStackingLine encoded as
	// `ConanClass * 1000 + lineIndex` names the class that line belongs to, so RdbSpellImporter fills all twelve
	// classes offline. The create-flow captures add the two facts the catalog cannot see - IsStarter and GrantLevel -
	// and are never overwritten by it. An AoC-fandom-wiki scrape used to be the membership source, on the belief that
	// "the client RDB spell record has no class field"; it has one, and the two agreed on 342 of the 343 rows where
	// both spoke.
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
		public virtual Spell Spell { get; set; } = null!;

		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		// True = a create capture SHOWS this class being granted this spell at creation (0x200A), so it is part of the
		// starter kit regardless of the spell's level requirement. These are two INDEPENDENT facts and conflating them
		// was a real bug: retail grants the necro Ice Strike (Rank 1) at create even though the spell's requirement is
		// level 7 (the client's own record says 7). The starter kit is a fixed LIST, not a level query.
		//
		// This used to be encoded by PINNING Spells.Level = 1 for granted spells and pushing every other level-1 row of
		// that class to 2, so that a level-gated "Level <= charLevel" query happened to return the captured set. That
		// overwrote a correct level with a sentinel and made Level mean two things at once. Now the level stays true
		// and the grant is stated outright. The RDB leads on level data, including over captures.
		public bool IsStarter { get; set; }

		// The level at which this class is actually granted this spell. TWO sources, and they agree:
		//
		//   1. a capture SHOWING the grant - a NewLevelIIR (0x6D314743) ding followed by the 0x200A grant (#376);
		//   2. ⭐ the client's own featclass SCHEDULE - the level of the auto-trained feat that teaches the spell
		//      (`FeatClassEntry`, RDB 1000043), derived offline by `RdbFeatImporter.DeriveClassSpellGrantLevels`
		//      (#853). It reproduces all 14 capture-proven levels EXACTLY, including the ten where `Spells.Level`
		//      disagrees with the capture, and covers the whole catalog instead of the handful a capture caught.
		//
		// A capture still wins where both speak - it never has to, but the rule that retail leads should not be
		// inverted by a derivation that merely happens to match it. Null = neither source answers.
		//
		// This is the real unlock gate and it is NOT `Spells.Level`. Retail grants at or BELOW that field, never above
		// - Desecrating Essence R1 is granted at 3 but Spells.Level says 5; Contract of Protection R1 at 5 vs 10; Ice
		// Strike R1 in the create kit at 1 vs 7. So the RDB field is a trainer /
		// full-power level, a DIFFERENT fact, not a worse measurement of this one.
		//
		// ⚠️ `Spells.Level` is the fallback only where NEITHER source above answers - which is now the minority. It
		// is also the source that is wrong where they differ: it breaks one rank ladder (`Quell the Ether`
		// 45/30/45/60/75) and gates two spells above the level a captured character demonstrably holds them at.
		public byte? GrantLevel { get; set; }
	}
}
