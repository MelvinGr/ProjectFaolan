using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	public sealed class SubClassPostAccessBlock
	{
		private const int RepeatedBlockEndIndex = 182;
		private const int RepeatedBlockOffset = 148;
		private const int RepeatedBlockStartIndex = 35;

		public List<uint> Values { get; init; } = [];

		// Whether the post-access stat table carries its ADJACENT REPEATED RUN - the region whose second copy sits
		// +148 words after the first, so a vitals patch has to write both (SubClassPostAccessBlock.SetValue). Only the
		// yes/no was ever consumed: this used to be a SubClassRepeatedUInt32Run object carrying StartIndex/Length/
		// RepeatCount, whose only reader was a layout-analysis class that nothing read in turn.
		public bool HasRepeatedStatTable { get; init; }

		public byte[] Trailer { get; init; } = [];

		public uint BlockType { get; init; }

		public uint CurrentHp
		{
			get => GetValue(CharacterValueIndex.CurrentHp) / 100;
			set
			{
				var scaledValue = value * 100;

				Values[(int)CharacterValueIndex.CurrentHpExtra] = scaledValue;
				SetValue(CharacterValueIndex.CurrentHp, scaledValue);
			}
		}

		public uint CurrentMana
		{
			get => GetValue(CharacterValueIndex.CurrentMana) / 100;
			set => SetValue(CharacterValueIndex.CurrentMana, value * 100);
		}

		public uint CurrentStamina
		{
			get => GetValue(CharacterValueIndex.CurrentStamina) / 100;
			set => SetValue(CharacterValueIndex.CurrentStamina, value * 100);
		}

		public uint CurrentXp
		{
			get => GetValue(CharacterValueIndex.CurrentXp) / 10;
			set => SetValue(CharacterValueIndex.CurrentXp, value * 10);
		}

		public uint Marker0 { get; init; }

		public uint Marker1 { get; init; }

		//public uint PvpXpTillNextLevel
		//{
		//	get => GetValue(CharacterValueIndex.PvpXpTillNextLevel);
		//	set => SetValue(CharacterValueIndex.PvpXpTillNextLevel, value);
		//}

		/*public uint PvpKillsOverall
		{
			get => GetValue(CharacterValueIndex.PvpKillsOverall);
			set => SetValue(CharacterValueIndex.PvpKillsOverall, value);
		}*/

		public uint PvpDeathsOverall
		{
			get => GetValue(CharacterValueIndex.PvpDeathsOverall);
			set => SetValue(CharacterValueIndex.PvpDeathsOverall, value);
		}

		public enum CharacterValueIndex : uint
		{
			// HP is special:
			// - Extra pre-block slot: 23
			// - Main repeated-block slot: 54
			// - Copy slot: 202
			CurrentHpExtra = 23,

			Unknown35 = 35,
			Unknown36 = 36,
			Unknown37 = 37,
			Unknown38 = 38,
			Unknown39 = 39,
			Unknown40 = 40,
			Unknown41 = 41,
			Unknown42 = 42,
			Unknown43 = 43,
			Unknown44 = 44, // PvpXpTillNextLevel?
			Unknown45 = 45,
			PvpXp = 46,
			Unknown47 = 47,
			Unknown48 = 48,
			Unknown49 = 49,
			Unknown50 = 50,
			Unknown51 = 51,
			Unknown52 = 52,
			Unknown53 = 53,
			CurrentHp = 54,
			Unknown55 = 55,
			Unknown56 = 56,
			Unknown57 = 57,
			Unknown58 = 58,
			Unknown59 = 59,
			Unknown60 = 60,
			Unknown61 = 61,

			// Index 62 is a SECONDARY mana value (~6 lower than displayed): retail Necro [62]=24000 vs displayed
			// 24600, Bear Shaman [62]=16500 vs displayed 17100. NOT the displayed max — see CurrentMana below.
			ManaSecondary = 62,
			Unknown63 = 63,

			// The displayed MAX mana (×100). Verified against retail LOGIN snapshots: Necro [64]=24600 (=246 mana,
			// matches the client sheet), Bear Shaman [64]=17100 (=171). Index 60 carries the same value; [62] is a
			// ~6-lower secondary. (The earlier "[64]=2276 / use 62" note came from an UNSETTLED create snapshot.)
			CurrentMana = 64,
			Unknown65 = 65,
			Unknown66 = 66,
			Unknown67 = 67,
			CurrentStamina = 68,
			Unknown69 = 69,
			Unknown70 = 70,
			Unknown71 = 71,
			Unknown72 = 72,
			Unknown73 = 73,
			Unknown74 = 74,
			Unknown75 = 75,
			Unknown76 = 76,
			Unknown77 = 77,
			Unknown78 = 78,
			Unknown79 = 79,
			CurrentXp = 80, // factor 10
			Unknown81 = 81,
			Unknown82 = 82,
			Unknown83 = 83,
			Unknown84 = 84,
			Unknown85 = 85,
			PvpLevel = 86,
			Unknown87 = 87,
			Unknown88 = 88,
			Unknown89 = 89,
			Unknown90 = 90,
			Unknown91 = 91,
			Unknown92 = 92,
			Unknown93 = 93,
			Unknown94 = 94,
			Unknown95 = 95,
			Unknown96 = 96,
			Unknown97 = 97,
			Unknown98 = 98,
			Unknown99 = 99,
			Unknown100 = 100,
			Unknown101 = 101,
			Unknown102 = 102,
			Unknown103 = 103,
			Unknown104 = 104,
			Unknown105 = 105,
			Unknown106 = 106,
			Unknown107 = 107,
			Unknown108 = 108,
			Unknown109 = 109,
			Unknown110 = 110,
			Unknown111 = 111,
			Unknown112 = 112,
			Unknown113 = 113,
			Unknown114 = 114,
			Unknown115 = 115,
			Unknown116 = 116,
			Unknown117 = 117,
			Unknown118 = 118,
			Unknown119 = 119,
			Unknown120 = 120,
			Unknown121 = 121,
			Unknown122 = 122,
			Unknown123 = 123,
			Unknown124 = 124,
			Unknown125 = 125,
			Unknown126 = 126,
			Unknown127 = 127,
			Unknown128 = 128,
			Unknown129 = 129,
			Unknown130 = 130,
			Unknown131 = 131,
			Unknown132 = 132,
			Unknown133 = 133,
			Unknown134 = 134,
			Unknown135 = 135,
			Unknown136 = 136,
			Unknown137 = 137,
			Unknown138 = 138,
			Unknown139 = 139,
			Unknown140 = 140,
			Unknown141 = 141,
			Unknown142 = 142,
			Unknown143 = 143,
			PvpMinigamesAbandonedSeason = 144,
			Unknown145 = 145,
			PvpMinigamesAbandonedOverall = 146,
			Unknown147 = 147,
			PvpSiegeLosesSeason = 148,
			Unknown149 = 149,
			PvpSiegeLosesOverall = 150,
			Unknown151 = 151,
			PvpSiegeWinsSeason = 152,
			Unknown153 = 153,
			PvpSiegeWinsOverall = 154,
			Unknown155 = 155,
			PvpCaptureTheSkullLossesSeason = 156,
			Unknown157 = 157,
			PvpCaptureTheSkullLossesOverall = 158,
			Unknown159 = 159,
			PvpCaptureTheSkullWinsSeason = 160,
			Unknown161 = 161,
			PvpCaptureTheSkullWinsOverall = 162,
			Unknown163 = 163,
			PvpAnnihilationLossesSeason = 164,
			Unknown165 = 165,
			PvpAnnihilationLossesOverall = 166,
			Unknown167 = 167,
			PvpAnnihilationWinsSeason = 168,
			Unknown169 = 169,
			PvpAnnihilationWinsOverall = 170,
			Unknown171 = 171,
			PvpFatalitiesSeason = 172,
			Unknown173 = 173,
			PvpFatalitiesOverall = 174,
			Unknown175 = 175,
			PvpDeathSeason = 176,
			Unknown177 = 177,
			PvpDeathsOverall = 178,
			Unknown179 = 179,
			PvpKillsSeason = 180, // factor 10
			Unknown181 = 181,
			Unknown182 = 182 // PvpKillsOverall ?
		}

		// Authors the WHOLE per-class vitals block (#4/#186): for HP/Mana/Stamina it sets the DISPLAYED max AND the
		// lower base/secondary the client reads into a parallel slot, each ×100, keeping the +148 repeated copies in
		// sync. The old per-stat setters (CurrentHp/Mana/Stamina) touched only the displayed slot, leaving the base
		// slots - and the second mana copy [60] - at the captured Strie (Rogue) template's values; that internal
		// inconsistency is the suspected cause of #4 (a Necromancer showed ~0 mana because [60] stayed 0 while [64]
		// held 24600) and the in-client "gear vanished" snapshot reject. Verified slot map across four archetypes'
		// retail LOGIN snapshots (decode_statblock.py): HP displayed [23]/[54], base [21]/[56]; mana displayed
		// [60]=[64], secondary [62]; stamina displayed [68], secondary [66]. Pass CharacterMaxStats.SnapshotVitals().
		public void ApplyClassVitals(uint hp, uint hpBase, uint mana, uint manaBase, uint stamina, uint staminaBase)
		{
			// HP: [21]/[23] are pre-repeated-block standalone slots (like CurrentHpExtra, no +148 copy); [54]/[56] live
			// inside the repeated block, so SetValue also writes their [202]/[204] copies.
			Values[21] = hpBase * 100;
			Values[(int)CharacterValueIndex.CurrentHpExtra] = hp * 100; // [23]
			SetValue(CharacterValueIndex.CurrentHp, hp * 100); // [54] (+[202])
			SetValue((CharacterValueIndex)56, hpBase * 100); // [56] (+[204])

			// Mana: the displayed max lives in BOTH [60] and [64]; [62] is the ~6-lower secondary.
			SetValue((CharacterValueIndex)60, mana * 100); // [60] (+[208])
			SetValue(CharacterValueIndex.ManaSecondary, manaBase * 100); // [62] (+[210])
			SetValue(CharacterValueIndex.CurrentMana, mana * 100); // [64] (+[212])

			// Stamina: displayed [68], secondary [66].
			SetValue((CharacterValueIndex)66, staminaBase * 100); // [66] (+[214])
			SetValue(CharacterValueIndex.CurrentStamina, stamina * 100); // [68] (+[216])
		}

		public uint GetValue(CharacterValueIndex index)
		{
			return Values[(int)index];
		}

		// The block is a [statId, value] stat sheet: a stat's value is the slot immediately after each occurrence of
		// its id (a stat appears in the leading displayed section, the repeated run, AND the run's +148 copy). Sets the
		// value at EVERY occurrence so the sheet is internally consistent. Used to DERIVE character stats (Level 0x36,
		// XP 0x34, MaxXP 0x15E, ...) from the character instead of carrying the skeleton's fixed value - the fix for
		// hardcoded Level: the 313-value "fixed skeleton" was diffed only across level-1 characters, so the level slot
		// never varied and was misclassified as fixed (it is really character-derived).
		public void SetStat(uint statId, uint value)
		{
			for (var i = 0; i + 1 < Values.Count; i++)
			{
				if (Values[i] == statId)
					Values[i + 1] = value;
			}
		}

		public void SetValue(CharacterValueIndex index, uint value)
		{
			Values[(int)index] = value;
			Values[GetRepeatedCopyIndex(index)] = value;
		}

		private static int GetRepeatedCopyIndex(CharacterValueIndex index)
		{
			var indexx = (int)index;

			if (indexx is < RepeatedBlockStartIndex or > RepeatedBlockEndIndex)
				throw new ArgumentOutOfRangeException(nameof(indexx), indexx, $"Index must be inside repeated block {RepeatedBlockStartIndex}..{RepeatedBlockEndIndex}.");

			return indexx + RepeatedBlockOffset;
		}
	}
}
