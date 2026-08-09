using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// ONE GEM socketed into one ItemInstance, at one socket index (#750).
	//
	// ⚠️ The socket CONTENTS are the truth; the stats it grants are DERIVED from the gem's own template. That is not a
	// design preference, it is what the wire says: neither form of a socket carries a stat value.
	//
	//   - persistent: ProtoInventoryGameEntry.slot_map (field 4), repeated { slot: uint, item: ProtoAcgItem } - the
	//     socket index and the gem's own template identity. The login snapshot restates every socketed item's full
	//     gem list, which is how a gemmed item survives a relog. 1,187 slot_map blocks across 56 captures, socket
	//     indices 0/1/2 only (up to THREE sockets per item), 13 distinct gem templates.
	//   - live: DispatchAddToItemSlot (GameCharInterface 0x1D) carries the same pair for ONE socket, followed by an
	//     ordinary DispatchRemoveItem for the gem leaving the bag. The socketed item's container entry is NOT re-sent.
	//
	// So anything that stores a socket stores an ITEM REFERENCE, never a stat list. This is also why sockets and
	// (eventual) per-instance stats are separate tables: "which gem is in socket 2" has to survive removal and
	// re-socketing.
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json frame 8248 (the live add)]
	[PrimaryKey(nameof(ItemInstanceId), nameof(SocketIndex))]
	public class ItemInstanceSocket
	{
		// The gem's catalog template. ⚠️ Nine of the 13 gem templates the corpus socket lists name are MISSING from
		// Items, so a restored socket cannot name its gem for those - a content gap, tracked separately. The importer
		// skips a socket whose gem it cannot resolve rather than inventing one.
		public uint GemItemId { get; set; }

		[ForeignKey(nameof(GemItemId))]
		public virtual Item Gem { get; set; } = null!;

		[ForeignKey(nameof(ItemInstanceId))]
		public virtual ItemInstance ItemInstance { get; set; } = null!;

		public uint ItemInstanceId { get; set; }

		// 0-based, as the wire carries it (`slot`). Only 0/1/2 occur across the corpus.
		public uint SocketIndex { get; set; }
	}
}
