using System.ComponentModel.DataAnnotations;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	// An item TEMPLATE: the stable identity (Data quad + Archetype) the client needs to realize an item, keyed by
	// the template id (BaseEntity.Id). Sourced from the old C++ `p_items` (Archive/), real captured inventories, and
	// AH listings - see the per-field notes (the Data quad vs Archetype distinction is the crux of what renders how).
	public class Item : BaseEntity
	{
		// Id (BaseEntity) is the item TEMPLATE id (== ProtoAcgItem.Template1/Template2 on the wire).

		// Level requirement / item level. byte on the wire; mostly 0 for capture-harvested items (the RDB carries
		// the real value), so level-appropriate filtering is limited until RDB item levels are imported.
		public byte Level { get; set; }

		[MaxLength(255)]
		public string Name { get; set; }

		// Normal / Quest / Resource, set at import from the container an item was captured in (default Normal).
		public ItemType Type { get; set; }

		// Per-TEMPLATE hash the client needs (alongside the Data quad) to fully realize/render the item from its RDB.
		// On the wire it is ProtoAcgItem.Hash (and the FullChar snapshot tail's f8 field). It is NOT in the old C++
		// `p_items`, so it can only be HARVESTED from a capture where the item sat in an inventory (a 0x737A6DF9
		// snapshot or a 0x2020 add-item) - it is absent from sources that only describe the template abstractly (AH
		// search-results carry Hash=0). An item is "fully buildable" (renders via the archetype-gated world-entry
		// snapshot) iff Archetype != 0 AND it has a Data quad; the more forgiving live DispatchAddItem (0x2020) path
		// renders an item by template even with Archetype/Hash == 0 (used for AH-bought items we lack an archetype for).
		public uint Archetype { get; set; }

		// The item's stable identity QUAD (Data1..Data4) - the per-template 16-byte ProtoDecryptionKey repeated in
		// the inventory snapshot tail and the add-item packet, and the TEA key for the item's encrypted RDB template.
		// The client REJECTS an item whose quad isn't a template it knows (the add/remove stall). Sourced from the
		// old C++ `p_items` (Archive/) AND harvested from real captured inventories (every item a player has held)
		// AND from AH listings (the listing carries the per-template quad). Data0 is a separate leading word (0 for
		// quad-only sources like AH listings). See docs/archive/spawn-dissection-plan.md Phase 5.
		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }
	}
}
