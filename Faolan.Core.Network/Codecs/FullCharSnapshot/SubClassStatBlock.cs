using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The fresh-character STAT-block skeleton (the FullCharSnapshotSubClass PostAccessBlock), relocated out of
	// BeachTailsTemplate.bin so the stat region can be authored without the .bin (#186). Decoded by
	// tools/capture/decode_statblock.py: of the 338 values, only ~14 distinct slots are character/map-derived (class
	// HP/Mana/Stamina + hash, XP, the playfield token + position, one session slot) - the other 313 are this
	// FIXED baseline, identical for every fresh character. CreateFreshBlock() returns a PostAccessBlock seeded with
	// the skeleton; SnapshotStatSheet authors the character-derived slots by stat id before the block is built, and
	// the per-class vitals ride the positional setters after it - so generating == relocating
	// this baseline (the genuinely-fixed fresh-char stats) + the already-computed patches.
	public static class SubClassStatBlock
	{
		public const int RepeatedRunLength = 148;
		public const int RepeatedRunStart = 35; // the 35..182 sub-block is copied to 183..330 (Length 148)
		public const uint Marker = ListFrame.Unit; // an EMPTY count-framed list, twice

		// StatList#1's count word - and the field the parser scans for. It is NOT a type tag: it is
		// `(displayedStats + 1) * 0x3F1` (0x46F2 for 17), the ordinary ListFrame count. Written as the arithmetic so
		// resizing StatList1 moves the scan target with it instead of silently looking for the old list's length.
		public static uint BlockType => ListFrame.ForCount(StatList1.Length);

		// The stat slot whose VALUE is the playfield-instance token: in the snapshot the token is framed by the stat ids
		// 0xBF..0x40B (see WorldDespawn). This is SESSION/MAP state, NOT a character stat - the snapshot is BUILT with the
		// live zone's minted token (FullCharSnapshotSubClass passes it in), never a captured one. Replaying a stale token
		// HIDES the entire in-client abilities tab (bisected on NecroL80's level-80 sheet, 2026-07-04) and mis-scopes
		// despawns. SkeletonValueOf (the beach 0x4FE428) is the fresh/fallback value when no live token is given.
		public const uint PlayfieldTokenStatId = (uint)CharacterStatId.PlayfieldToken;

		// The number of stat pairs a full sheet carries: StatList#1 (the displayed stats) + StatList#2's single copy.
		public static int SheetSize => StatList1.Length + StatList2Half.Length;

		// The two lists' sizes, exposed so a size mismatch can say WHICH half is short.
		public static int StatList1Size => StatList1.Length;
		public static int StatList2Size => StatList2Half.Length;

		// The block is TWO 0x3F1-framed StatList_t (StatPair_t = [statId, value]; docs/re/fullplayer-snapshot-reader.md
		// regions 9-10). StatList#1 = the 17 DISPLAYED stats (its count word IS the BlockType field, (17+1)*0x3F1 =
		// 0x46F2). StatList#2 = the full sheet: 74 UNIQUE pairs written TWICE (base + current copy = 148 pairs, the old
		// "repeated run"). Every value is a named (statId, value) - the character-derived stats (Level 0x36, XP 0x34,
		// MaxXP 0x15E, HP 0x1B, mana, stamina) are overwritten from the character by the caller; the rest are the fresh
		// baseline (armour/resists/... we do not model yet, so carried as their fixed defaults). No opaque blob.
		private static readonly (CharacterStatId Id, uint Value)[] StatList1 =
		[
			(CharacterStatId.TempSaveTeamId, 0x0), (CharacterStatId.TempSavePlayfield, 0x0), (CharacterStatId.TempSaveX, 0x0), (CharacterStatId.TempSaveY, 0x0), (CharacterStatId.Flags, 0x1),
			(CharacterStatId.Features, 0x0), (CharacterStatId.PreviousMovementMode, 0x2), (CharacterStatId.CurrentMovementMode, 0x2), (CharacterStatId.PlayfieldToken, 0x4FE428), (CharacterStatId.DeathEffectXp, 0x0),
			(CharacterStatId.HealthMax, 0x2328), (CharacterStatId.HealthCurrent, 0x2648), (CharacterStatId.Level, 0x1), (CharacterStatId.Exp, 0x0), (CharacterStatId.MaxExp, 0x64),
			(CharacterStatId.ExpCurrentLevelBase, 0x0), (CharacterStatId.ChosenGod, 0x0)
		];

		// One half of StatList#2; the client carries it twice (base + current). Its last two slots (Unknown63 then
		// KillingBlows) are the tail the known-spell list is built right after (CreateBlock).
		private static readonly (CharacterStatId Id, uint Value)[] StatList2Half =
		[
			(CharacterStatId.Unknown12, 0x0), (CharacterStatId.NanoInterval, 0x0), (CharacterStatId.Timestamp, 0x0), (CharacterStatId.Unknown15, 0x0), (CharacterStatId.PvpMaxExp, 0xDAC),
			(CharacterStatId.MurderPoints, 0x0), (CharacterStatId.ShieldAllocation, 0x6F), (CharacterStatId.FeatResets, 0x0), (CharacterStatId.XpKillRange, 0x8), (CharacterStatId.HealthCurrent, 0x2648),
			(CharacterStatId.HealthMax, 0x2328), (CharacterStatId.Intelligence, 0xA), (CharacterStatId.ManaCurrent, 0x0), (CharacterStatId.ManaMax, 0x0), (CharacterStatId.ManaCurrent, 0x0),
			(CharacterStatId.StaminaMax, 0x59D8), (CharacterStatId.StaminaCurrent, 0x5C30), (CharacterStatId.Strength, 0xA), (CharacterStatId.Icon, 0x0), (CharacterStatId.Mesh, 0x0),
			(CharacterStatId.DeadTimer, 0x0), (CharacterStatId.Sex, 0x3), (CharacterStatId.MaxExp, 0x64), (CharacterStatId.ExpCurrentLevelBase, 0x0), (CharacterStatId.Level, 0x1),
			(CharacterStatId.PvpLevel, 0x0), (CharacterStatId.PvpExp, 0x0), (CharacterStatId.Exp, 0x0), (CharacterStatId.PreviousHealth, 0x3E8), (CharacterStatId.Played, 0xBA),
			(CharacterStatId.Side, 0x0), (CharacterStatId.AwardedOmniTekTokens, 0x0), (CharacterStatId.GmLevel, 0x0), (CharacterStatId.Expansions, 0x0), (CharacterStatId.PlayerId, 0x59EA506E),
			(CharacterStatId.LastKillNumber, 0x14), (CharacterStatId.Unknown32, 0x0), (CharacterStatId.Skulk, 0x0), (CharacterStatId.RunSpeed, 0x0), (CharacterStatId.Perception, 0x0),
			(CharacterStatId.Hiding, 0x0), (CharacterStatId.Resilence, 0x0), (CharacterStatId.TrapDisarm, 0x0), (CharacterStatId.Climbing, 0x0), (CharacterStatId.MagicalResist, 0x64),
			(CharacterStatId.MagicRating, 0x64), (CharacterStatId.AttackRating, 0x64), (CharacterStatId.DefenseRating, 0x64), (CharacterStatId.MemorizeDelay, 0x0), (CharacterStatId.ElderFrostSpecialization, 0x0),
			(CharacterStatId.Unknown46, 0x0), (CharacterStatId.PetPoints, 0x0), (CharacterStatId.Unknown48, 0x0), (CharacterStatId.Money, 0x0), (CharacterStatId.Unknown50, 0x0),
			(CharacterStatId.MinigamesAbandoned, 0x0), (CharacterStatId.Unknown52, 0x0), (CharacterStatId.SiegeLosses, 0x0), (CharacterStatId.Unknown54, 0x0), (CharacterStatId.SiegeWins, 0x0),
			(CharacterStatId.Unknown56, 0x0), (CharacterStatId.CaptureSkullLosses, 0x0), (CharacterStatId.Unknown57, 0x0), (CharacterStatId.CaptureSkullWins, 0x0), (CharacterStatId.Unknown58, 0x0),
			(CharacterStatId.AnnihilationLosses, 0x0), (CharacterStatId.Unknown59, 0x0), (CharacterStatId.AnnihilationWins, 0x0), (CharacterStatId.Unknown60, 0x0), (CharacterStatId.Fatalities, 0x0),
			(CharacterStatId.Unknown62, 0x0), (CharacterStatId.Deaths, 0x0), (CharacterStatId.Unknown63, 0x0), (CharacterStatId.Kills, 0x0)
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
		public static SubClassPostAccessBlock CreateBlock(IReadOnlyList<(uint Id, uint Value)> sheet, KnownSpellList? known = null)
		{
			var displayedCount = StatList1.Length;
			var sheetCount = sheet.Count - displayedCount;

			var values = new List<uint>(338);
			for (var i = 0; i < displayedCount; i++)
			{
				values.Add(sheet[i].Id);
				values.Add(sheet[i].Value);
			}

			values.Add(ListFrame.ForCount(sheetCount * 2)); // StatList#2 count - it carries BOTH copies (0x24B45 for 74 pairs)
			for (var copy = 0; copy < 2; copy++)
			for (var i = displayedCount; i < sheet.Count; i++)
			{
				values.Add(sheet[i].Id);
				values.Add(sheet[i].Value);
			}

			var records = known?.Records ?? [];
			values.Add(ListFrame.ForCount(records.Count)); // known-spell list count (0x3F1 when empty)
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
				BlockType = ListFrame.ForCount(displayedCount), // StatList#1 count word = 0x46F2 for its 17 pairs
				Values = values,
				Trailer = [0x00],
				// True so HasKnownRepeatedStatTable is, and the caller's vitals patches (which write the +148 copy too)
				// fire. A FRESH sheet is built with the doubled region by construction, so this is a statement of fact
				// about what was just assembled, not a detection.
				HasRepeatedStatTable = true
			};
		}

		public static SubClassPostAccessBlock CreateFreshBlock(KnownSpellList? known = null)
		{
			// The fresh skeleton is just the baseline sheet run through the generic builder - byte-identical to the
			// captured beach block (proven by BeachTailsFromScratchTests).
			return CreateBlock(FreshSheet(), known);
		}

		// Scans a raw 0x737A6DF9 snapshot body for the stat block and returns its (statId, value) pairs in order:
		// StatList#1 (the 17 displayed stats, count word 0x46F2) then StatList#2's first copy (the 74 sheet stats - the
		// sheet is written TWICE on the wire, base + current; we keep one). Null when not found. This is the inverse of
		// the generated block - the importer uses it to store a character's REAL stat sheet from its login snapshot.
		public static List<(uint Id, uint Value)>? ExtractFromSnapshot(ReadOnlySpan<byte> body)
		{
			for (var i = 0; i + 8 <= body.Length; i++)
			{
				if (body.ReadU32Be(i) != BlockType)
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

		private static bool TryReadStatList(ReadOnlySpan<byte> body, int off, [NotNullWhen(true)] out List<(uint Id, uint Value)>? list, out int end)
		{
			list = null;
			end = off;
			if (!ListFrame.TryCountOf(body.ReadU32Be(off), out var n))
				return false;

			if (off + 4 + n * 8 > body.Length)
				return false;

			list = new List<(uint, uint)>(n);
			var p = off + 4;
			for (var i = 0; i < n; i++)
			{
				list.Add((body.ReadU32Be(p), body.ReadU32Be(p + 4)));
				p += 8;
			}

			end = p;
			return true;
		}
	}
}
