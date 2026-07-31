using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

namespace Faolan.Core.Network.Codecs.World
{
	// Pure wire-body transforms extracted from WorldBroadcaster (they take captured/DB inputs, no world state). The
	// broadcaster keeps the interest/send orchestration; these are the byte construction it forwards.
	// (The QuestTextRef -> on-wire record mapping moved to QuestTextTokens.Record, which is where the other three
	// copies of it lived - #530 wave 5.)
	public static class WorldBroadcastBodies
	{
		// Injects THIS drop's live contents (cash + rolled items) into an already-from-scratch-built loot-bag record
		// (WorldObjectCodec + LootContainerTemplate build the identity + kd-tree + a scaffold ProtoLootBagIIR): keeps
		// the constructed envelope and swaps the trailing [u32 len][ProtoLootBagIIR] for the live contents, via the
		// typed LootBagContentsCodec. Nothing captured is replayed; the two-step split is only because the envelope builder
		// is the generic dynel/bag one, so the loot-specific cash+items are injected here at drop time. Per-item ids
		// are synthetic (display only). Returns the body unchanged if the contents blob can't be located.
		public static byte[] WithLootContents(byte[] record, uint handle, uint cash, IReadOnlyList<(uint Pos, Item Item)> entries)
		{
			if (!WorldObjectColumns.TryLocateContents(record, out var contents, out var contentsStart))
				return record;

			contents.Cash = cash;
			contents.Inventory.Items.Clear();
			uint maxPos = 0;
			foreach (var (pos, item) in entries)
			{
				var guid = ((ulong)handle << 16) | pos; // synthetic per-instance id (display only)
				contents.Inventory.Items.Add(SnapshotItems.ListEntry(pos, guid, guid, 1, item, item.Hash));
				maxPos = Math.Max(maxPos, pos);
			}

			contents.Inventory.MaxSize = entries.Count > 0
				? Math.Max(10u, maxPos + 1)
				: 10u;

			var iir = WorldObjectCodec.Parse(record);
			return WorldObjectCodec.Build(iir with { Tail = [.. iir.Tail.AsSpan(0, contentsStart), .. LootBagContentsCodec.Build(contents)] });
		}
	}
}
