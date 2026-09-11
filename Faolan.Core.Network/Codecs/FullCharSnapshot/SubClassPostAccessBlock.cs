using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The snapshot's post-access-window block: the two <c>StatList_t</c> the character's sheet is, its known-spell
	///     list and the two counter lists that close the region. See <see cref="SubClassPostAccessBlockCodec" /> for the
	///     grammar and where in the client's reader each part comes from.
	/// </summary>
	/// <remarks>
	///     Stats are addressed by stat id, never by slot. Every stat is a <c>[u32 statId][u32 value]</c> pair and all
	///     22,348 ids across the corpus are defined <see cref="CharacterStatId" /> values, so a value is found rather than
	///     counted to. <see cref="SetStat(uint,uint)" /> writes every occurrence of an id, which is what keeps the sheet
	///     internally consistent when a stat appears in both the displayed list and both halves of the doubled sheet.
	/// </remarks>
	public sealed class SubClassPostAccessBlock
	{
		// StatList#1 - the 17 displayed stats (Level 0x36, XP 0x34, MaxXP 0x15E, HP 0x1B, …).
		public StatList Displayed { get; init; } = new StatList();

		// StatList#2 - the full sheet, 74 unique pairs written twice (base plus current copy = 148 pairs). Both copies are
		// held, because both are on the wire and a stat is only consistent when both carry the same value.
		public StatList Sheet { get; init; } = new StatList();

		// The spellbook list. Null in the 21 create-editor snapshots, which end at Sheet; an empty list is a different thing
		// on the wire, carrying its frame word, and every login snapshot carries one.
		public KnownSpellList? KnownSpells { get; init; }

		// The two counter lists that close the region, plainly counted rather than frame-counted.
		public List<SnapshotTailWideEntry> WideTail { get; init; } = [];

		public List<SnapshotTailPair> PairTail { get; init; } = [];

		// Whether Sheet carries the doubled run - two identical halves, on all 151 captures. Computed rather than carried:
		// it is a fact about the pairs, and a block built from a sheet has it by construction.
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

		// The vitals, in player-facing units. The wire scale (x100 for every vital) comes from CharacterStatId.GetScale, the
		// same table CharacterStatBlock uses for the live stat packet.
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
		///     The raw to-next-level cost, unscaled. Experience itself is <see cref="CharacterStatId.Exp" />, authored by id
		///     in <see cref="SnapshotStatSheet" />.
		/// </summary>
		public uint MaxExp => GetStat(CharacterStatId.MaxExp);

		public uint Deaths
		{
			get => GetStat(CharacterStatId.PvPTotalDeaths);
			set => SetStat(CharacterStatId.PvPTotalDeaths, value);
		}

		// Authors the whole per-class vitals block: current and max for each vital, x100. Pass
		// CharacterMaxStats.SnapshotVitals().
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

		// A stat's value, from whichever list names it. The displayed list answers first; a stat only in the sheet falls
		// through to it.
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

		// Sets the value at every occurrence of the id across both lists, which is what keeps the sheet consistent: a stat
		// appears in the displayed list, in the sheet, and again in the sheet's identical second copy. Used to derive
		// character stats (Level 0x36, Exp 0x34, MaxExp 0x15E) from the character rather than carrying a captured template's
		// values. The known-spell list is a separate field, so a spell id that happens to equal a stat id cannot be written
		// over.
		public void SetStat(uint statId, uint value)
		{
			Displayed.Set(statId, value);
			Sheet.Set(statId, value);
		}
	}

	/// <summary>
	///     One element of the base reader's trailing list (FUN_00797440's tail): a byte and two 8-byte reads.
	/// </summary>
	public readonly record struct SnapshotTailWideEntry(byte Flag, ulong A, ulong B);

	/// <summary>
	///     One element of FullPlayerCharacterIIR's trailing pair list. Its ids are RDB template ids (0xF426A = 1000042,
	///     the feat id-map), so it reads as a per-character counter table.
	/// </summary>
	public readonly record struct SnapshotTailPair(uint Id, uint Value);
}
