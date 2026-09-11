using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// One achievement, keyed by the id the wire calls a tag. `GameCharInterface.DispatchAddTag` awards one as a
	// bare `[u32 tagId]`, and `CharacterTags` records which a character holds - so without this table a granted
	// achievement is a number and nothing can name it.
	//
	// Harvested from the client's own LDB by the `rdb-achievements` module: category **55701** is the name and **55700**
	// the description, both keyed by the tag id. Verified against every tag the corpus awards - 7917 is
	// *"Seeing Red" / "Achieve a kill streak."*, 3509 *"Pet Collector" / "Collect and summon your first pet,
	// companion or mini-pet."*, 716 *"Old Tarantia Docks"* (a vista award), 2562 *"Spider Caverns" / "Defeat the
	// Lurking Horror in the Spider Caverns"*.
	//
	// A natural-key catalog like Item/Spell/Map: the id is the client's, never DB-generated.
	//
	// The catalog carries internal entries beside the real ones - 3104 is *"XX Dungeon Level Range"*, 3316
	// *"XX Mammoth - Blue"* - so a row existing does not mean the client shows it to a player. And a name without a
	// description is normal (about half): 3689 *"Puma"* has none. Both are the client's data as shipped, kept rather
	// than filtered, because which entries are player-facing is not something the strings state.
	public class Achievement
	{
		// The tag id, as `DispatchAddTag` sends it.
		[Key]
		public uint Id { get; set; }

		// LDB 55701 - the achievement's title ("Seeing Red").
		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// LDB 55700 - what earns it ("Achieve a kill streak."). Null for the ~half of the catalog that ships without
		// one; Not a gap in the import.
		[MaxLength(512)]
		public string? Description { get; set; }
	}
}
