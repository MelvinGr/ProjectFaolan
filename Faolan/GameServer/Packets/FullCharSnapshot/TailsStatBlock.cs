using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// The fresh-character STAT-block skeleton (the SnapshotTails PostAccessBlock), relocated out of
	// BeachTailsTemplate.bin so the stat region can be authored without the .bin (#186). Decoded by
	// Captures/scripts/decode_statblock.py: of the 338 values, only ~14 distinct slots are character/map-derived (class
	// HP/Mana/Stamina + archetype, XP, the playfield token + position, one session slot) - the other 313 are this
	// FIXED baseline, identical for every fresh character. CreateFreshBlock() returns a PostAccessBlock seeded with
	// the skeleton; the caller patches the character-derived slots via the existing setters (CurrentHp/Mana/Stamina/
	// CurrentXp + Level), exactly as SnapshotTailsBuilder already does on the parsed block - so generating == relocating
	// this baseline (the genuinely-fixed fresh-char stats) + the already-computed patches.
	public static class TailsStatBlock
	{
		public const int RepeatedRunLength = 148;
		public const int RepeatedRunStart = 35; // the 35..182 sub-block is copied to 183..330 (Length 148)
		public const uint BlockType = 0x46F2;
		public const uint Marker = 1009; // 0x3F1, twice

		// The block is TWO 0x3F1-framed StatList_t (StatPair_t = [statId, value]; docs/fullplayer-snapshot-reader.md
		// regions 9-10). StatList#1 = the 17 DISPLAYED stats (its count word IS the BlockType field, (17+1)*0x3F1 =
		// 0x46F2). StatList#2 = the full sheet: 74 UNIQUE pairs written TWICE (base + current copy = 148 pairs, the old
		// "repeated run"). Every value is a named (statId, value) - the character-derived stats (Level 0x36, XP 0x34,
		// MaxXP 0x15E, HP 0x1B, mana, stamina) are overwritten from the character by the caller; the rest are the fresh
		// baseline (armour/resists/... we do not model yet, so carried as their fixed defaults). No opaque blob.
		private static readonly (uint Id, uint Value)[] StatList1 =
		[
			(0x252,0x0), (0x253,0x0), (0x254,0x0), (0x255,0x0), (0x0,0x1), (0xE0,0x0),
			(0xAE,0x2), (0xAD,0x2), (0xBF,0x4FE428), (0x40B,0x0), (0x1,0x2328), (0x1B,0x2648),
			(0x36,0x1), (0x34,0x0), (0x15E,0x64), (0x39,0x0), (0xA9,0x0)
		];

		// One half of StatList#2; the client carries it twice (base + current). The [0x566][0x290] pair at the tail is
		// the known-spell-list landmark FindKnownSpellCountIndex keys on.
		private static readonly (uint Id, uint Value)[] StatList2Half =
		[
			(0x534,0x0), (0x16B,0x0), (0x44A,0x0), (0x163,0x0), (0x161,0xDAC), (0x449,0x0),
			(0xA5,0x6F), (0x241,0x0), (0x113,0x8), (0x1B,0x2648), (0x1,0x2328), (0x328,0xA),
			(0x1FA,0x0), (0x1FB,0x0), (0x1FA,0x0), (0x1F9,0x59D8), (0x1F8,0x5C30), (0x324,0xA),
			(0x4F,0x0), (0xC,0x0), (0x22,0x0), (0x3B,0x3), (0x15E,0x64), (0x39,0x0),
			(0x36,0x1), (0x46,0x0), (0x33,0x0), (0x34,0x0), (0xB,0x3E8), (0x3A,0xBA),
			(0x21,0x0), (0x4B,0x0), (0xD7,0x0), (0x185,0x0), (0x25F,0x59EA506E), (0x6A,0x14),
			(0xF3,0x0), (0x445,0x0), (0x43D,0x0), (0x33E,0x0), (0x33C,0x0), (0x8C,0x0),
			(0x338,0x0), (0x332,0x0), (0x39D,0x64), (0x39B,0x64), (0x398,0x64), (0x39A,0x64),
			(0x7F,0x0), (0x5B,0x0), (0x3C,0x0), (0x1C8,0x0), (0x14B,0x0), (0x3D,0x0),
			(0x575,0x0), (0x560,0x0), (0x573,0x0), (0x29F,0x0), (0x56E,0x0), (0x29E,0x0),
			(0x571,0x0), (0x29D,0x0), (0x56B,0x0), (0x29C,0x0), (0x570,0x0), (0x29B,0x0),
			(0x56A,0x0), (0x29A,0x0), (0x569,0x0), (0x564,0x0), (0x567,0x0), (0x292,0x0),
			(0x566,0x0), (0x290,0x0)
		];

		// The tail after StatList#2: the empty known-spell list (count 0x3F1) + the trailing pair-list.
		private static readonly uint[] Trailer = [0x3F1, 0x1, 0x1000000, 0x0, 0x0, 0x0, 0x0];

		public static TailsPostAccessBlock CreateFreshBlock()
		{
			// Assemble the Values array from the structured StatLists (byte-identical to the captured block):
			// StatList#1 pairs, then StatList#2's count word + its 74 pairs twice, then the trailer.
			var values = new List<uint>(338);
			foreach (var (id, value) in StatList1)
			{
				values.Add(id);
				values.Add(value);
			}

			values.Add(((uint)StatList2Half.Length * 2 + 1) * StatPairList.Frame); // StatList#2 count = 0x24B45
			for (var copy = 0; copy < 2; copy++)
				foreach (var (id, value) in StatList2Half)
				{
					values.Add(id);
					values.Add(value);
				}

			values.AddRange(Trailer);

			return new TailsPostAccessBlock
			{
				Marker0 = Marker, // the two preceding empty framed lists (List E / F)
				Marker1 = Marker,
				BlockType = ((uint)StatList1.Length + 1) * StatPairList.Frame, // StatList#1 count word = 0x46F2
				Values = values,
				Trailer = [0x00],
				// Non-null so HasKnownRepeatedStatTable is true and the caller's vitals patches (which write the +148
				// copy too) fire; the run mirrors RepeatedBlockStartIndex/Offset on TailsPostAccessBlock.
				LargestAdjacentRepeatedRun = new TailsRepeatedUInt32Run
				{
					StartIndex = RepeatedRunStart,
					Length = RepeatedRunLength,
					RepeatCount = 2
				}
			};
		}

		// Scans a raw 0x737A6DF9 snapshot body for the stat block and returns its (statId, value) pairs in order:
		// StatList#1 (the 17 displayed stats, count word 0x46F2) then StatList#2's first copy (the 74 sheet stats - the
		// sheet is written TWICE on the wire, base + current; we keep one). Null when not found. This is the inverse of
		// the generated block - the importer uses it to store a character's REAL stat sheet from its login snapshot.
		public static List<(uint Id, uint Value)> ExtractFromSnapshot(ReadOnlySpan<byte> body)
		{
			for (var i = 0; i + 8 <= body.Length; i++)
			{
				if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i, 4)) != BlockType)
					continue; // not a StatList#1 count word (0x46F2)

				if (!TryReadStatList(body, i, out var displayed, out var afterDisplayed) || displayed.Count != StatList1.Length)
					continue;

				// Reject a spurious 0x46F2 that is really a stat VALUE: the displayed stat ids are all small.
				if (displayed.Exists(p => p.Id >= 0x700))
					continue;

				if (!TryReadStatList(body, afterDisplayed, out var sheet, out _) || sheet.Count < StatList2Half.Length * 2)
					continue;

				var pairs = new List<(uint, uint)>(StatList1.Length + StatList2Half.Length);
				pairs.AddRange(displayed);
				pairs.AddRange(sheet.GetRange(0, StatList2Half.Length)); // first copy = the 74 unique sheet stats
				return pairs;
			}

			return null;
		}

		private static bool TryReadStatList(ReadOnlySpan<byte> body, int off, out List<(uint Id, uint Value)> list, out int end)
		{
			list = null;
			end = off;
			var count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(off, 4));
			if (count == 0 || count % StatPairList.Frame != 0)
				return false;

			var n = (int)(count / StatPairList.Frame) - 1;
			if (off + 4 + n * 8 > body.Length)
				return false;

			list = new List<(uint, uint)>(n);
			var p = off + 4;
			for (var i = 0; i < n; i++)
			{
				list.Add((BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4)), BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p + 4, 4))));
				p += 8;
			}

			end = p;
			return true;
		}

		// The known-spell list lives INSIDE this stat block as a 0x3F1-framed list of 12-byte records (KnownSpellList),
		// identified by the 16-byte landmark [0x566][0][0x290][0] immediately followed by a count word that is a positive
		// multiple of 0x3F1. The fresh skeleton carries it empty (count 0x3F1, 0 records). InjectKnownSpells rewrites the
		// count and splices the character's learned-spell records in after it, growing Values. The landmark word [0x566]
		// also precedes an UNRELATED list (count 0x534, not a 0x3F1 multiple), so the multiple-of-0x3F1 count is the
		// discriminator. Splices into the Values list (the count index is past the repeated stat block 35..330, so the
		// +148 copies are untouched). See docs/handoff-2026-06-20-snapshot-known-spell-list.md.
		public static int FindKnownSpellCountIndex(IReadOnlyList<uint> values)
		{
			for (var i = 4; i < values.Count; i++)
			{
				if (values[i] != 0 && values[i] % KnownSpellList.Frame == 0 && values[i - 4] == 0x566 && values[i - 3] == 0 && values[i - 2] == 0x290 && values[i - 1] == 0)
					return i;
			}

			return -1;
		}

		// Splices the character's known-spell records into the stat block's known-list. No-op for an empty list (so the
		// fresh skeleton stays byte-identical to the beach template). Each record is 3 big-endian u32s
		// (id, 0xFFFFFFFF, type); the count word becomes (records + 1) * 0x3F1.
		public static void InjectKnownSpells(TailsPostAccessBlock block, KnownSpellList known)
		{
			if (known is not { Records.Count: > 0 })
				return;

			var idx = FindKnownSpellCountIndex(block.Values);
			if (idx < 0)
				throw new InvalidOperationException("Could not locate the known-spell list count word in the stat block.");

			block.Values[idx] = ((uint)known.Records.Count + 1) * KnownSpellList.Frame;

			var insert = new List<uint>(known.Records.Count * 3);
			foreach (var record in known.Records)
			{
				insert.Add(record.Id);
				insert.Add(KnownSpellList.Unbound);
				insert.Add(record.Type);
			}

			block.Values.InsertRange(idx + 1, insert);
		}

		// Serializes a stat block to its wire bytes: [u32 Marker0][u32 Marker1][u32 BlockType][u32 x Values][Trailer]
		// (mirrors SnapshotTails.WritePostAccessBlock). 12 + Values.Count*4 + Trailer bytes - 1365 for the fresh block.
		public static byte[] Serialize(TailsPostAccessBlock block)
		{
			var w = new FaolanStream();
			w.WriteUInt32(block.Marker0);
			w.WriteUInt32(block.Marker1);
			w.WriteUInt32(block.BlockType);
			foreach (var value in block.Values)
				w.WriteUInt32(value);
			w.WriteArray(block.Trailer);
			return w.ToArray();
		}
	}
}
