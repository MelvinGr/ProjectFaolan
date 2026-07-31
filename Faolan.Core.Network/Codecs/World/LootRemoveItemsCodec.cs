using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// LootRemoveItemsIIR (0x70010162) - the per-take ACK that tells the client which display slot(s) to remove from
	// the open bag window; the client removes them and compacts the window. 25 B for the single-slot case:
	//   [subop][0xC87A LootBag][bagHandle][u32 0][u8 0][ListFrame count][u32 removedPos ...]
	// The removed positions are a COUNT-FRAMED LIST, not a landmark plus a value. This used to be written as
	// `[u32 0x00000007][u8 0xE2]`, straddling the word boundary, and described as "the 0x000007E2 landmark is a
	// fixed constant here" - the exact mistake the project rule names: 0x7E2 is ListFrame.ForCount(1), every
	// capture we have takes ONE slot at a time, and a single-element list is indistinguishable from a constant.
	// Reading it aligned puts the u8 marker where its siblings carry one (NewLooterCodec has a u8 after the handle
	// too) and leaves the list word 4-byte aligned. The bytes are unchanged - WorldLootTakeTests pins both captured
	// frames.
	//
	// Decoded from loot-drag-1538 (a drag-only capture): drag srcPos=1 -> element=1, drag srcPos=0 -> element=0,
	// single-item bags (srcPos 0) -> 0, loot-all -> 0. The OLD constant element=0 removed slot 0 on every take
	// (#226-2: "first slot disappears"); a remaining-COUNT value (e.g. 2) named no real slot, so the dragged item
	// was left in the bag.
	//
	// Whether a multi-slot take sends count>1 rather than N single-slot acks is UNVERIFIED - no capture takes two
	// slots in one message. Gitea #453. [V0]
	public static class LootRemoveItemsCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LootRemoveItemsIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LootRemoveItemsIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint bagHandle, params uint[] removedPositions)
		{
			return Build(new LootRemoveItemsIIR(bagHandle, removedPositions.Length == 0
				? [0u]
				: removedPositions));
		}

		public static LootRemoveItemsIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LootRemoveItemsCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static LootRemoveItemsIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "LootRemoveItemsIIR subop");
			r.ExpectU32Be(EntityClass.LootBag, "entity class");
			var bagHandle = r.ReadU32Be();
			r.ExpectU32Be(0, "reserved word");
			r.ExpectU8(0, "record marker");
			var count = r.ReadListCount();
			var positions = new List<uint>(count);
			for (var i = 0; i < count; i++)
				positions.Add(r.ReadU32Be());
			return new LootRemoveItemsIIR(bagHandle, positions);
		}

		internal static void Write(FaolanStream s, LootRemoveItemsIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.LootBag).WriteUInt32(v.BagHandle).WriteUInt32(0).WriteByte(0x00).WriteUInt32(ListFrame.ForCount(v.RemovedPositions.Count));
			foreach (var pos in v.RemovedPositions)
				s.WriteUInt32(pos);
		}
	}

	// LootRemoveItemsIIR (0x70010162).
	public sealed record LootRemoveItemsIIR(uint BagHandle, IReadOnlyList<uint> RemovedPositions);
}
