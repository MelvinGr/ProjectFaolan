using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->spell access map: which character classes may cast/learn a given spell. A character learns each spell
	// its class has access to when it reaches that spell's unlock level - and the unlock level lives on the Spell
	// (Spells.Level, from the RDB), not here: this table is membership + the create-grant flag. Keyed by (Class,
	// SpellId). Class 0 (default(Class)) = generic (every class).
	//
	// Membership comes from the client's own catalog: a Spell.GuiStackingLine encoded as
	// `ConanClass * 1000 + lineIndex` names the class that line belongs to, so RdbSpellImporter fills all twelve
	// classes offline. The create-flow captures add the two facts the catalog cannot see - IsStarter and GrantLevel -
	// and are never overwritten by it.
	//
	// A second offline membership source: the client's featclass schedule. A feat the client auto-trains
	// for a class, teaching a spell, says that class has access to it - `RdbFeatImporter.DeriveClassSpellGrantLevels`
	// writes those rows. It reaches exactly what the line encoding cannot: the 12 it adds all carry a
	// `GuiStackingLine` that is not of the `class * 1000 + index` form (71, 76, 232 on the Herald's
	// `Arcane Abatement` ranks 2-5, `Burning Skull` and `Exultation of Xotli`), so the class map was blind to them by
	// construction - while rank 1 of the same family had a row, from the captured spellbook source.
	// Level > 0 only: every class auto-trains the four `Dance:` emotes at level 5, and a Level-0 record belongs to
	// no class. That exclusion is the difference between 12 rows and 57.
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

		// True = a create capture shows this class being granted this spell at creation (0x200A), so it is part of the
		// starter kit regardless of the spell's level requirement. Membership and level are independent facts and must
		// not be conflated: retail grants the necro Ice Strike (Rank 1) at create even though the spell's requirement
		// is level 7 (the client's own record says 7). The starter kit is a fixed list, not a level query.
		//
		// Stating the grant outright is what keeps Spells.Level true. Encoding it by pinning Spells.Level = 1 for
		// granted spells - so that a "Level <= charLevel" query happens to return the captured set - overwrites a
		// correct level with a sentinel and makes Level mean two things at once. The RDB leads on level data,
		// including over captures.
		public bool IsStarter { get; set; }

		// The level at which this class is actually granted this spell. Two sources, and they agree:
		//
		//   1. a capture showing the grant - a NewLevelIIR (0x6D314743) ding followed by the 0x200A grant;
		//   2. the client's own featclass schedule - the level of the auto-trained feat that teaches the spell
		//      (`FeatClassEntry`, RDB 1000043), derived offline by `RdbFeatImporter.DeriveClassSpellGrantLevels`
		//      It reproduces all 14 capture-proven levels exactly, including the ten where `Spells.Level`
		//      disagrees with the capture, and covers the whole catalog instead of the handful a capture caught.
		//
		// A capture still wins where both speak - it never has to, but the rule that retail leads should not be
		// inverted by a derivation that merely happens to match it. Null = neither source answers.
		//
		// This is the real unlock gate and it is not `Spells.Level`: Desecrating Essence R1 is granted at 3 but
		// Spells.Level says 5; Contract of Protection R1 at 5 vs 10; Ice Strike R1 in the create kit at 1 vs 7. So the
		// RDB field is a trainer / full-power level, a different fact, not a worse measurement of this one.
		//
		// Nor does retail grant at or below that field: `Pestilential Blast (Rank 1)` is granted at 7 where the record
		// says 1, and `Dance: Aquilonian` at 5 where the record says 0. Over the 432 rows carrying a grant level, 363
		// agree with `Spells.Level`, 69 differ, and 14 of those differ upward. The two fields are independent in both
		// directions.
		//
		// `Spells.Level` is the fallback only where neither source above answers - the minority of rows. It
		// is also the source that is wrong where they differ: it breaks one rank ladder (`Quell the Ether`
		// 45/30/45/60/75) and gates two spells above the level a captured character demonstrably holds them at.
		public byte? GrantLevel { get; set; }
	}
}
