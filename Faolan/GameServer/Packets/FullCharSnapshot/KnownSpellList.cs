using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// The per-character KNOWN list carried inside the 0x737A6DF9 FullChar snapshot tails (the spellbook region): a
	// 0x3F1-framed list of 12-byte records, one per spell/feat the character knows. Decoded byte-exact from the
	// Faolanx (clean fresh necromancer) login snapshot - its list is exactly the 2 create-spells - and matches the
	// empty list in the beach template (count 0x3F1, 0 records). See docs/handoff-2026-06-20-snapshot-known-spell-list.md.
	//
	// Record (big-endian): [u32 id][u32 0xFFFFFFFF][u8 0x00 x3][u8 type]. The 0xFFFFFFFF is the (unbound) bar slot;
	// type 2 = spell, type 5 = feat/ability. Count word = (recordCount + 1) * 0x3F1 (the standard list-framing
	// invariant). A fresh character's list is just its learned spells (type 2) - the 3 Normal Attacks do NOT appear
	// here (Faolanx's list has no type-5 records), so this carries spells only.
	public sealed class KnownSpellList
	{
		public const byte FeatType = 5;
		public const byte SpellType = 2;
		public const uint Frame = 1009; // 0x3F1
		public const uint Unbound = 0xFFFFFFFF; // the "no bound bar slot" word

		public List<KnownSpellRecord> Records { get; init; } = [];

		// The count word + the records, big-endian (the on-wire bytes that replace the empty list's count word).
		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(((uint)Records.Count + 1) * Frame);
			foreach (var record in Records)
			{
				w.WriteUInt32(record.Id);
				w.WriteUInt32(Unbound);
				w.WriteUInt32(record.Type); // big-endian => 00 00 00 <type>
			}

			return w.ToArray();
		}

		public static KnownSpellList FromSpellIds(IReadOnlyList<uint> spellIds)
		{
			return new KnownSpellList { Records = [.. (spellIds ?? []).Select(id => new KnownSpellRecord(id, SpellType))] };
		}

		// Parses the [count][records] sub-structure beginning at `data[offset]` (the count word). `consumed` reports the
		// total byte length read (4 + recordCount * 12). Throws when the count is not a positive multiple of 0x3F1 or
		// the records run past the buffer - so a mis-located slot is rejected rather than yielding garbage.
		public static KnownSpellList Parse(ReadOnlySpan<byte> data, int offset, out int consumed)
		{
			var count = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
			if (count == 0 || count % Frame != 0)
				throw new InvalidOperationException($"known-list count 0x{count:X} at 0x{offset:X} is not a positive multiple of 0x{Frame:X}.");

			var recordCount = (int)(count / Frame) - 1;
			var records = new List<KnownSpellRecord>(recordCount);
			var pos = offset + 4;
			for (var i = 0; i < recordCount; i++)
			{
				var id = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
				var unbound = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4, 4));
				if (unbound != Unbound)
					throw new InvalidOperationException($"known-list record {i} at 0x{pos:X} has slot word 0x{unbound:X}, expected 0x{Unbound:X}.");

				var type = (byte)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8, 4));
				records.Add(new KnownSpellRecord(id, type));
				pos += 12;
			}

			consumed = pos - offset;
			return new KnownSpellList { Records = records };
		}
	}

	public readonly record struct KnownSpellRecord(uint Id, byte Type);
}
