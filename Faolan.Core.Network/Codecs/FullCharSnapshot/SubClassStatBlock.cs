using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Stat = Faolan.Core.Enums.CharacterStatId;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The fresh-character STAT-block skeleton (the FullCharSnapshotSubClass PostAccessBlock), relocated out of
	// BeachTailsTemplate.bin so the stat region can be authored without the .bin (#186). Decoded by
	// Captures/scripts/decode_statblock.py: of the 338 values, only ~14 distinct slots are character/map-derived (class
	// HP/Mana/Stamina + hash, XP, the playfield token + position, one session slot) - the other 313 are this
	// FIXED baseline, identical for every fresh character. CreateFreshBlock() returns a PostAccessBlock seeded with
	// the skeleton; the caller patches the character-derived slots via the existing setters (CurrentHp/Mana/Stamina/
	// CurrentXp + Level), exactly as FullCharSnapshotSubClass already does on the parsed block - so generating == relocating
	// this baseline (the genuinely-fixed fresh-char stats) + the already-computed patches.
	public static class SubClassStatBlock
	{
		public const int RepeatedRunLength = 148;
		public const int RepeatedRunStart = 35; // the 35..182 sub-block is copied to 183..330 (Length 148)
		public const uint BlockType = 0x46F2;
		public const uint Marker = 1009; // 0x3F1, twice

		// The stat slot whose VALUE is the playfield-instance token: in the snapshot the token is framed by the stat ids
		// 0xBF..0x40B (see WorldDespawn). This is SESSION/MAP state, NOT a character stat - the snapshot is BUILT with the
		// live zone's minted token (FullCharSnapshotSubClass passes it in), never a captured one. Replaying a stale token
		// HIDES the entire in-client abilities tab (bisected on NecroL80's level-80 sheet, 2026-07-04) and mis-scopes
		// despawns. SkeletonValueOf (the beach 0x4FE428) is the fresh/fallback value when no live token is given.
		public const uint PlayfieldTokenStatId = (uint)Stat.PlayfieldToken;

		// The number of stat pairs a full sheet carries: StatList#1 (the displayed stats) + StatList#2's single copy.
		public static int SheetSize => StatList1.Length + StatList2Half.Length;

		// The block is TWO 0x3F1-framed StatList_t (StatPair_t = [statId, value]; docs/fullplayer-snapshot-reader.md
		// regions 9-10). StatList#1 = the 17 DISPLAYED stats (its count word IS the BlockType field, (17+1)*0x3F1 =
		// 0x46F2). StatList#2 = the full sheet: 74 UNIQUE pairs written TWICE (base + current copy = 148 pairs, the old
		// "repeated run"). Every value is a named (statId, value) - the character-derived stats (Level 0x36, XP 0x34,
		// MaxXP 0x15E, HP 0x1B, mana, stamina) are overwritten from the character by the caller; the rest are the fresh
		// baseline (armour/resists/... we do not model yet, so carried as their fixed defaults). No opaque blob.
		private static readonly (Stat Id, uint Value)[] StatList1 =
		[
			(Stat.Unknown0, 0x0), (Stat.Unknown1, 0x0), (Stat.Unknown2, 0x0), (Stat.Unknown3, 0x0), (Stat.Unknown4, 0x1), (Stat.Unknown5, 0x0), (Stat.Unknown6, 0x2), (Stat.Unknown7, 0x2),
			(Stat.PlayfieldToken, 0x4FE428), (Stat.Unknown9, 0x0), (Stat.HealthMax, 0x2328), (Stat.HealthCurrent, 0x2648), (Stat.Level, 0x1), (Stat.Exp, 0x0), (Stat.MaxExp, 0x64),
			(Stat.Unknown10, 0x0), (Stat.Unknown11, 0x0)
		];

		// One half of StatList#2; the client carries it twice (base + current). Its last two slots (Unknown63 then
		// KillingBlows) are the tail the known-spell list is built right after (CreateBlock).
		private static readonly (Stat Id, uint Value)[] StatList2Half =
		[
			(Stat.Unknown12, 0x0), (Stat.Unknown13, 0x0), (Stat.Unknown14, 0x0), (Stat.Unknown15, 0x0), (Stat.PvpMaxExp, 0xDAC), (Stat.Unknown16, 0x0), (Stat.Unknown17, 0x6F),
			(Stat.Unknown18, 0x0), (Stat.Unknown19, 0x8), (Stat.HealthCurrent, 0x2648), (Stat.HealthMax, 0x2328), (Stat.Intelligence, 0xA), (Stat.ManaCurrent, 0x0), (Stat.ManaMax, 0x0),
			(Stat.ManaCurrent, 0x0), (Stat.StaminaMax, 0x59D8), (Stat.StaminaCurrent, 0x5C30), (Stat.Strength, 0xA), (Stat.Unknown20, 0x0), (Stat.Unknown21, 0x0), (Stat.Unknown22, 0x0),
			(Stat.Unknown23, 0x3), (Stat.MaxExp, 0x64), (Stat.Unknown10, 0x0), (Stat.Level, 0x1), (Stat.PvpLevel, 0x0), (Stat.PvpExp, 0x0), (Stat.Exp, 0x0), (Stat.Unknown24, 0x3E8),
			(Stat.Unknown25, 0xBA), (Stat.Unknown26, 0x0), (Stat.Unknown27, 0x0), (Stat.Unknown28, 0x0), (Stat.Unknown29, 0x0), (Stat.Unknown30, 0x59EA506E), (Stat.Unknown31, 0x14),
			(Stat.Unknown32, 0x0), (Stat.Unknown33, 0x0), (Stat.Unknown34, 0x0), (Stat.Unknown35, 0x0), (Stat.Unknown36, 0x0), (Stat.Unknown37, 0x0), (Stat.Unknown38, 0x0), (Stat.Unknown39, 0x0),
			(Stat.Unknown40, 0x64), (Stat.Unknown41, 0x64), (Stat.Unknown42, 0x64), (Stat.Unknown43, 0x64), (Stat.Unknown44, 0x0), (Stat.Unknown45, 0x0), (Stat.Unknown46, 0x0),
			(Stat.Unknown47, 0x0), (Stat.Unknown48, 0x0), (Stat.Unknown49, 0x0), (Stat.Unknown50, 0x0), (Stat.Unknown51, 0x0), (Stat.Unknown52, 0x0), (Stat.Unknown53, 0x0), (Stat.Unknown54, 0x0),
			(Stat.Unknown55, 0x0), (Stat.Unknown56, 0x0), (Stat.CaptureSkullLose, 0x0), (Stat.Unknown57, 0x0), (Stat.CaptureSkullWin, 0x0), (Stat.Unknown58, 0x0), (Stat.AnnihilationLose, 0x0),
			(Stat.Unknown59, 0x0), (Stat.AnnihilationWin, 0x0), (Stat.Unknown60, 0x0), (Stat.Unknown61, 0x0), (Stat.Unknown62, 0x0), (Stat.Death, 0x0), (Stat.Unknown63, 0x0),
			(Stat.KillingBlows, 0x0)
		];

		// The fixed words AFTER the known-spell list at the block tail (the known-list - its count word then the 12-byte
		// records - is built inline by CreateBlock, so it is no longer spliced in after the fact).
		private static readonly uint[] TrailerRest = [0x1, 0x1000000, 0x0, 0x0, 0x0, 0x0];

		// Builds a stat block from a full stat sheet: `sheet` is StatList#1 (its first StatList1.Length pairs - the
		// displayed stats) followed by StatList#2 (the remaining pairs, ONE copy). StatList#2 is emitted TWICE on the
		// wire (base + current), so the block is [17 displayed pairs][StatList#2 count][74 sheet pairs x2][known-spell
		// list][trailer]. The KNOWN-SPELL LIST is built inline here - its count word ((records+1)*0x3F1) then each 12-
		// byte [id][0xFFFFFFFF][type] record; a null/empty list is just the count 0x3F1 (byte-identical to the fresh
		// skeleton). Everything is CONSTRUCTED directly, so nothing scans the whole list (SetStat would otherwise also
		// overwrite the trailer's 0x1 word and the known-list ids - the reason this used to be a fragile post-hoc splice).
		public static SubClassPostAccessBlock CreateBlock(IReadOnlyList<(uint Id, uint Value)> sheet, KnownSpellList known = null)
		{
			var displayedCount = StatList1.Length;
			var sheetCount = sheet.Count - displayedCount;

			var values = new List<uint>(338);
			for (var i = 0; i < displayedCount; i++)
			{
				values.Add(sheet[i].Id);
				values.Add(sheet[i].Value);
			}

			values.Add(((uint)sheetCount * 2 + 1) * StatList.Frame); // StatList#2 count = 0x24B45 for 74 pairs
			for (var copy = 0; copy < 2; copy++)
			for (var i = displayedCount; i < sheet.Count; i++)
			{
				values.Add(sheet[i].Id);
				values.Add(sheet[i].Value);
			}

			var records = known?.Records ?? [];
			values.Add(((uint)records.Count + 1) * StatList.Frame); // known-spell list count (0x3F1 when empty)
			foreach (var record in records)
			{
				values.Add(record.Id);
				values.Add(KnownSpellList.Unbound);
				values.Add(record.Type);
			}

			values.AddRange(TrailerRest);

			return new SubClassPostAccessBlock
			{
				Marker0 = Marker, // the two preceding empty framed lists (List E / F)
				Marker1 = Marker,
				BlockType = ((uint)displayedCount + 1) * StatList.Frame, // StatList#1 count word = 0x46F2
				Values = values,
				Trailer = [0x00],
				// Non-null so HasKnownRepeatedStatTable is true and the caller's vitals patches (which write the +148
				// copy too) fire; the run mirrors RepeatedBlockStartIndex/Offset on SubClassPostAccessBlock.
				LargestAdjacentRepeatedRun = new SubClassRepeatedUInt32Run
				{
					StartIndex = RepeatedRunStart,
					Length = RepeatedRunLength,
					RepeatCount = 2
				}
			};
		}

		public static SubClassPostAccessBlock CreateFreshBlock(KnownSpellList known = null)
		{
			// The fresh skeleton is just the baseline sheet run through the generic builder - byte-identical to the
			// captured beach block (proven by BeachTailsFromScratchTests).
			return CreateBlock(FreshSheet(), known);
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

		// The fresh-character baseline stat sheet (StatList#1's 17 displayed pairs + StatList#2's 74 sheet pairs, ONE
		// copy), as a fresh mutable list - the caller patches the character-derived slots (level/...) on IT before
		// building, so nothing has to SetStat the finished block (which would also scan the inline known-list).
		public static List<(uint Id, uint Value)> FreshSheet()
		{
			var sheet = new List<(uint, uint)>(SheetSize);
			foreach (var (id, value) in StatList1)
				sheet.Add(((uint)id, value));
			foreach (var (id, value) in StatList2Half)
				sheet.Add(((uint)id, value));
			return sheet;
		}

		// (The known-spell list is no longer spliced in after the fact - CreateBlock builds it inline at the block tail,
		// so the former FindKnownSpellCountIndex landmark search + InjectKnownSpells splice are gone.)

		// Serializes a stat block to its wire bytes: [u32 Marker0][u32 Marker1][u32 BlockType][u32 x Values][Trailer]
		// (mirrors FullCharSnapshotSubClass.WritePostAccessBlock). 12 + Values.Count*4 + Trailer bytes - 1365 for the fresh block.
		public static byte[] Serialize(SubClassPostAccessBlock block)
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

		// The fresh-skeleton baseline value for a stat id (0 if absent). Used to reset the volatile slots above.
		public static uint SkeletonValueOf(uint statId)
		{
			foreach (var (id, value) in StatList1)
			{
				if ((uint)id == statId)
					return value;
			}

			foreach (var (id, value) in StatList2Half)
			{
				if ((uint)id == statId)
					return value;
			}

			return 0;
		}

		private static bool TryReadStatList(ReadOnlySpan<byte> body, int off, out List<(uint Id, uint Value)> list, out int end)
		{
			list = null;
			end = off;
			var count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(off, 4));
			if (count == 0 || count % StatList.Frame != 0)
				return false;

			var n = (int)(count / StatList.Frame) - 1;
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
	}
}
