using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	// Builds a 0x737A6DF9 inventory container-entry (ProtoInventoryGame.items, field 4) from a DB Item template,
	// using the typed protobuf schema recovered from the client exe (Faolan.Core/Network/Protobuf, see
	// docs/aoc-client-protos.proto). The layout is the client's own:
	//   entry  = InventoryListEntry { pos = slot, item = ProtoInventoryGameEntry }
	//   item   = ProtoInventoryGameEntry { item_id, stat_list[ (0,flags),(23,recordId),(26,count) ], item_data }
	//   data   = ProtoAcgItem { key1=key2=key3 = ProtoDecryptionKey(Data1..4), template1=template2 = Item.Id,
	//                           level, hash = archetype }
	// The per-instance state (itemId GUID, inventory record id, stack count, slot) is kept; the template identity -
	// the Data1..4 quad (which is the item's ProtoDecryptionKey) plus the archetype hash - comes from the DB Item.
	// Proven byte-exact against the captured "Slave Manacles" entry (SnapshotItemBuilderTests); the typed schema is
	// proven to re-serialize the wire byte-for-byte (AocProtocolDecodeTests).
	public static class SnapshotItemBuilder
	{
		public static ProtoField BuildEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint archetype, ulong flags = 1, ulong f7 = 1)
		{
			var key = new ProtoDecryptionKey { Key1 = item.Data1, Key2 = item.Data2, Key3 = item.Data3, Key4 = item.Data4 };

			var entry = new ProtoInventoryGame.InventoryListEntry
			{
				Pos = (uint)slot,
				Item = new ProtoInventoryGameEntry
				{
					ItemId = itemId,
					StatLists =
					{
						new ProtoInventoryGameEntry.StatValue { Stat = 0, Value = (uint)flags },
						new ProtoInventoryGameEntry.StatValue { Stat = 23, Value = (uint)recordId },
						new ProtoInventoryGameEntry.StatValue { Stat = 26, Value = (uint)count }
					},
					ItemData = new ProtoAcgItem
					{
						Key1 = key,
						Key2 = key,
						Key3 = key,
						Template1 = item.Id,
						Template2 = item.Id,
						Level = (uint)f7,
						Hash = archetype
					}
				}
			};

			using var ms = new MemoryStream();
			Serializer.Serialize(ms, entry);

			// Wrap the serialized entry as the container's field-4 message (the live path swaps this into the parsed
			// ProtoMessage snapshot tree, which stays the lossless carrier for the rest of the packet).
			return new ProtoField
			{
				FieldNumber = 4,
				WireType = 2,
				MessageValue = ProtoMessage.Parse(ms.ToArray())
			};
		}

		// Convenience overload: an item is buildable iff it has an archetype (DB column, harvested from captures).
		// Returns false otherwise so the caller can fall back to the verbatim capture for that item.
		public static bool TryBuildEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, out ProtoField entry, ulong flags = 1, ulong f7 = 1)
		{
			entry = null;
			if (item == null || item.Archetype == 0)
				return false;

			entry = BuildEntry(slot, itemId, recordId, count, item, item.Archetype, flags, f7);
			return true;
		}
	}
}
