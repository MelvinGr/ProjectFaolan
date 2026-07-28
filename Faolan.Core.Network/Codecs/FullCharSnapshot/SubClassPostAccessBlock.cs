using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The snapshot's post-access-window block: a flat u32 view over the tail region, skipping its first three
	// framing words. `Values` therefore spans List F, the doubled StatList_t and everything after it - see
	// SnapshotTailRegion for the region's actual grammar.
	//
	// ⚠️ ADDRESSED BY STAT ID, not by slot (#393). Every stat in this block is a [u32 statId][u32 value] pair and
	// all 22,348 ids across the corpus are defined CharacterStatId values, so a value is FOUND rather than counted
	// to. This class used to carry a 150-member `CharacterValueIndex` enum of `UnknownNN` slots plus hardcoded
	// indexes (21, 56, 60, 62, 66, ...) and a `+148` copy rule; the enum's names were early-dev guesses and three
	// of them were wrong in a way that only the ids could show:
	//
	//   slot [21]/[56] "HP base"          is HealthMax    (id 1)
	//   slot [23]/[54] "HP displayed"     is HealthCurrent(id 27)
	//   slot [60]/[64] "displayed MAX mana" is ManaCurrent (id 506)   <- named the exact opposite
	//   slot [62]      "~6-lower secondary" is ManaMax     (id 507)   <- likewise
	//   slot [66]      "stamina secondary" is StaminaMax   (id 505)
	//   slot [68]      "stamina displayed" is StaminaCurrent (id 504)
	//   slot [178]     "PvpDeathsOverall" is Deaths        (id 658)
	//
	// The `+148` rule was the doubled stat table (SnapshotTailRegion: two identical copies of the same 74-pair run),
	// and writing by id covers it without knowing that - SetStat writes EVERY occurrence, which is 2-4 slots
	// depending on the stat, and is what keeps the sheet internally consistent (#4's suspected cause was a setter
	// that touched one slot and left its siblings on the captured template's values).
	public sealed class SubClassPostAccessBlock
	{
		public List<uint> Values { get; init; } = [];

		// Where the [statId][value] tables sit inside `Values`, as (start, wordCount) pairs - the stride-8 lists of
		// the region walk (SnapshotTailRegion). A stat is looked up PAIR-ALIGNED inside these and nowhere else.
		//
		// ⚠️ Scanning the flat array instead is not a smaller version of this, it is wrong: HealthMax's id is 1, and
		// the word 1 appears all over the array as ordinary data, so "the slot after any word equal to the id" wrote
		// 9000 into five unrelated places on the beach template. The tables are where ids are ids.
		public List<(int Start, int Words)> StatTables { get; init; } = [];

		// Whether the block carries the doubled stat table. Kept because callers assert on it; the doubling itself
		// no longer needs to be KNOWN to write a stat - the two copies are both inside the same table and both
		// pair-aligned, so a pair-aligned scan writes them without a +148 rule.
		public bool HasRepeatedStatTable { get; init; }

		public byte[] Trailer { get; init; } = [];

		public uint BlockType { get; init; }

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

		public uint Marker0 { get; init; }

		public uint Marker1 { get; init; }

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

		// A stat's value is the slot immediately after its id. The first occurrence answers; SetStat writes them all.
		public uint GetStat(CharacterStatId stat)
		{
			return GetStat((uint)stat);
		}

		public uint GetStat(uint statId)
		{
			foreach (var (start, words) in StatTables)
			{
				for (var i = start; i + 1 < start + words && i + 1 < Values.Count; i += 2)
					if (Values[i] == statId)
						return Values[i + 1];
			}

			return 0;
		}

		public void SetStat(CharacterStatId stat, uint value)
		{
			SetStat((uint)stat, value);
		}

		// Sets the value at every occurrence of the id ACROSS THE TABLES, which is what keeps the sheet consistent:
		// a stat appears in the small leading table, in the stat table, and again in that table's identical second
		// copy. Used to derive character stats (Level 0x36, Exp 0x34, MaxExp 0x15E) from the character rather than
		// carrying the captured template's values.
		public void SetStat(uint statId, uint value)
		{
			foreach (var (start, words) in StatTables)
			{
				for (var i = start; i + 1 < start + words && i + 1 < Values.Count; i += 2)
				{
					if (Values[i] == statId)
						Values[i + 1] = value;
				}
			}
		}
	}
}
