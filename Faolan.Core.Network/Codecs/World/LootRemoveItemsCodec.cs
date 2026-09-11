using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     LootRemoveItemsIIR - the per-take ack that tells the client what to remove from the open bag window, which it
	///     then compacts. 25 bytes for the single-slot case:
	///     <c>[subop][class][bagHandle][u8 env][u32 cash][ListFrame count][u32 removedPos ...]</c>.
	/// </summary>
	/// <remarks>
	///     The field after the carrier's env byte is a u32, not a zero word plus a stray marker byte: the client's
	///     LootRemoveItemsIIR_c::ReadSubClass (FUN_007A5950) reads one u32 into this+0x3c and then the count-framed list
	///     (FUN_004D4EF0, the generic frame reader). Straddling that boundary makes the u32 look like a zero word plus a
	///     byte and refuses the 48 of 148 captured records that carry a non-zero value.
	///     <para>
	///         That u32 is the cash taken out of the bag, proven on the eight cash loots in
	///         xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city: each TryLootCash is answered by a
	///         DispatchUpdateCash and this record, and the value equals the player's cash delta exactly every time (21, 1,
	///         30, 1, 3, 1, 3, 36). It is a strict alternative to the position list across all 148 captured records - a cash
	///         take sends the cash with an empty list, an item take sends zero cash with the slots, and neither ever sends
	///         both.
	///     </para>
	///     <para>
	///         The removed positions are a count-framed list rather than a landmark plus a value. Every capture takes one
	///         slot at a time, and a single-element list is indistinguishable from a constant; reading it aligned puts the
	///         env byte where its siblings carry one (<see cref="NewLooterCodec" /> has one after the handle too) and leaves
	///         the list word 4-byte aligned. Decoded from a drag-only capture: a drag from slot 1 gives element 1, from slot
	///         0 element 0, a single-item bag 0, and loot-all 0. A constant element 0 removes the first slot on every take,
	///         and a remaining-count value names no real slot at all, leaving the dragged item in the bag.
	///     </para>
	///     <para>
	///         Whether a multi-slot take sends a count above 1 rather than several single-slot acks is unverified: no
	///         capture takes two slots in one message. [V0]
	///     </para>
	/// </remarks>
	public static class LootRemoveItemsCodec
	{
		public const uint Opcode = (uint)IirTypes.LootRemoveItemsIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(LootRemoveItemsIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     Builds the ack. The positions are carried verbatim, an empty list included: the bare empty-list frame is a
		///     real captured shape, 50 of the 148 records in the corpus, sitting alongside each bag's removal message.
		///     Substituting a single zero position for it makes that shape inexpressible and turns "removed nothing" into
		///     "removed slot 0".
		/// </summary>
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

	/// <summary>
	///     The LootRemoveItemsIIR body. <c>Cash</c> and <c>RemovedPositions</c> are alternatives - a take is one or the
	///     other, never both, across all 148 captured records - and <c>Env</c> is the carrier's env byte, 0 on every
	///     captured record.
	/// </summary>
	public sealed record LootRemoveItemsIIR(uint BagHandle, IReadOnlyList<uint> RemovedPositions, uint Cash = 0, byte Env = 0);
}
