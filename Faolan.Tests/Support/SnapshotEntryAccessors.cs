using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;

namespace Faolan.Tests.Support
{
	// Convenience readers over a snapshot inventory entry, used only by the snapshot tests. These lived on
	// FullCharSnapshotCodec but had ZERO production callers (confirmed with the Roslyn language server, not grep -
	// comment mentions make grep unreliable here), so they were test scaffolding sitting in the protocol layer.
	//
	// They are one-liners over already-typed protobuf properties, so moving them costs nothing: no production model
	// is duplicated. That is the line to hold when moving "test-only" code out of a codec - FullCharSnapshotCodec's
	// ParseInventoryPacketBody is ALSO test-only by reference count, but it encodes production's prefix/root/tails
	// SPLIT, so moving it would leave the round-trip tests asserting their own copy of the model instead of the
	// server's. It deliberately stays put. See [[self_referential_golden_tests]].
	public static class SnapshotEntryAccessors
	{
		public static ProtoInventoryGame.InventoryListEntry EntryBySlot(ProtoInventoryGame container, ulong slot)
		{
			return Entries(container).FirstOrDefault(e => e.Pos == slot);
		}

		public static uint? EntryCount(ProtoInventoryGame.InventoryListEntry entry)
		{
			return Stat(entry, 26); // stat 26 = the stack count
		}

		public static ulong? EntryItemId(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Item?.ItemId;
		}

		public static uint? EntryRecordId(ProtoInventoryGame.InventoryListEntry entry)
		{
			return Stat(entry, 23); // stat 23 = the item's local inventory record id
		}

		public static ulong EntrySlot(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Pos;
		}

		private static IEnumerable<ProtoInventoryGame.InventoryListEntry> Entries(ProtoInventoryGame container)
		{
			return FullCharSnapshotCodec.GetContainerEntries(container);
		}

		private static uint? Stat(ProtoInventoryGame.InventoryListEntry entry, uint stat)
		{
			return entry.Item?.StatLists.FirstOrDefault(s => s.Stat == stat)?.Value;
		}
	}
}
