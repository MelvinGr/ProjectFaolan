using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// Builds a 0x737A6DF9 inventory container-entry (ProtoInventoryGame.items, field 4) from a DB Item template,
	// using the typed protobuf schema recovered from the client exe (Faolan.Core/Protobuf, see
	// Faolan.Core/Protobuf/aoc-client-protos.proto). The layout is the client's own:
	//   entry  = InventoryListEntry { pos = slot, item = ProtoInventoryGameEntry }
	//   item   = ProtoInventoryGameEntry { item_id, stat_list[ (0,flags),(23,recordId),(26,count) ], item_data }
	//   data   = ProtoAcgItem { key1=key2=key3 = ProtoDecryptionKey(Data1..4), template1=template2 = Item.Id,
	//                           level, hash = hash }
	// The per-instance state (itemId GUID, inventory record id, stack count, slot) is kept; the template identity -
	// the Data1..4 quad (which is the item's ProtoDecryptionKey) plus the hash hash - comes from the DB Item.
	// Proven byte-exact against the captured "Slave Manacles" entry (SnapshotItemsTests); the typed schema is
	// proven to re-serialize the wire byte-for-byte (AocProtocolDecodeTests).
	public static class SnapshotItems
	{
		// Builds the bare per-item entry (ProtoInventoryGameEntry, i.e. InventoryListEntry.item without the Pos
		// wrapper) from a DB template + per-instance state and an explicit stat list. This is the form
		// GameCharInterface.DispatchAddItem (s2c 0x2020) carries as i_cEntry - the live, mid-session counterpart
		// to the world-entry snapshot; there the slot rides separately as i_nItemPos, so no Pos wrapper.
		public static ProtoInventoryGameEntry InventoryEntry(ulong itemId, Item item, uint hash, IEnumerable<ItemStat> stats, ulong level = 1)
		{
			var key = item.Key;

			var entry = new ProtoInventoryGameEntry
			{
				ItemId = itemId,
				ItemData = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = item.Id,
					Template2 = item.Id,
					Level = (uint)level,
					Hash = hash
				}
			};

			foreach (var stat in stats)
			{
				entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
				{
					Stat = stat.Stat,
					Value = stat.Value
				});
			}

			return entry;
		}

		// The typed form, used directly by FullCharacterRoots' generated containers. The world-entry snapshot
		// entry carries the fixed (flags, recordId, count) stat triple.
		//
		// That triple is the SNAPSHOT's shape and it is a good match for retail's: of 8,479 captured snapshot
		// entries, 100% carry StaticInstance(0x17), 95% Flags(0x00) and 93% StackSize(0x1A), and the rest of the
		// stats are sparse (Duration on 16%, ItemSource on 5 entries total). The LIVE add is a different shape -
		// see LiveAddEntry (#722).
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash, ulong flags = 1, ulong f7 = 1)
		{
			return new ProtoInventoryGame.InventoryListEntry
			{
				Pos = (uint)slot,
				Item = InventoryEntry(itemId, item, hash, [new ItemStat(0, (uint)flags), new ItemStat(23, (uint)recordId), new ItemStat(26, (uint)count)], f7)
			};
		}

		// The LIVE DispatchAddItem (0x2020) form - a different stat list from the snapshot's, which is why it is its
		// own factory rather than a flag on ListEntry. Measured over the 165 captured live adds:
		//
		//   Flags (0x00)          165/165        StackSize (0x1A)       84/165
		//   StaticInstance (0x17) 165/165        Duration  (0x08)      164/165   <- we never sent it
		//                                        ItemSource (0x2E)     164/165   <- we never sent it
		//
		// The live add used to be built from ListEntry - the SNAPSHOT's triple - so it was missing both. That was a
		// real gap: on the snapshot side those two are sparse (Duration 16%, ItemSource 5 of 8,479 entries), so the
		// triple is right THERE and wrong here. #722.
		//
		// ⚠️ The stat list is ORDERED, ascending by stat id, and the order is load-bearing for byte-exactness -
		// DispatchAddItemTests asserts the captured entry rebuilds byte-for-byte and its stats run 0x00, 0x08,
		// 0x17, 0x2E. Keep any new stat in id order.
		//
		// `Duration` takes only two values across all 1,528 captured sightings - 0xFFFFFFFF on 1,497 and 0 on 31,
		// with no template carrying both. UINT_MAX in a field named Duration reads as "does not expire", the same
		// idiom as a float defaulting to FLT_MAX to mean "no cap".
		//
		// `StackSize` is sent unconditionally here, which is NOT what retail does - it rides 84 of 165 adds. The
		// obvious rule ("only when the stack is bigger than one") is refuted: 82 of those 84 carry the value 1, so
		// whatever selects it is not the count. Sending it always is the safe side of that unknown, since the value
		// we send is correct either way.
		//
		// `ItemSource` is deliberately NOT sent. It is a provenance CODE with 8 observed values
		// (1,2,5,7,13,15,16,19) and guessing is worse than omitting - a wrong provenance is a false claim, a missing
		// one is a gap. Decoding it needs each add correlated with the c2s action that produced it; grouping by
		// capture is too coarse, since values 2, 7 and 15 all appear within the same sessions. Tracked separately.
		public static ProtoInventoryGameEntry LiveAddEntry(ulong itemId, ulong recordId, ulong count, Item item, uint hash, ulong level = 1, ulong flags = 1)
		{
			return InventoryEntry(itemId, item, hash,
			[
				new ItemStat(0, (uint)flags),
				new ItemStat((uint)CharacterStatId.Duration, NoExpiry),
				new ItemStat(23, (uint)recordId),
				new ItemStat(26, (uint)count)
			], level);
		}

		// The value retail puts in Duration on an item that does not expire: 1,497 of 1,528 captured sightings.
		public const uint NoExpiry = uint.MaxValue;

		// One (stat id, value) record of a ProtoInventoryGameEntry.stat_list. The world-entry snapshot uses a
		// fixed triple (ListEntry below); the incremental DispatchAddItem (0x2020) s2c carries a richer,
		// per-instance set captured from retail, so InventoryEntry takes the stat list as a parameter.
		public readonly record struct ItemStat(uint Stat, uint Value);
	}
}
