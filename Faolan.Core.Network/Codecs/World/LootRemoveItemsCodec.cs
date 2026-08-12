using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// LootRemoveItemsIIR (0x70010162) - the per-take ACK that tells the client what to remove from the open bag
	// window; the client removes it and compacts the window. 25 B for the single-slot case:
	//   [subop][0xC87A LootBag][bagHandle][u8 env][u32 cash][ListFrame count][u32 removedPos ...]
	//
	// ⚠️ The two fields after the handle used to be read as `[u32 reserved][u8 marker]`, both required to be zero -
	// a MISALIGNED split that is invisible while both are (#809). The client's own reader says otherwise: after the
	// carrier's standard `[u8 env]`, `LootRemoveItemsIIR_c::ReadSubClass` (FUN_007A5950) reads ONE u32 into
	// `this+0x3c` and then the count-framed list (FUN_004D4EF0, the generic `word/0x3F1 - 1` reader). Straddling
	// those two made the u32 look like a zero word plus a stray byte, and 48 of the 148 captured records - every one
	// carrying a non-zero value - were refused outright.
	//
	// ⭐ That u32 is the CASH taken out of the bag. Proven on the eight cash loots in
	// `xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city`: each `TryLootCash` is answered by a
	// `DispatchUpdateCash` and this record, and the value equals the player's cash DELTA exactly, every time
	// (21, 1, 30, 1, 3, 1, 3, 36). It is a strict alternative to the position list across all 148 captured records:
	// a cash take sends `[cash=N][empty list]`, an item take `[cash=0][the slots]`, and neither ever sends both.
	//
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
		public const uint Opcode = (uint)IirTypes.LootRemoveItemsIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LootRemoveItemsIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The positions are carried VERBATIM, including an empty list (#453). An empty list used to be substituted with
		// `[0]` - which reads as harmless "default to the first slot" and is not: the bare `ListFrame.ForCount(0)`
		// (0x3F1) is a real captured shape, 50 of the 148 records in the corpus, sitting alongside each bag's removal
		// message. Substituting made that shape inexpressible and turned a "removed nothing" into "removed slot 0".
		public static byte[] Build(uint bagHandle, params uint[] removedPositions)
		{
			return Build(new LootRemoveItemsIIR(bagHandle, removedPositions));
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
			var env = r.ReadU8();
			var cash = r.ReadU32Be();
			var count = r.ReadListCount();
			var positions = new List<uint>(count);
			for (var i = 0; i < count; i++)
				positions.Add(r.ReadU32Be());
			return new LootRemoveItemsIIR(bagHandle, positions, cash, env);
		}

		internal static void Write(FaolanStream s, LootRemoveItemsIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.LootBag).WriteUInt32(v.BagHandle).WriteByte(v.Env).WriteUInt32(v.Cash).WriteUInt32(ListFrame.ForCount(v.RemovedPositions.Count));
			foreach (var pos in v.RemovedPositions)
				s.WriteUInt32(pos);
		}
	}

	// LootRemoveItemsIIR (0x70010162). `Cash` and `RemovedPositions` are alternatives - a take is one or the other,
	// never both (148/148 captured records). `Env` is the carrier's env byte, 0 on every captured record.
	public sealed record LootRemoveItemsIIR(uint BagHandle, IReadOnlyList<uint> RemovedPositions, uint Cash = 0, byte Env = 0);
}
