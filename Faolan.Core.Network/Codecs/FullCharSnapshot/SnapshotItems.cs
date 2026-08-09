using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
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
		public static ProtoInventoryGameEntry InventoryEntry(ulong itemId, Item item, uint hash, IEnumerable<ItemStat> stats, string? creatorName = null,
			IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			var entry = new ProtoInventoryGameEntry
			{
				ItemId = itemId,
				ItemData = Identity(item, hash)
			};

			// The GEMS socketed into this item (#750). `slot_map` (field 4) is the PERSISTENT form of a socket, and it
			// is how a gemmed item survives a relog: the login snapshot restates every socketed item's full gem list.
			// Each entry names the gem BY TEMPLATE and carries no stat values at all - the client derives the gem's
			// effect from the gem's own template - so this is the whole of a socket on the wire.
			//
			// ⚠️ We populated this NOWHERE until #750, which means every gemmed item silently lost its gems at world
			// entry. It was not a decode gap: the field has been in the schema the whole time.
			//
			// Measured over the 898 slot_map blocks in the corpus slice: socket indices are 0/1/2 only (up to THREE
			// sockets per item), the gem's `level` is its own Items.Level and its `hash` is 0, and the gem block is
			// an ordinary ProtoAcgItem - the same Identity() the outer item gets.
			foreach (var socket in (sockets ?? []).OrderBy(s => s.SocketIndex))
			{
				entry.SlotMaps.Add(new ProtoInventoryGameEntry.SlotMap
				{
					Slot = socket.SocketIndex,
					Item = Identity(socket.Gem, AcgHash.ForWire(socket.Gem.Id, socket.Gem.Hash))
				});
			}

			foreach (var stat in stats)
			{
				entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
				{
					Stat = stat.Stat,
					Value = stat.Value
				});
			}

			// `creator_name` (field 5) is the client's "Created by <name>" tooltip line, and it is the ONLY thing that
			// feeds it: it is a string on the entry, not a stat. It is PRESENT only on player-crafted items, so an
			// empty name must leave the field absent rather than emit "" - ProtoAcgItem's presence rules apply
			// (`ShouldSerializeCreatorName`), and a captured non-crafted entry ends at the item block with no `2a`
			// tag at all.
			if (!string.IsNullOrEmpty(creatorName))
				entry.CreatorName = creatorName;

			return entry;
		}

		// The typed form, used directly by FullCharacterRoots' generated containers. The world-entry snapshot
		// entry carries the fixed (flags, recordId, count) stat triple.
		//
		// That triple is the SNAPSHOT's shape and it is a good match for retail's: of 8,479 captured snapshot
		// entries, 100% carry StaticInstance(0x17), 95% Flags(0x00) and 93% StackSize(0x1A), and the rest of the
		// stats are sparse (Duration on 16%, ItemSource on 5 entries total). The LIVE add is a different shape -
		// see LiveAddEntry (#722).
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash, ulong flags = 1,
			string? creatorName = null, IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			return new ProtoInventoryGame.InventoryListEntry
			{
				Pos = (uint)slot,
				Item = InventoryEntry(itemId, item, hash, [new ItemStat(0, (uint)flags), new ItemStat(23, (uint)recordId), new ItemStat(26, (uint)count)], creatorName, sockets)
			};
		}

		// The item INSTANCE overload - one row in, the whole entry out, so a caller holding an ItemInstance never has
		// to spread it back into loose scalars and can't forget a piece of it. Which is the point: the crafter and the
		// SOCKETS travelled by hand before #750, and the sockets were simply never picked up.
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ItemInstance instance, ulong itemId, ulong recordId)
		{
			return ListEntry((ulong)instance.Slot, itemId, recordId, instance.Count, instance.Template, AcgHash.ForWire(instance.TemplateId, instance.Template.Hash),
				creatorName: instance.CreatorName, sockets: instance.Sockets);
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
		public static ProtoInventoryGameEntry LiveAddEntry(ulong itemId, ulong recordId, ulong count, Item item, uint hash, ulong flags = 1, string? creatorName = null,
			IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			return InventoryEntry(itemId, item, hash,
			[
				new ItemStat(0, (uint)flags),
				new ItemStat((uint)CharacterStatId.Duration, NoExpiry),
				new ItemStat(23, (uint)recordId),
				new ItemStat(26, (uint)count)
			], creatorName, sockets);
		}

		// The entry's TEMPLATE identity block, shared by the item itself and by every gem in its sockets - a gem in a
		// slot_map is an ordinary ProtoAcgItem, so there is one function for both rather than two that must agree.
		//
		// ⚠️ `level` is the item's OWN Items.Level, not 1. It used to be a `level = 1` default parameter that no caller
		// ever passed, so every item we sent claimed level 1 - wrong for 49% of them (4,170 of 8,479 captured entries
		// carry a level other than 1). It is per-TEMPLATE and it is exact: across all 8,479 captured snapshot entries
		// the wire's `level` equals that template's Items.Level, 8,479 for 8,479, zero exceptions. So there was never
		// anything to decode here - the column was already right and the emitter simply ignored it. (#725 settled the
		// value as per-template; this is the emit half.)
		//
		// ⚠️ `template2`/`key2` are emitted as COPIES of template1/key1, which is what 8,445 of 8,479 captured entries
		// carry - but the copy is OUR SIMPLIFICATION, not a wire fact. 34 top-level entries and 10 socket blocks carry
		// `template2 = template1 + 1` with a second, unrelated TEA key (key3 still equals key1), and that pair is REAL:
		// `key2` TEA-decrypts RDB item template `template2` to a valid record (`type=1000020 id=3976769
		// marker=1000000`), while cross-pairing key1 with template2 yields garbage. Both halves of the Cabochon Azurite
		// pair carry the SAME display name and differ in content (285 vs 306 bytes), so it is one item in two variants.
		//
		// The client names the whole family in its own stat table: `ACGItemTemplateID` (0x2BE) / `ACGItemTemplateID2`
		// (0x2BF) / `ACGItemTemplateID3` (0x510) beside `ACGItemSeed` (0x2BC), `ACGItemLevel` (0x2BD),
		// `ACGItemCategoryID` (0x2C0) and `ACGItemNano1..4` - i.e. a procedurally GENERATED item, composed from up to
		// three templates plus a seed and a level. `ItemSlot1ACG1`/`ItemSlot1ACG2` .. `ItemSlot4ACG1`/`ItemSlot4ACG2`
		// (0x2A3..0x2AA) say the same thing about SOCKETS: four slots, each naming its gem by an ACG id PAIR.
		//
		// Our Items row holds one template and one key, so it cannot express the pair. Tracked in Gitea #752 - do not
		// read the copy as a decoded fact.
		private static ProtoAcgItem Identity(Item item, uint hash)
		{
			var key = item.Key;
			return new ProtoAcgItem
			{
				Key1 = key,
				Key2 = key,
				Key3 = key,
				Template1 = item.Id,
				Template2 = item.Id,
				Level = item.Level,
				Hash = hash
			};
		}

		// The value retail puts in Duration on an item that does not expire: 1,497 of 1,528 captured sightings.
		public const uint NoExpiry = uint.MaxValue;

		// One (stat id, value) record of a ProtoInventoryGameEntry.stat_list. The world-entry snapshot uses a
		// fixed triple (ListEntry below); the incremental DispatchAddItem (0x2020) s2c carries a richer,
		// per-instance set captured from retail, so InventoryEntry takes the stat list as a parameter.
		public readonly record struct ItemStat(uint Stat, uint Value);
	}
}
