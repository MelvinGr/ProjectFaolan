using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot's post-access-window block: the two StatList_t the character's sheet is, its known-spell list and
	// the two counter lists that close the region. See SubClassPostAccessBlockCodec for the grammar and where in the
	// client's reader each part comes from.
	//
	// ⚠️ ADDRESSED BY STAT ID, not by slot (#393). Every stat is a [u32 statId][u32 value] pair and all 22,348 ids
	// across the corpus are defined CharacterStatId values, so a value is FOUND rather than counted to. This class
	// used to carry a flat `List<uint> Values` plus a 150-member `CharacterValueIndex` enum of `UnknownNN` slots and
	// hardcoded indexes (21, 56, 60, 62, 66, ...) with a `+148` copy rule; the enum's names were early-dev guesses
	// and three of them were wrong in a way that only the ids could show:
	//
	//   slot [21]/[56] "HP base"          is HealthMax    (id 1)
	//   slot [23]/[54] "HP displayed"     is HealthCurrent(id 27)
	//   slot [60]/[64] "displayed MAX mana" is ManaCurrent (id 506)   <- named the exact opposite
	//   slot [62]      "~6-lower secondary" is ManaMax     (id 507)   <- likewise
	//   slot [66]      "stamina secondary" is StaminaMax   (id 505)
	//   slot [68]      "stamina displayed" is StaminaCurrent (id 504)
	//   slot [178]     "PvpDeathsOverall" is Deaths        (id 658)
	//
	// The `+148` rule was Sheet carrying the same 74 pairs twice, and writing by id covers it without knowing that -
	// SetStat writes EVERY occurrence, which is what keeps the sheet internally consistent (#4's suspected cause was
	// a setter that touched one slot and left its siblings on the captured template's values).
	public sealed class SubClassPostAccessBlock
	{
		// StatList#1 - the 17 DISPLAYED stats (Level 0x36 / XP 0x34 / MaxXP 0x15E / HP 0x1B / ...).
		public StatList Displayed { get; init; } = new();

		// StatList#2 - the full sheet, 74 unique pairs written TWICE (base + current copy = 148 pairs). Both copies
		// are held, because both are on the wire and a stat is only consistent when both carry the same value.
		public StatList Sheet { get; init; } = new();

		// The spellbook list (#74). NULL in the 21 create-editor snapshots, which end at Sheet - an EMPTY list is a
		// different thing on the wire (its 0x3F1 frame word) and every login snapshot carries one.
		public KnownSpellList? KnownSpells { get; init; }

		// The two counter lists that close the region, plainly counted rather than 0x3F1-framed.
		public List<SnapshotTailWideEntry> WideTail { get; init; } = [];

		public List<SnapshotTailPair> PairTail { get; init; } = [];

		// Whether Sheet carries the doubled run - two identical halves, on all 151 captures. Computed rather than
		// carried: it is a fact about the pairs, and a block built from a sheet has it by construction.
		public bool HasRepeatedStatTable
		{
			get
			{
				var pairs = Sheet.Pairs;
				if (pairs.Count < 4 || pairs.Count % 2 != 0)
					return false;

				var half = pairs.Count / 2;
				for (var i = 0; i < half; i++)
				{
					if (pairs[i] != pairs[half + i])
						return false;
				}

				return true;
			}
		}

		// The vitals, in PLAYER-FACING units. The wire scale (x100 for every vital) comes from
		// CharacterStatId.GetScale, the same table CharacterStatBlock uses for the live stat packet - it was written
		// out as a literal `* 100` in six places here, which is the same knowledge kept in two spellings.
		public uint CurrentHp
		{
			get => GetScaled(CharacterStatId.HealthCurrent);
			set => SetScaled(CharacterStatId.HealthCurrent, value);
		}

		public uint CurrentMana
		{
			get => GetScaled(CharacterStatId.ManaCurrent);
			set => SetScaled(CharacterStatId.ManaCurrent, value);
		}

		public uint CurrentStamina
		{
			get => GetScaled(CharacterStatId.StaminaCurrent);
			set => SetScaled(CharacterStatId.StaminaCurrent, value);
		}

		/// <summary>
		///     The raw to-next-level cost, unscaled (#447). It used to be a <c>CurrentXp</c> accessor with a "factor
		///     10", which produced the right bytes for exactly one case by coincidence: the beach template is a level-1
		///     character whose MaxExp is 100 and the golden carried <c>Experience = 10</c>. Experience itself is
		///     <see cref="CharacterStatId.Exp" />, authored by id in <see cref="SnapshotStatSheet" />.
		/// </summary>
		public uint MaxExp => GetStat(CharacterStatId.MaxExp);

		public uint Deaths
		{
			get => GetStat(CharacterStatId.Deaths);
			set => SetStat(CharacterStatId.Deaths, value);
		}

		// Authors the whole per-class vitals block (#4/#186): current AND max for each vital, x100. The parameters
		// used to be named hp/hpBase..., which read as "displayed and its base"; the ids say they are current and
		// MAX. Pass CharacterMaxStats.SnapshotVitals().
		public void ApplyClassVitals(uint hp, uint hpMax, uint mana, uint manaMax, uint stamina, uint staminaMax)
		{
			SetScaled(CharacterStatId.HealthCurrent, hp);
			SetScaled(CharacterStatId.HealthMax, hpMax);
			SetScaled(CharacterStatId.ManaCurrent, mana);
			SetScaled(CharacterStatId.ManaMax, manaMax);
			SetScaled(CharacterStatId.StaminaCurrent, stamina);
			SetScaled(CharacterStatId.StaminaMax, staminaMax);
		}

		// Player-facing value in, wire value out (and back), through the shared scale table.
		public uint GetScaled(CharacterStatId stat)
		{
			return GetStat(stat) / stat.GetScale();
		}

		public void SetScaled(CharacterStatId stat, uint value)
		{
			SetStat(stat, value * stat.GetScale());
		}

		// A stat's value, from whichever list names it. The displayed list answers first; a stat only in the sheet
		// falls through to it.
		public uint GetStat(CharacterStatId stat)
		{
			return GetStat((uint)stat);
		}

		public uint GetStat(uint statId)
		{
			return Displayed.Get(statId) ?? Sheet.Get(statId) ?? 0;
		}

		public void SetStat(CharacterStatId stat, uint value)
		{
			SetStat((uint)stat, value);
		}

		// Sets the value at EVERY occurrence of the id across BOTH lists, which is what keeps the sheet consistent: a
		// stat appears in the displayed list, in the sheet, and again in the sheet's identical second copy. Used to
		// derive character stats (Level 0x36, Exp 0x34, MaxExp 0x15E) from the character rather than carrying the
		// captured template's values. The known-spell list is a separate field, so - unlike the flat view this
		// replaced - a spell id that happens to equal a stat id cannot be written over.
		public void SetStat(uint statId, uint value)
		{
			Displayed.Set(statId, value);
			Sheet.Set(statId, value);
		}
	}

	// One element of the base reader's trailing list (FUN_00797440's tail): a byte and two 8-byte reads.
	public readonly record struct SnapshotTailWideEntry(byte Flag, ulong A, ulong B)
	{
		public const int ByteCount = 17;
	}

	// One element of FullPlayerCharacterIIR's trailing pair list. Its ids are RDB template ids (0xF426A = 1000042,
	// the feat id-map), so it reads as a per-character counter table.
	public readonly record struct SnapshotTailPair(uint Id, uint Value);
}
