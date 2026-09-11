using System;
using System.Collections.Generic;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     Pure wire-body transforms over captured or DB inputs, carrying no world state: the broadcaster keeps the
	///     interest and send orchestration, and forwards the byte construction here.
	/// </summary>
	public static class WorldBroadcastBodies
	{
		/// <summary>
		///     Injects one drop's live contents (cash plus rolled items) into a loot-bag record built from scratch:
		///     <see cref="WorldObjectCodec" /> and LootContainerTemplate build the identity, kd-tree and a scaffold
		///     ProtoLootBagIIR, and this keeps that envelope while swapping the trailing [u32 len][ProtoLootBagIIR] for
		///     the live contents via LootBagContentsCodec.
		/// </summary>
		/// <remarks>
		///     Nothing captured is replayed; the two-step split exists because the envelope builder is the generic
		///     dynel/bag one, so the loot-specific cash and items are injected here at drop time. Per-item ids are
		///     synthetic and for display only.
		/// </remarks>
		/// <returns>The body unchanged when the contents blob cannot be located.</returns>
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
				contents.Inventory.Items.Add(SnapshotItems.ListEntry(pos, guid, guid, 1, item, AcgHash.ForWire(item.Id, item.Hash)));
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
