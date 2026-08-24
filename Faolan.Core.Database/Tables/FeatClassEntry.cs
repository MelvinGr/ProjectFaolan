using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One entry of the client's own FEATCLASS table - RDB type <b>1000043 id 1</b>, the resource whose load
	///     failure the client reports as <i>"Could not load featclass info"</i> (Gitea #862). 9,916 entries of 40
	///     bytes, and the table that says - per CLASS and per LEVEL - which feats a character may have and which of
	///     them the client trains BY ITSELF.
	/// </summary>
	/// <remarks>
	///     ⭐ <b><see cref="Autotrain" /> is why a captured feat list is so short.</b> The level-80 Necromancer casts
	///     all 109 of her spells carrying 25 feats, and every one of the 56 feat ids across the four captured
	///     characters has <c>Autotrain = false</c> here - not one exception. The auto-trained feats never reach the
	///     wire because the client trains them off this table plus the character's class and level; the server
	///     neither grants nor transmits them.
	///     <para>
	///         ⛔ And it must not: <c>FeatHolder::TrainFeat</c> (<c>FUN_01135e20</c>, <c>Feat\FeatHolder.cpp:74</c>)
	///         refuses one outright, asserting <c>pcFeat-&gt;GetCategory() != e_ClassFeat</c> and logging
	///         <i>"Character:%d called TrainFeat on class feat : %d ( will break feats! )"</i>. The client's own
	///         builder is <c>FUN_0078c990</c>, which logs <i>"Autotrain feat: &lt;id&gt; for class: &lt;class&gt; at
	///         level: &lt;level&gt;"</i> - the three columns below.
	///     </para>
	///     <para>
	///         ⚠️ This supersedes <see cref="Feat.AutomaticUnlockCategory" /> as the AUTHORITY on which feats are
	///         automatic. The category was a proxy measured over the spell-granting feats and it is a good one, but
	///         it disagrees with the client on 204 of 9,907 rows (150 rows are category 7063 yet trainable, 54 are
	///         8072 yet auto-trained). The category stays where it is used to classify a spell GRANT; this column is
	///         what decides whether a feat may be put in a character's list.
	///     </para>
	///     <para>
	///         ⚠️ <see cref="FeatId" /> is a WIRE id (<c>CharacterFeat.FeatId</c>'s space), not a catalog id - join
	///         through <see cref="FeatIdMap" /> to reach a <see cref="Feat" />. There is deliberately no FK: 9 of the
	///         9,916 rows name a feat the 1000042 map does not carry, and rejecting the import over 9 rows would
	///         trade the other 9,907 for nothing. Same reasoning as <see cref="FeatPrereq" />.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(Id))]
	public class FeatClassEntry
	{
		// The client's own row id (106..738078). A natural key: it is assigned by the client's data, not by us, so a
		// re-import does not renumber the table (see the model conventions in CLAUDE.md).
		public uint Id { get; set; }

		// The class the row is for. NULL on the 188 rows whose class word is 0 - a real state (a feat that belongs to
		// no class) rather than a missing value, which is why it is not a 0 sentinel. All twelve classes appear, each
		// with ~490 trainable rows and 266-379 auto-trained ones.
		[ConstrainToEnumValues]
		public ConanClass? Class { get; set; }

		// The character level at which the feat becomes available (1..80). For an auto-trained row this is the level
		// the client hands it over at, which is what makes the whole schedule readable: the Necromancer's
		// `Spell Unlocking 20-40` sits at 20/25/30/35 and `Necromancer Class Feat 001` has one rank per level 1..10.
		public uint Level { get; set; }

		// The WIRE feat id. Resolvable through FeatIdMap on 9,907 of the 9,916 rows.
		public uint FeatId { get; set; }

		/// <summary>
		///     Whether the CLIENT trains this feat by itself at <see cref="Level" /> - the row's word 4.
		/// </summary>
		/// <remarks>
		///     True on 3,839 rows, and the split against the feat catalog's own category is near-exact: 3,785 of the
		///     3,839 are category 7063 and 5,918 of the 6,077 false ones are 8072. It is a property of the FEAT
		///     rather than of the row - no feat has both an auto-trained and a trainable entry.
		/// </remarks>
		public bool Autotrain { get; set; }

		// The feat TREE this row belongs to (word 5): 1000001..1000190 for a real tree, 900 for the innate feats
		// that belong to none (`Basic Riding`, the Equip: rows) and 9000 for the class PROGRESSION track.
		//
		// ⭐ The twelve trees 1000177..1000190 (the range is not dense - 1000185 and 1000187 do not exist) are the
		// ALTERNATE-ADVANCEMENT perk trees, one per playable class, gated at levels 20..80 (#150). They are the only
		// ones with a per-placement point cost - see FeatPerkCost, off RDB 1000099 - because an ordinary feat costs
		// one point per rank (FeatRules.PointCost).
		public uint TreeId { get; set; }

		// The feat's COLUMN within its tier row in the tree UI (word 6), 0..8.
		public uint TreeColumn { get; set; }

		// Word 7, undecoded. Set on 3,589 of the 3,839 auto-trained rows and 1,800 of the 6,077 trainable ones, and
		// it does not separate the feats that teach a spell from the ones that do not (564/1236 vs 774/3503). Carried
		// raw rather than dropped or named - `Basic Riding` and the `Weaponsmith Novice/Apprentice/Adept` ladder
		// carry it, which is suggestive of a skill-ish flag and not enough to name one.
		public uint Flag { get; set; }

		// The feat's TIER in its tree (word 8), 1..9 - the row it sits on in the tree UI.
		public uint Tier { get; set; }

		// How many points must already be spent IN THIS TREE before the feat unlocks (word 9). On the ordinary trees
		// this is `(Tier - 1) * 5` on all but 57 of 3,197 rows; on the twelve AA perk trees the schedule is flatter
		// (tiers 1-3 free, 4-6 at 5, 7-9 at 10). Kept as the client's own value rather than derived from Tier,
		// because those exceptions are real data.
		public uint TreePointsRequired { get; set; }
	}
}
