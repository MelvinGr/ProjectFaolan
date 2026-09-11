using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     Builds a FullChar inventory container-entry (<c>ProtoInventoryGame.items</c>, field 4) from a DB Item
	///     template, using the typed protobuf schema recovered from the client exe (see
	///     Faolan.Core/Protobuf/aoc-client-protos.proto).
	/// </summary>
	/// <remarks>
	///     The layout is the client's own:
	///     <code>
	///     entry  = InventoryListEntry { pos = slot, item = ProtoInventoryGameEntry }
	///     item   = ProtoInventoryGameEntry { item_id, stat_list[ (0,flags),(23,recordId),(26,count) ], item_data }
	///     data   = ProtoAcgItem { key1=key2=key3 = ProtoDecryptionKey(Data1..4), template1=template2 = Item.Id,
	///                             level, hash = hash }
	///     </code>
	///     The per-instance state (itemId GUID, inventory record id, stack count, slot) is kept; the template identity -
	///     the Data1..4 quad, which is the item's <c>ProtoDecryptionKey</c>, plus the hash - comes from the DB Item.
	///     Proven byte-exact against the captured "Slave Manacles" entry (<c>SnapshotItemsTests</c>); the typed schema is
	///     proven to re-serialize the wire byte-for-byte (<c>AocProtocolDecodeTests</c>).
	/// </remarks>
	public static class SnapshotItems
	{
		// What to send as stat 0 when `Item.Flags` is null - when no RDB template was readable for the item, so there is no
		// measured word to send. 1 is the bit 4,676 of the 4,741 readable templates carry, so it is the least-wrong
		// fallback; it is not a decoded default, and an item that reaches it renders as unbound whatever its template would
		// have said.

		// The `Flags` word to send for one item: the template's own word (`Item.WireFlags`, which resolves the
		// unreadable-template case), plus bit 26 when this particular copy has bound to its owner. Both halves in one place,
		// because they are one wire field - the template says "every copy of me is born bound" (bind on pickup) and the
		// instance says "this copy bound when it was worn".
		private static uint WireFlags(Item item, bool bound)
		{
			return item.WireFlags | (bound
				? Item.BoundOnTheWire
				: 0u);
		}

		// Builds the bare per-item entry (ProtoInventoryGameEntry, i.e. InventoryListEntry.item without the Pos wrapper)
		// from a DB template plus per-instance state and an explicit stat list. This is the form
		// GameCharInterface.DispatchAddItem carries as i_cEntry - the live, mid-session counterpart to the world-entry
		// snapshot; there the slot rides separately as i_nItemPos, so there is no Pos wrapper.
		public static ProtoInventoryGameEntry InventoryEntry(ulong itemId, Item item, uint hash, IEnumerable<ItemStat> stats, string? creatorName = null,
			IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			var entry = new ProtoInventoryGameEntry
			{
				ItemId = itemId,
				ItemData = Identity(item, hash)
			};

			// The gems socketed into this item. `slot_map` (field 4) is the persistent form of a socket, and it is how a gemmed
			// item survives a relog: the login snapshot restates every socketed item's full gem list. Each entry names the gem by
			// template and carries no stat values at all - the client derives the gem's effect from the gem's own template - so
			// this is the whole of a socket on the wire.
			//
			// Measured over the 898 slot_map blocks in the corpus slice: socket indices are 0/1/2 only (up to three sockets per
			// item), the gem's `level` is its own Items.Level and its `hash` is 0, and the gem block is an ordinary
			// ProtoAcgItem - the same Identity() the outer item gets.
			foreach (var socket in (sockets ?? []).OrderBy(s => s.SocketIndex))
			{
				entry.SlotMaps.Add(new ProtoInventoryGameEntry.SlotMap
				{
					Slot = socket.SocketIndex,
					Item = Identity(socket.Gem, AcgHash.ForWire(socket.Gem.Id, socket.Gem.Hash))
				});
			}

			// A socket is stated twice on the wire, and both halves have to be sent. Besides slot_map, the entry's stat_list
			// carries the gem as the pair `ItemSlotNACG1`/`ItemSlotNACG2` - the client's own stat names for a socket slot's two
			// ACG ids, which are exactly the gem's template1/template2 (see Identity).
			//
			// Measured over all 898 slot_map blocks in the corpus slice, the two forms agree 898/898, with no disagreements and
			// no cases of one present without the other: retail sends both, every time.
			//
			// The stat ids are 0x2A3 + 2N / 0x2A4 + 2N for socket N, and the table names four slots (ItemSlot1..ItemSlot4), so
			// socket index 3 has a home even though no captured item uses it.
			//
			// ACG1/ACG2 are the gem's template1/template2, so they get exactly what Identity() puts in the slot_map block, which
			// is what keeps the socket's two wire forms agreeing. That is the real pair when the gem has a second endpoint
			// (`Cabochon Azurite` 3976768/3976769 is one) rather than the template repeated.
			var socketStats = (sockets ?? [])
				.Where(socket => socket.SocketIndex < MaxSockets)
				.OrderBy(socket => socket.SocketIndex)
				.SelectMany(socket => new[]
				{
					new ItemStat(ItemSlot1Acg1 + (2 * socket.SocketIndex), socket.Gem.Id),
					new ItemStat(ItemSlot1Acg1 + (2 * socket.SocketIndex) + 1, socket.Gem.WireTemplate2)
				});

			// Ordered ascending by stat id, like every other stat list here: the order is load-bearing for byte-exactness, and
			// the socket ids (0x2A3 and up) sort after every bookkeeping stat the callers pass.
			foreach (var stat in stats.Concat(socketStats).OrderBy(s => s.Stat))
			{
				entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
				{
					Stat = stat.Stat,
					Value = stat.Value
				});
			}

			// `creator_name` (field 5) is the client's "Created by <name>" tooltip line, and it is the only thing that feeds it:
			// a string on the entry, not a stat. It is present only on player-crafted items, so an empty name must leave the
			// field absent rather than emit "" - ProtoAcgItem's presence rules apply (`ShouldSerializeCreatorName`), and a
			// captured non-crafted entry ends at the item block with no `2a` tag at all.
			if (!string.IsNullOrEmpty(creatorName))
				entry.CreatorName = creatorName;

			return entry;
		}

		// The typed form, used directly by FullCharacterRoots' generated containers. The world-entry snapshot entry carries
		// the fixed (flags, recordId, count) stat triple.
		//
		// `flags` is the template's own `Flags` word (`Item.Flags`, RDB property field 0), not a caller's choice: 174 of the
		// 179 captured DispatchAddItem arrivals carry exactly their template's Flags (`--report item-binding`).
		//
		// Bit 26 is bound, so sending the real word is also what makes "Character Bound : Binds when Picked Up" render for
		// the 1,267 templates that mint bound. The per-instance half - an item that binds when equipped, which the server
		// marks by re-sending stat 0 with bit 26 on the equipped cell - is not stored yet.
		//
		// A null `Item.Flags` means no template was readable for that item (61 of the shipped rows), not that the template
		// said zero: 65 templates genuinely carry 0, which is why the column is nullable. `Item.WireFlags` is where that
		// fallback lives.
		//
		// That triple is the snapshot's shape and is a good match for retail's: of 8,479 captured snapshot entries, 100%
		// carry StaticInstance(0x17), 95% Flags(0x00) and 93% StackSize(0x1A), and the rest of the stats are sparse
		// (Duration on 16%, ItemSource on 5 entries total). The live add is a different shape - see LiveAddEntry.
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash,
			string? creatorName = null, IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			return ListEntry(slot, itemId, recordId, count, item, hash, false, creatorName, sockets);
		}

		// The full form. `bound` is the per-instance half and is why the loose overload above cannot answer it: a caller
		// holding no instance (a loot bag, a vendor's wares, a test) is describing an item nobody owns, which is never bound,
		// so the two overloads differ by what they can know rather than by convenience.
		private static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash, bool bound,
			string? creatorName, IEnumerable<ItemInstanceSocket>? sockets)
		{
			return new ProtoInventoryGame.InventoryListEntry
			{
				Pos = (uint)slot,
				Item = InventoryEntry(itemId, item, hash, [new ItemStat(0, WireFlags(item, bound)), new ItemStat(23, (uint)recordId), new ItemStat(26, (uint)count)], creatorName, sockets)
			};
		}

		// The item instance overload - one row in, the whole entry out, so a caller holding an ItemInstance never has to
		// spread it back into loose scalars and cannot forget a piece of it, the crafter and the sockets included.
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ItemInstance instance, ulong itemId, ulong recordId)
		{
			return ListEntry((ulong)instance.Slot, itemId, recordId, instance.Count, instance.Template, AcgHash.ForWire(instance.TemplateId, instance.Template.Hash),
				instance.Bound, instance.CreatorName, instance.Sockets);
		}

		// The live DispatchAddItem form - a different stat list from the snapshot's, which is why it is its own factory
		// rather than a flag on ListEntry. Measured over the 165 captured live adds:
		//
		//   Flags (0x00)          165/165        StackSize (0x1A)       84/165
		//   StaticInstance (0x17) 165/165        Duration  (0x08)      164/165
		//                                        ItemSource (0x2E)     164/165
		//
		// On the snapshot side Duration and ItemSource are sparse (16% and 5 of 8,479 entries), so the snapshot triple is
		// right there and wrong here.
		//
		// The stat list is ordered ascending by stat id, and the order is load-bearing for byte-exactness -
		// DispatchAddItemTests asserts the captured entry rebuilds byte-for-byte and its stats run 0x00, 0x08, 0x17, 0x2E.
		// Keep any new stat in id order.
		//
		// `Duration` takes only two values across all 1,528 captured sightings - 0xFFFFFFFF on 1,497 and 0 on 31, with no
		// template carrying both. UINT_MAX in a field named Duration reads as "does not expire", the same idiom as a float
		// defaulting to FLT_MAX to mean "no cap".
		//
		// `StackSize` is sent unconditionally here, which is not what retail does - it rides 84 of 165 adds. The obvious
		// rule ("only when the stack is bigger than one") is refuted: 82 of those 84 carry the value 1, so whatever selects
		// it is not the count. Sending it always is the safe side of that unknown, since the value sent is correct either
		// way.
		//
		// `ItemSource` is deliberately not sent. It is a provenance code with 8 observed values (1,2,5,7,13,15,16,19) and
		// guessing is worse than omitting: a wrong provenance is a false claim, a missing one is a gap. Decoding it needs
		// each add correlated with the c2s action that produced it; grouping by capture is too coarse, since values 2, 7 and
		// 15 all appear within the same sessions.
		public static ProtoInventoryGameEntry LiveAddEntry(ItemInstance instance, ulong itemId, ulong recordId)
		{
			return LiveAddEntry(itemId, recordId, instance.Count, instance.Template, AcgHash.ForWire(instance.TemplateId, instance.Template.Hash), instance.Bound,
				instance.CreatorName, instance.Sockets);
		}

		public static ProtoInventoryGameEntry LiveAddEntry(ulong itemId, ulong recordId, ulong count, Item item, uint hash, bool bound = false, string? creatorName = null,
			IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			return InventoryEntry(itemId, item, hash,
			[
				new ItemStat(0, WireFlags(item, bound)),
				new ItemStat((uint)CharacterStatId.Duration, NoExpiry),
				new ItemStat(23, (uint)recordId),
				new ItemStat(26, (uint)count)
			], creatorName, sockets);
		}

		// The entry's template identity block, shared by the item itself and by every gem in its sockets - a gem in a
		// slot_map is an ordinary ProtoAcgItem, so there is one function for both rather than two that must agree.
		//
		// `level` is the item's own Items.Level, not 1: it is per-template and exact, since across all 8,479 captured
		// snapshot entries the wire's `level` equals that template's Items.Level, 8,479 for 8,479, with no exceptions.
		//
		// `template2`/`key2` are a real second item template and the key that decrypts it, not a copy of the first: `key2`
		// TEA-decrypts RDB item template `template2` to a valid record (`type=1000020 id=3976769 marker=1000000`) while
		// cross-pairing key1 with template2 yields garbage, so the binding is `keyN` to `templateN` exactly. Both halves of
		// a pair carry the same display name and differ only in their level fields (Cabochon Azurite: 285 vs 306 bytes,
		// fields 54/203/204 reading 50 vs 80) - one item in two variants, whose templates are the low and high endpoints of
		// a level range with the entry's own `level` saying where between them this copy sits. That is what
		// `AcgItemData_t::GetInterpolatedItem` is named for and what it does.
		//
		// Most items have no second endpoint and genuinely repeat the first - 341 of the 344 captured pairs - which is what
		// `Item.WireTemplate2`/`WireKey2` return when `TemplateId2` is null. The three that carry one send it out of
		// `Item.TemplateId2` plus `Item.PairKey`, and the key is not optional: a second template without the key that
		// decrypts it would leave the client holding a template it cannot read.
		//
		// The client names the whole family in its own stat table: `ACGItemTemplateID` (0x2BE) / `ACGItemTemplateID2`
		// (0x2BF) / `ACGItemTemplateID3` (0x510) beside `ACGItemSeed` (0x2BC), `ACGItemLevel` (0x2BD),
		// `ACGItemCategoryID` (0x2C0) and `ACGItemNano1..4` - a procedurally generated item, composed from up to three
		// templates plus a seed and a level. `ItemSlot1ACG1`/`ItemSlot1ACG2` .. `ItemSlot4ACG1`/`ItemSlot4ACG2`
		// (0x2A3..0x2AA) say the same thing about sockets: four slots, each naming its gem by an ACG id pair.
		private static ProtoAcgItem Identity(Item item, uint hash)
		{
			// template1/template2 are the low and high endpoints of a level range and the entry's own `level` says where
			// between them this copy sits, which is what AcgItemData_t::GetInterpolatedItem is named for. Most items have no
			// second endpoint and repeat the first, which is what WireTemplate2/WireKey2 return; the three that do carry one
			// send it with the key that decrypts it.
			//
			// key3 is key1 on every one of the 13,368 captured sightings - the report counts per sighting precisely so a later
			// copy of an already-seen pair cannot hide a difference - and template3 is 0 on all of them. The third slot is
			// therefore unused rather than undecoded, and `Item` needs no third key, since a Key3 column would hold a
			// byte-identical duplicate of `Key`. A non-zero template3 would invert that, and would need its own key for the same
			// reason `PairKey` exists: templateN is encrypted under keyN.
			//
			// key2 differs from key1 on 65 of those sightings, which is exactly the 36 + 28 + 1 sightings of the three differing
			// pairs - so the key differs precisely when the template does, with no third case.
			return new ProtoAcgItem
			{
				Key1 = item.Key,
				Key2 = item.WireKey2,
				Key3 = item.Key,
				Template1 = item.Id,
				Template2 = item.WireTemplate2,
				Level = item.Level,
				Hash = hash
			};
		}

		// The value retail puts in Duration on an item that does not expire: 1,497 of 1,528 captured sightings.
		public const uint NoExpiry = uint.MaxValue;

		// `ItemSlot1ACG1` (0x2A3) - the first of the eight socket stat ids, which run in (ACG1, ACG2) pairs for four slots
		// up to `ItemSlot4ACG2` (0x2AA). Named by the client's own stat table.
		private const uint ItemSlot1Acg1 = 0x2A3;

		// Four socket slots, per that table. The corpus only ever shows indices 0/1/2, so three sockets is a capture limit
		// and four is the game's; a socket beyond the fourth has no stat id to go in and is dropped rather than scribbling
		// over ItemSlot4ACG2's neighbour.
		private const uint MaxSockets = 4;

		// One (stat id, value) record of a ProtoInventoryGameEntry.stat_list. The world-entry snapshot uses a fixed triple
		// (ListEntry below); the incremental DispatchAddItem s2c carries a richer, per-instance set captured from retail, so
		// InventoryEntry takes the stat list as a parameter.
		public readonly record struct ItemStat(uint Stat, uint Value);
	}
}
