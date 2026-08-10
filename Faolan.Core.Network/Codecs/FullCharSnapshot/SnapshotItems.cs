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
		// What to send as stat 0 when `Item.Flags` is NULL - i.e. when no RDB template was readable for the item, so
		// there is no measured word to send. `1` is the value every item shipped before #756 and the one bit 4,676 of
		// the 4,741 readable templates carry, so it is the least-wrong fallback; it is NOT a decoded default, and an
		// item that reaches it renders as unbound whatever its template would have said.
		// The `Flags` word to send for one item: the TEMPLATE's own word (`Item.WireFlags`, which resolves the
		// unreadable-template case), plus bit 26 when this particular copy has bound to its owner (#756). Both halves
		// in one place, because they are one wire field - the template says "every copy of me is born bound" (bind on
		// pickup) and the instance says "this copy bound when it was worn".
		private static uint WireFlags(Item item, bool bound)
		{
			return item.WireFlags | (bound
				? Item.BoundOnTheWire
				: 0u);
		}

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

			// ⚠️ A socket is stated TWICE on the wire, and both halves have to be sent. Besides slot_map, the entry's
			// stat_list carries the gem as the PAIR `ItemSlotNACG1`/`ItemSlotNACG2` - the client's own stat names for a
			// socket slot's two ACG ids, which are exactly the gem's template1/template2 (see Identity).
			//
			// Measured over all 898 slot_map blocks in the corpus slice: the two forms AGREE 898/898, with zero
			// disagreements and zero cases of one present without the other. So they are not alternatives - retail sends
			// both, every time.
			//
			// The stat ids are 0x2A3 + 2N / 0x2A4 + 2N for socket N, and the table names FOUR slots
			// (ItemSlot1..ItemSlot4), so socket index 3 has a home even though no captured item uses it.
			//
			// ACG1/ACG2 ARE the gem's template1/template2, so they get exactly what Identity() puts in the slot_map
			// block - which is what keeps the socket's two wire forms AGREEING, the invariant the corpus pins 898/898.
			// Since #752 that is the real pair when the gem has a second endpoint (`Cabochon Azurite` 3976768/3976769
			// is one) rather than the template repeated.
			var socketStats = (sockets ?? [])
				.Where(socket => socket.SocketIndex < MaxSockets)
				.OrderBy(socket => socket.SocketIndex)
				.SelectMany(socket => new[]
				{
					new ItemStat(ItemSlot1Acg1 + (2 * socket.SocketIndex), socket.Gem.Id),
					new ItemStat(ItemSlot1Acg1 + (2 * socket.SocketIndex) + 1, socket.Gem.WireTemplate2)
				});

			// ⚠️ ORDERED ascending by stat id, like every other stat list here - the order is load-bearing for
			// byte-exactness, and the socket ids (0x2A3+) sort after every bookkeeping stat the callers pass.
			foreach (var stat in stats.Concat(socketStats).OrderBy(s => s.Stat))
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
		// ⭐ `flags` is the TEMPLATE's own `Flags` word (`Item.Flags`, RDB property field 0), not a caller's choice.
		// It used to be a `flags = 1` default parameter that no caller ever passed - the third time that exact shape
		// has shipped a wrong constant here, after `level = 1` on this same entry and the dead `level` on
		// GrantItemToBackpack. Retail's value is measurable and it is this column: 174 of the 179 captured
		// DispatchAddItem arrivals carry EXACTLY their template's Flags (#756, `--report item-binding`).
		//
		// ⚠️ Bit 26 is BOUND, so sending the real word is also what makes "Character Bound : Binds when Picked Up"
		// render for the 1,267 templates that mint bound. The per-INSTANCE half - an item that binds when EQUIPPED,
		// which the server marks by re-sending stat 0 with bit 26 on the equipped cell - is not stored yet and will
		// land with the equip behaviour that needs it.
		//
		// A NULL `Item.Flags` means no template was readable for that item (61 of the shipped rows), NOT that the
		// template said zero - 65 templates genuinely carry 0, which is why the column is nullable. `Item.WireFlags`
		// is where that fallback lives.
		//
		// That triple is the SNAPSHOT's shape and it is a good match for retail's: of 8,479 captured snapshot
		// entries, 100% carry StaticInstance(0x17), 95% Flags(0x00) and 93% StackSize(0x1A), and the rest of the
		// stats are sparse (Duration on 16%, ItemSource on 5 entries total). The LIVE add is a different shape -
		// see LiveAddEntry (#722).
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash,
			string? creatorName = null, IEnumerable<ItemInstanceSocket>? sockets = null)
		{
			return ListEntry(slot, itemId, recordId, count, item, hash, false, creatorName, sockets);
		}

		// The full form. `bound` is the per-INSTANCE half and is why the loose overload above cannot answer it: a
		// caller holding no instance (a loot bag, a vendor's wares, a test) is describing an item nobody owns, which
		// is never bound - so the two overloads differ by what they can KNOW, not by convenience.
		private static ProtoInventoryGame.InventoryListEntry ListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash, bool bound,
			string? creatorName, IEnumerable<ItemInstanceSocket>? sockets)
		{
			return new ProtoInventoryGame.InventoryListEntry
			{
				Pos = (uint)slot,
				Item = InventoryEntry(itemId, item, hash, [new ItemStat(0, WireFlags(item, bound)), new ItemStat(23, (uint)recordId), new ItemStat(26, (uint)count)], creatorName, sockets)
			};
		}

		// The item INSTANCE overload - one row in, the whole entry out, so a caller holding an ItemInstance never has
		// to spread it back into loose scalars and can't forget a piece of it. Which is the point: the crafter and the
		// SOCKETS travelled by hand before #750, and the sockets were simply never picked up.
		public static ProtoInventoryGame.InventoryListEntry ListEntry(ItemInstance instance, ulong itemId, ulong recordId)
		{
			return ListEntry((ulong)instance.Slot, itemId, recordId, instance.Count, instance.Template, AcgHash.ForWire(instance.TemplateId, instance.Template.Hash),
				instance.Bound, instance.CreatorName, instance.Sockets);
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
				new ItemStat((uint)CharacterStatId.TimeExist, NoExpiry),
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
		// ⚠️ `template2`/`key2` are a REAL SECOND item template and the key that decrypts it, NOT a copy of the first
		// (#752). `key2` TEA-decrypts RDB item template `template2` to a valid record (`type=1000020 id=3976769
		// marker=1000000`) while cross-pairing key1 with template2 yields garbage, so the binding is `keyN` <->
		// `templateN` exactly. Both halves of a pair carry the SAME display name and differ ONLY in their level fields
		// (Cabochon Azurite: 285 vs 306 bytes, fields 54/203/204 reading 50 vs 80) - one item in two variants, whose
		// templates are the LOW and HIGH endpoints of a level range with the entry's own `level` saying where between
		// them this copy sits. That is what `AcgItemData_t::GetInterpolatedItem` is named for and what it does.
		//
		// Most items have no second endpoint and genuinely repeat the first - 341 of the 344 captured pairs - which is
		// what `Item.WireTemplate2`/`WireKey2` return when `TemplateId2` is null. The three that carry one send it out
		// of `Item.TemplateId2` + `Item.PairKey`, and the key is not optional: a second template without the key that
		// decrypts it would leave the client holding a template it cannot read.
		//
		// The client names the whole family in its own stat table: `ACGItemTemplateID` (0x2BE) / `ACGItemTemplateID2`
		// (0x2BF) / `ACGItemTemplateID3` (0x510) beside `ACGItemSeed` (0x2BC), `ACGItemLevel` (0x2BD),
		// `ACGItemCategoryID` (0x2C0) and `ACGItemNano1..4` - i.e. a procedurally GENERATED item, composed from up to
		// three templates plus a seed and a level. `ItemSlot1ACG1`/`ItemSlot1ACG2` .. `ItemSlot4ACG1`/`ItemSlot4ACG2`
		// (0x2A3..0x2AA) say the same thing about SOCKETS: four slots, each naming its gem by an ACG id PAIR.
		private static ProtoAcgItem Identity(Item item, uint hash)
		{
			// template1/template2 are the LOW and HIGH endpoints of a level range and the entry's own `level` says
			// where between them this copy sits - which is what AcgItemData_t::GetInterpolatedItem is named for
			// (#752). Most items have no second endpoint and repeat the first, which is what WireTemplate2/WireKey2
			// return; the three that do carry one now send it, with the key that decrypts it.
			//
			// ⚠️ key3 is key1 on every one of the 13,368 captured SIGHTINGS (not merely the 344 distinct pairs - the
			// report counts per sighting precisely so a later copy of an already-seen pair cannot hide a difference),
			// and template3 is 0 on all of them. So the third slot is UNUSED rather than undecoded, and `Item` needs
			// no third key: a Key3 column would hold a byte-identical duplicate of `Key`. If a non-zero template3 is
			// ever captured that inverts, for the same reason `PairKey` exists - templateN is encrypted under keyN,
			// so a third template would need its own key too.
			//
			// key2 differs from key1 on 65 of those sightings, which is exactly the 36 + 28 + 1 sightings of the three
			// differing pairs - so the key differs precisely when the template does, with no third case.
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

		// `ItemSlot1ACG1` (0x2A3) - the first of the eight socket stat ids, which run in (ACG1, ACG2) pairs for four
		// slots up to `ItemSlot4ACG2` (0x2AA). Named by the client's own stat table.
		private const uint ItemSlot1Acg1 = 0x2A3;

		// Four socket slots, per that table. The corpus only ever shows indices 0/1/2, so three sockets is a CAPTURE
		// limit and four is the game's; a socket beyond the fourth has no stat id to go in and is dropped rather than
		// scribbling over ItemSlot4ACG2's neighbour.
		private const uint MaxSockets = 4;

		// One (stat id, value) record of a ProtoInventoryGameEntry.stat_list. The world-entry snapshot uses a
		// fixed triple (ListEntry below); the incremental DispatchAddItem (0x2020) s2c carries a richer,
		// per-instance set captured from retail, so InventoryEntry takes the stat list as a parameter.
		public readonly record struct ItemStat(uint Stat, uint Value);
	}
}
