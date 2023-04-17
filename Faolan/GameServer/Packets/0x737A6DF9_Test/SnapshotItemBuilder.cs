using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets;

namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	// Builds a 0x737A6DF9 inventory container-entry (the field-4 message inside a container) from a DB Item
	// template, so an inventory can be assembled from `CharacterEquipment`/`player_items` joined to the imported
	// `p_items` (Items) - instead of patching a captured snapshot. The mapping was proven byte-exact against the
	// captured "Slave Manacles" entry (see SnapshotItemBuilderTests / docs/spawn-dissection-plan.md Phase 5):
	//
	//   entry.f1                = slot
	//   entry.f2 (details).f1   = itemId           (per-instance GUID, NOT from the template)
	//   details.f2 {f1=0,f2=v}  = flags property
	//   details.f2 {f1=23,f2=v} = inventory record id (per-instance)
	//   details.f2 {f1=26,f2=v} = stack count
	//   details.f3 (tail).f1/f2/f3 = three identical identity payloads {f1..f4 fixed32 = Item.Data1..Data4}
	//   tail.f4 = tail.f5       = Item.Id (the item template id)
	//   tail.f7                 = 1
	//   tail.f8                 = archetype  (the ONE field not in p_items - must be supplied/harvested)
	public static class SnapshotItemBuilder
	{
		public static ProtoField BuildEntry(ulong slot, ulong itemId, ulong recordId, ulong count, Item item, uint archetype, ulong flags = 1, ulong f7 = 1)
		{
			var details = new ProtoMessage();
			details.Fields.Add(Varint(1, itemId));
			details.Fields.Add(Msg(2, Property(0, flags)));
			details.Fields.Add(Msg(2, Property(23, recordId)));
			details.Fields.Add(Msg(2, Property(26, count)));
			details.Fields.Add(Msg(3, BuildTail(item, archetype, f7)));

			var entry = new ProtoMessage();
			entry.Fields.Add(Varint(1, slot));
			entry.Fields.Add(Msg(2, details));

			return new ProtoField
			{
				FieldNumber = 4,
				WireType = 2,
				MessageValue = entry
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

		private static ProtoMessage BuildTail(Item item, uint archetype, ulong f7)
		{
			var tail = new ProtoMessage();
			tail.Fields.Add(Msg(1, IdentityPayload(item)));
			tail.Fields.Add(Msg(2, IdentityPayload(item)));
			tail.Fields.Add(Msg(3, IdentityPayload(item)));
			tail.Fields.Add(Fixed32(4, item.Id));
			tail.Fields.Add(Fixed32(5, item.Id));
			tail.Fields.Add(Varint(7, f7));
			tail.Fields.Add(Fixed32(8, archetype));
			return tail;
		}

		private static ProtoField Fixed32(int field, uint value)
		{
			return new ProtoField
			{
				FieldNumber = field,
				WireType = 5,
				Fixed32Value = value
			};
		}

		private static ProtoMessage IdentityPayload(Item item)
		{
			var payload = new ProtoMessage();
			payload.Fields.Add(Fixed32(1, item.Data1));
			payload.Fields.Add(Fixed32(2, item.Data2));
			payload.Fields.Add(Fixed32(3, item.Data3));
			payload.Fields.Add(Fixed32(4, item.Data4));
			return payload;
		}

		private static ProtoField Msg(int field, ProtoMessage value)
		{
			return new ProtoField
			{
				FieldNumber = field,
				WireType = 2,
				MessageValue = value
			};
		}

		private static ProtoMessage Property(ulong kind, ulong value)
		{
			var m = new ProtoMessage();
			m.Fields.Add(Varint(1, kind));
			m.Fields.Add(Varint(2, value));
			return m;
		}

		private static ProtoField Varint(int field, ulong value)
		{
			return new ProtoField
			{
				FieldNumber = field,
				WireType = 0,
				VarintValue = value
			};
		}
	}
}
