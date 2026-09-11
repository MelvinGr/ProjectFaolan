using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The fresh-character stat-block skeleton - the <see cref="FullCharSnapshotSubClass" />'s post-access block -
	///     authored from typed data rather than from a captured template.
	/// </summary>
	/// <remarks>
	///     Of the 338 values, only ~14 distinct slots are character- or map-derived (class HP/Mana/Stamina plus hash, XP,
	///     the playfield token and position, one session slot); the other 313 are this fixed baseline, identical for every
	///     fresh character. <see cref="CreateFreshBlock" /> returns a post-access block seeded with the skeleton,
	///     <see cref="SnapshotStatSheet" /> authors the character-derived slots by stat id before the block is built, and
	///     the per-class vitals ride the id-addressed setters after it.
	/// </remarks>
	public static class SubClassStatBlock
	{
		// StatList#1's count word, and the landmark ExtractFromSnapshot scans for. It is not a type tag: it is
		// `(displayedStats + 1)` frame units, the ordinary ListFrame count. Written as the arithmetic so resizing StatList1
		// moves the scan target with it instead of silently looking for the old list's length.
		public static uint BlockType => ListFrame.ForCount(StatList1.Length);

		// The stat slot whose value is the playfield-instance token: in the snapshot the token is framed by the stat ids
		// 0xBF..0x40B (see N3ToClientQuitCodec). This is session and map state rather than a character stat - the snapshot is
		// built with the live zone's minted token, which FullCharSnapshotSubClass passes in, never a captured one. Replaying
		// a stale token hides the entire in-client abilities tab and mis-scopes despawns. SkeletonValueOf is the fresh or
		// fallback value when no live token is given.
		public const uint PlayfieldTokenStatId = (uint)CharacterStatId.PlayfieldToken;

		// The number of stat pairs a full sheet carries: StatList#1 (the displayed stats) plus StatList#2's single copy.
		public static int SheetSize => StatList1.Length + StatList2Half.Length;

		// The two lists' sizes, exposed so a size mismatch can say which half is short.
		public static int StatList1Size => StatList1.Length;
		public static int StatList2Size => StatList2Half.Length;


		// The block is two count-framed StatList_t (StatPair_t = [statId, value];
		// docs/re/fullplayer-snapshot-reader.md regions 9-10). StatList#1 is the 17 displayed stats, and its count word is
		// the BlockType field. StatList#2 is the full sheet: 74 unique pairs written twice (base plus current copy = 148
		// pairs). Every value is a named (statId, value): the character-derived stats (Level 0x36, XP 0x34, MaxXP 0x15E, HP
		// 0x1B, mana, stamina) are overwritten from the character by the caller, and the rest are the fresh baseline
		// (armour, resists and the like that are not modelled yet, carried as their fixed defaults).
		private static readonly (CharacterStatId Id, uint Value)[] StatList1 =
		[
			(CharacterStatId.TempSaveTeamId, 0x0), (CharacterStatId.TempSavePlayfield, 0x0), (CharacterStatId.TempSaveX, 0x0), (CharacterStatId.TempSaveY, 0x0), (CharacterStatId.Flags, 0x1),
			(CharacterStatId.Features, 0x0), (CharacterStatId.PreviousMovementMode, 0x2), (CharacterStatId.CurrentMovementMode, 0x2), (CharacterStatId.PlayfieldToken, 0x4FE428), (CharacterStatId.DeathEffectXp, 0x0),
			(CharacterStatId.HealthMax, 0x2328), (CharacterStatId.HealthCurrent, 0x2648), (CharacterStatId.Level, 0x1), (CharacterStatId.Exp, 0x0), (CharacterStatId.MaxExp, 0x64),
			(CharacterStatId.ExpCurrentLevelBase, 0x0), (CharacterStatId.HeroicMode, 0x0)
		];

		// One half of StatList#2; the client carries it twice (base plus current). Its last two slots (Unknown63 then
		// KillingBlows) are the tail the known-spell list is built right after (CreateBlock).
		private static readonly (CharacterStatId Id, uint Value)[] StatList2Half =
		[
			(CharacterStatId.ExpandBankInventory, 0x0), (CharacterStatId.NanoInterval, 0x0), (CharacterStatId.Timestamp, 0x0), (CharacterStatId.Unknown15, 0x0), (CharacterStatId.PvpMaxExp, 0xDAC),
			(CharacterStatId.MurderPoints, 0x0), (CharacterStatId.ShieldAllocation, 0x6F), (CharacterStatId.FeatResets, 0x0), (CharacterStatId.XpKillRange, 0x8), (CharacterStatId.HealthCurrent, 0x2648),
			(CharacterStatId.HealthMax, 0x2328), (CharacterStatId.Intelligence, 0xA), (CharacterStatId.ManaCurrent, 0x0), (CharacterStatId.ManaMax, 0x0), (CharacterStatId.ManaCurrent, 0x0),
			(CharacterStatId.StaminaMax, 0x59D8), (CharacterStatId.StaminaCurrent, 0x5C30), (CharacterStatId.Strength, 0xA), (CharacterStatId.Icon, 0x0), (CharacterStatId.Mesh, 0x0),
			(CharacterStatId.DeadTimer, 0x0), (CharacterStatId.Sex, 0x3), (CharacterStatId.MaxExp, 0x64), (CharacterStatId.ExpCurrentLevelBase, 0x0), (CharacterStatId.Level, 0x1),
			(CharacterStatId.PvpLevel, 0x0), (CharacterStatId.PvpExp, 0x0), (CharacterStatId.Exp, 0x0), (CharacterStatId.PreviousHealth, 0x3E8), (CharacterStatId.Played, 0xBA),
			(CharacterStatId.Side, 0x0), (CharacterStatId.AwardedOmniTekTokens, 0x0), (CharacterStatId.GmLevel, 0x0), (CharacterStatId.Expansions, 0x0), (CharacterStatId.PlayerId, 0x59EA506E),
			(CharacterStatId.PowerSessionRemaining, 0x14), (CharacterStatId.SkillPoints, 0x0), (CharacterStatId.Skulk, 0x0), (CharacterStatId.RunSpeed, 0x0), (CharacterStatId.Perception, 0x0),
			(CharacterStatId.Hiding, 0x0), (CharacterStatId.Resilence, 0x0), (CharacterStatId.TrapDisarm, 0x0), (CharacterStatId.Climbing, 0x0), (CharacterStatId.MagicalResist, 0x64),
			(CharacterStatId.MagicRating, 0x64), (CharacterStatId.AttackRating, 0x64), (CharacterStatId.DefenseRating, 0x64), (CharacterStatId.MemorizeDelay, 0x0), (CharacterStatId.ElderFrostSpecialization, 0x0),
			(CharacterStatId.Unknown46, 0x0), (CharacterStatId.PetPoints, 0x0), (CharacterStatId.Unknown48, 0x0), (CharacterStatId.Money, 0x0), (CharacterStatId.PvPMatchesAbandonedSeasonal, 0x0),
			(CharacterStatId.MinigamesAbandoned, 0x0), (CharacterStatId.PvPSiegeMatchesLostSeasonal, 0x0), (CharacterStatId.SiegeLosses, 0x0), (CharacterStatId.PvPSiegeMatchesWonSeasonal, 0x0), (CharacterStatId.SiegeWins, 0x0),
			(CharacterStatId.PvPCTFMatchesLostSeasonal, 0x0), (CharacterStatId.CaptureSkullLosses, 0x0), (CharacterStatId.PvPCTFMatchesWonSeasonal, 0x0), (CharacterStatId.CaptureSkullWins, 0x0), (CharacterStatId.PvPAnnihilationMatchesLostSeasonal, 0x0),
			(CharacterStatId.AnnihilationLosses, 0x0), (CharacterStatId.PvPAnnihilationMatchesWonSeasonal, 0x0), (CharacterStatId.AnnihilationWins, 0x0), (CharacterStatId.PvPFatalitiesSeasonal, 0x0), (CharacterStatId.Fatalities, 0x0),
			(CharacterStatId.PvPTotalDeathsSeasonal, 0x0), (CharacterStatId.PvPTotalDeaths, 0x0), (CharacterStatId.PvPTotalKillsSeasonal, 0x0), (CharacterStatId.PvPTotalKills, 0x0)
		];

		// The sheet's own stat ids - what a login snapshot carries and nothing else. CharacterStats can legitimately hold
		// more than the sheet (the daily-challenge counters land there, ids 1000066 and up, and retail pushes those on the
		// live StatUpdate channel rather than in the snapshot - every captured sheet is exactly these 91), so a sheet loader
		// filters the table to this set rather than assuming the table is the sheet.
		//
		// Declared after the two arrays it reads: static fields initialize in declaration order, and above them this would
		// run on nulls.
		public static readonly IReadOnlySet<uint> SheetStatIds =
			StatList1.Concat(StatList2Half).Select(p => (uint)p.Id).ToHashSet();

		// The block's wide tail: one entry, all-zero but for its flag byte. The pair tail is empty for a fresh character.
		private static readonly SnapshotTailWideEntry FreshWideTail = new SnapshotTailWideEntry(0x01, 0, 0);

		// Builds a stat block from a full stat sheet: `sheet` is StatList#1 (its first StatList1.Length pairs, the displayed
		// stats) followed by StatList#2 (the remaining pairs, one copy). StatList#2 is emitted twice on the wire (base plus
		// current), so the block is [17 displayed pairs][74 sheet pairs x2][known-spell list][tails]. A null known list
		// becomes an empty one rather than an absent one: a login snapshot always carries the list, and the empty form is
		// byte-identical to the captured beach skeleton.
		public static SubClassPostAccessBlock CreateBlock(IReadOnlyList<(uint Id, uint Value)> sheet, KnownSpellList? known = null)
		{
			var displayedCount = StatList1.Length;

			var displayed = new StatList();
			for (var i = 0; i < displayedCount; i++)
				displayed.Pairs.Add(sheet[i]);

			// Both copies, because both are on the wire and a stat is only consistent when both carry the same value.
			var full = new StatList();
			for (var copy = 0; copy < 2; copy++)
			for (var i = displayedCount; i < sheet.Count; i++)
				full.Pairs.Add(sheet[i]);

			return new SubClassPostAccessBlock
			{
				Displayed = displayed,
				Sheet = full,
				KnownSpells = known ?? new KnownSpellList(),
				WideTail = [FreshWideTail],
				PairTail = []
			};
		}

		public static SubClassPostAccessBlock CreateFreshBlock(KnownSpellList? known = null)
		{
			// The fresh skeleton is the baseline sheet run through the generic builder - byte-identical to the captured beach
			// block (proven by BeachTailsFromScratchTests).
			return CreateBlock(FreshSheet(), known);
		}

		// Scans a raw snapshot body for the stat block and returns its (statId, value) pairs in order: StatList#1 (the 17
		// displayed stats) then StatList#2's first copy (the 74 sheet stats; the sheet is written twice on the wire, base
		// plus current, and one copy is kept). Null when not found. This is the inverse of the generated block - the importer
		// uses it to store a character's real stat sheet from its login snapshot.
		public static List<(uint Id, uint Value)>? ExtractFromSnapshot(ReadOnlySpan<byte> body)
		{
			for (var i = 0; i + 8 <= body.Length; i++)
			{
				if (body.ReadU32Be(i) != BlockType)
					continue; // not a StatList#1 count word (0x46F2)

				if (!TryReadStatList(body, i, out var displayed, out var afterDisplayed) || displayed.Pairs.Count != StatList1.Length)
					continue;

				// Reject a spurious match that is really a stat value: the displayed stat ids are all small.
				if (displayed.Pairs.Exists(p => p.Id >= 0x700))
					continue;

				if (!TryReadStatList(body, afterDisplayed, out var sheet, out _) || sheet.Pairs.Count < StatList2Half.Length * 2)
					continue;

				var pairs = new List<(uint, uint)>(StatList1.Length + StatList2Half.Length);
				pairs.AddRange(displayed.Pairs);
				pairs.AddRange(sheet.Pairs.GetRange(0, StatList2Half.Length)); // first copy = the 74 unique sheet stats
				return pairs;
			}

			return null;
		}

		// The fresh-character baseline stat sheet (StatList#1's 17 displayed pairs plus StatList#2's 74 sheet pairs, one
		// copy), as a fresh mutable list - the caller patches the character-derived slots on it before building, so nothing
		// has to SetStat the finished block, which would also scan the inline known list.
		public static List<(uint Id, uint Value)> FreshSheet()
		{
			var sheet = new List<(uint, uint)>(SheetSize);
			foreach (var (id, value) in StatList1)
				sheet.Add(((uint)id, value));
			foreach (var (id, value) in StatList2Half)
				sheet.Add(((uint)id, value));
			return sheet;
		}

		// CreateBlock builds the known-spell list inline at the block tail.

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

		// Reads one StatList_t at `off` through its codec: the scan only decides where a list starts, never how one is laid
		// out. The codec is strict, so a frame word that is not really a list throws and the scan moves on.
		private static bool TryReadStatList(ReadOnlySpan<byte> body, int off, [NotNullWhen(true)] out StatList? list, out int end)
		{
			list = null;
			end = off;

			var r = new SpanReader(body[off..], nameof(SubClassStatBlock));
			try
			{
				list = StatListCodec.Read(ref r);
			}
			catch (CodecParseException)
			{
				return false;
			}

			end = off + r.Position;
			return true;
		}
	}
}
