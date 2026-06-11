using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	// Item template identity. Data1..Data4 are the item's stable identity quad - the 20-byte payload repeated in
	// the inventory snapshot tail and the op-0x1d add-item packet; the client rejects items whose quad isn't a
	// known template (the add/remove stall). Sourced from the old C++ server's `p_items` (Archive/) AND harvested
	// from real captured inventories (every item a player has held). Archetype (snapshot tail.f8) is a per-template
	// hash NOT in p_items - only captures have it - so an item is fully buildable iff Archetype != 0 and it has a
	// Data quad. See docs/spawn-dissection-plan.md Phase 5.
	public class Item : BaseEntity
	{
		public byte Level { get; set; }

		[MaxLength(255)]
		public string Name { get; set; }

		public uint Archetype { get; set; }

		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }
	}
}
