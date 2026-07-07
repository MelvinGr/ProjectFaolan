using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Protobuf;
using System.Collections.Generic;

namespace Faolan.Core.Network.GameServer.FullCharSnapshot
{
    // Builds a 0x737A6DF9 inventory container-entry (ProtoInventoryGame.items, field 4) from a DB Item template,
    // using the typed protobuf schema recovered from the client exe (Faolan.Core/Network/Protobuf, see
    // docs/aoc-client-protos.proto). The layout is the client's own:
    //   entry  = InventoryListEntry { pos = slot, item = ProtoInventoryGameEntry }
    //   item   = ProtoInventoryGameEntry { item_id, stat_list[ (0,flags),(23,recordId),(26,count) ], item_data }
    //   data   = ProtoAcgItem { key1=key2=key3 = ProtoDecryptionKey(Data1..4), template1=template2 = Item.Id,
    //                           level, hash = hash }
    // The per-instance state (itemId GUID, inventory record id, stack count, slot) is kept; the template identity -
    // the Data1..4 quad (which is the item's ProtoDecryptionKey) plus the hash hash - comes from the DB Item.
    // Proven byte-exact against the captured "Slave Manacles" entry (SnapshotItemBuilderTests); the typed schema is
    // proven to re-serialize the wire byte-for-byte (AocProtocolDecodeTests).
    public static class SnapshotItemBuilder
    {
        // Builds the bare per-item entry (ProtoInventoryGameEntry, i.e. InventoryListEntry.item without the Pos
        // wrapper) from a DB template + per-instance state and an explicit stat list. This is the form
        // GameCharInterface.DispatchAddItem (s2c 0x2020) carries as i_cEntry - the live, mid-session counterpart
        // to the world-entry snapshot; there the slot rides separately as i_nItemPos, so no Pos wrapper.
        public static ProtoInventoryGameEntry BuildInventoryEntry(ulong itemId, Item item, uint hash, IEnumerable<ItemStat> stats, ulong level = 1)
        {
            var key = new ProtoDecryptionKey
            {
                Key1 = item.Data1,
                Key2 = item.Data2,
                Key3 = item.Data3,
                Key4 = item.Data4
            };

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

        // The typed form, used directly by FullCharSnapshotBuilder's generated containers. The world-entry snapshot
        // entry carries the fixed (flags, recordId, count) stat triple.
        public static ProtoInventoryGame.InventoryListEntry BuildListEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint hash, ulong flags = 1, ulong f7 = 1)
        {
            return new ProtoInventoryGame.InventoryListEntry
            {
                Pos = (uint)slot,
                Item = BuildInventoryEntry(itemId,
                    item,
                    hash,
                    [
                        new ItemStat(0, (uint)flags),
                        new ItemStat(23, (uint)recordId),
                        new ItemStat(26, (uint)count)
                    ],
                    f7)
            };
        }

        // Convenience overload: an item is buildable iff it has an hash (DB column, harvested from captures).
        // Returns false otherwise so the caller can fall back to the verbatim capture for that item.
        public static bool TryBuildEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, out ProtoInventoryGame.InventoryListEntry entry, ulong flags = 1, ulong f7 = 1)
        {
            entry = null;
            if (item == null || item.Hash == 0)
                return false;

            entry = BuildListEntry(slot, itemId, recordId, count, item, item.Hash, flags, f7);
            return true;
        }

        // One (stat id, value) record of a ProtoInventoryGameEntry.stat_list. The world-entry snapshot uses a
        // fixed triple (BuildListEntry below); the incremental DispatchAddItem (0x2020) s2c carries a richer,
        // per-instance set captured from retail, so BuildInventoryEntry takes the stat list as a parameter.
        public readonly record struct ItemStat(uint Stat, uint Value);
    }
}
