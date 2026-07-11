using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.World
{
	// Pure wire-body transforms extracted from WorldBroadcaster (they take captured/DB inputs, no world state): the
	// patrol re-address, a QuestTextRef -> on-wire record, and the loot-bag contents splice. The broadcaster keeps
	// the interest/send orchestration; these are the byte construction it forwards.
	public static class WorldBroadcastBodies
	{
		// A QuestTextRef -> on-wire text record (remoteformat/localized). Missing slots fall back to an empty
		// remoteformat so the framing stays valid (the client just renders nothing for that slot).
		public static byte[] Record(QuestDefinition def, QuestTextSlot slot)
		{
			var r = def.TextRefs.FirstOrDefault(t => t.Slot == slot);
			if (r == null)
				return WorldQuest.RemoteFormat(0, 0, "");

			return r.IsLocalized
				? WorldQuest.Localized((int)r.RefId, (int)r.Category, r.Flags ?? "", r.KeyText ?? "")
				: WorldQuest.RemoteFormat((int)r.RefId, (int)r.Category, r.KeyText ?? "");
		}

		// Injects THIS drop's live contents (cash + rolled items) into an already-from-scratch-built loot-bag body
		// (WorldObjectBuilder + LootBagTemplate build the identity + kd-tree + a scaffold ProtoLootBagIIR): keeps the
		// constructed envelope and swaps the trailing [u32 len][ProtoLootBagIIR] for the live contents, via the typed
		// LootBagMessageCodec. Nothing captured is replayed; the two-step split is only because the envelope builder
		// is the generic dynel/bag one, so the loot-specific cash+items are injected here at drop time. Per-item ids
		// are synthetic (display only). Returns the body unchanged if the contents blob can't be located.
		public static byte[] WithLootContents(byte[] body, uint handle, uint cash, IReadOnlyList<(uint Pos, Item Item)> entries)
		{
			if (body.Length <= 8 || !LootBagMessageCodec.TryParse(body.AsSpan(8), out var contents, out var contentsStart))
				return body;

			contents.Cash = cash;
			contents.Inventory.Items.Clear();
			uint maxPos = 0;
			foreach (var (pos, item) in entries)
			{
				var guid = ((ulong)handle << 16) | pos; // synthetic per-instance id (display only)
				contents.Inventory.Items.Add(SnapshotItemBuilder.BuildListEntry(pos, guid, guid, 1, item, item.Hash));
				maxPos = Math.Max(maxPos, pos);
			}

			contents.Inventory.MaxSize = entries.Count > 0
				? Math.Max(10u, maxPos + 1)
				: 10u;

			var newContents = LootBagMessageCodec.BuildContents(contents);
			var newRecordLen = contentsStart + newContents.Length;
			var result = new byte[8 + newRecordLen];
			body.AsSpan(0, 8 + contentsStart).CopyTo(result); // [u32 len][u32 subOpcode][identity + kd-tree]
			newContents.CopyTo(result.AsSpan(8 + contentsStart)); // the new [u32 len][ProtoLootBagIIR] tail
			BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)(4 + newRecordLen)); // carrier len = subOpcode + record
			return result;
		}
	}
}
