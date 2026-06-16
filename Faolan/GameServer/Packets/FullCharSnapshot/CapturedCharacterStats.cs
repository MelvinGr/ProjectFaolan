namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	/// <summary>
	///     Known legacy stat ids seen in the mixed SpawnPlayer / bigboy capture families.
	///     Two encodings are currently confirmed:
	///     - 64-bit big-endian: (statId &lt;&lt; 32) | rawValue
	///     - compact 7-byte big-endian: 3-byte statId + 4-byte rawValue
	/// </summary>
	///  TODO: Rename legacy?
	public enum LegacyCharacterStatId : uint
	{
		// Vitals are sent as a (current, max) pair per stat in the 0x96B8DC59 update. The current/max id assignment
		// is **proven in-client** (2026-06-14, live .setstat probes on Darktemplarchar): setting 0x0001 moved the
		// player's MAX health bar, 0x001B moved CURRENT; 0x01F8 moved stamina CURRENT, 0x01FA moved mana CURRENT.
		// So current = {0x001B health, 0x01F8 stamina, 0x01FA mana}; max = {0x0001, 0x01F9, 0x01FB}. (The old
		// "larger id = max" heuristic was backwards - a full-health capture has current == max, so the announce
		// packet that uses 0x001B/0x01F8/0x01FA was the CURRENT ids all along; see CharacterStatBlockTests.)
		HealthCurrent = 0x001B,
		HealthMax = 0x0001,

		// Combat-mode / weapon-stance flag: pushed in a 0x96B8DC59 update the tick an entity enters combat, telling
		// the client to DRAW the entity's weapon (out of combat the weapon is sheathed). Decoded from the strickenbla
		// combat capture (#218): every beach melee mob gets `CombatMode = 4` the moment before its first swing, and the
		// player gets `CombatMode = 1` when it enters combat - so without it an NPC swings EMPTY-HANDED (the swing plays
		// but no weapon is drawn). The value is a stance/mode code not fully decoded (melee mobs = 4, player = 1); idle
		// entities carry no record at all, so 0 = sheathed.
		CombatMode = 0x0014,
		PvpExp = 0x0033,
		Exp = 0x0034,
		Level = 0x0036,
		PvpLevel = 0x0046,
		MaxExp = 0x015E,
		PvpMaxExp = 0x0161,
		StaminaCurrent = 0x01F8,
		StaminaMax = 0x01F9,
		ManaCurrent = 0x01FA,
		ManaMax = 0x01FB,
		KillingBlows = 0x0290,
		Death = 0x0292,
		AnnihilationWin = 0x029A,
		AnnihilationLose = 0x029B,
		CaptureSkullWin = 0x029C,
		CaptureSkullLose = 0x029D,
		Strength = 0x0324,
		Constitution = 0x0326,
		Intelligence = 0x0328,
		Wisdom = 0x032A,
		Dexterity = 0x032E
	}

	public sealed class LegacyCharacterStatEntry
	{
		public decimal ScaledValue =>
			GetScale(StatId) switch
			{
				100 => RawValue / 100m,
				10  => RawValue / 10m,
				_   => RawValue
			};

		public required LegacyCharacterStatId StatId { get; init; }

		public required uint RawValue { get; init; }

		public static uint GetScale(LegacyCharacterStatId statId)
		{
			return statId switch
			{
				LegacyCharacterStatId.HealthCurrent or LegacyCharacterStatId.HealthMax or LegacyCharacterStatId.StaminaMax or LegacyCharacterStatId.StaminaCurrent or LegacyCharacterStatId.ManaMax
					or LegacyCharacterStatId.ManaCurrent => 100,

				LegacyCharacterStatId.Strength or LegacyCharacterStatId.Constitution or LegacyCharacterStatId.Intelligence or LegacyCharacterStatId.Wisdom or LegacyCharacterStatId.Dexterity => 10,

				_ => 1
			};
		}
	}

	/// <summary>
	///     Strongly typed view over the exact screenshot-aligned values that are already confirmed
	///     in the bigboy / SpawnPlayer capture family.
	///     Intentionally conservative: only fields with clustered or repeated evidence are promoted
	///     to named properties here. Less certain mappings should remain as raw <see cref="LegacyCharacterStatEntry" />
	///     instances until more captures are decoded.
	/// </summary>
	public sealed class CapturedCharacterStats
	{
		public decimal? HealthMax =>
			HealthMaxRaw is null
				? null
				: HealthMaxRaw.Value / 100m;

		public decimal? ManaMax =>
			ManaMaxRaw is null
				? null
				: ManaMaxRaw.Value / 100m;

		public decimal? StaminaCurrent =>
			StaminaCurrentRaw is null
				? null
				: StaminaCurrentRaw.Value / 100m;

		public uint? AnnihilationLosses { get; set; }
		public uint? CaptureSkullLosses { get; set; }
		public uint? CaptureSkullWins { get; set; }
		public uint? HealthMaxRaw { get; set; }

		public uint? Level { get; set; }

		public uint? ManaMaxRaw { get; set; }
		public uint? OverallDeaths { get; set; }
		public uint? OverallKills { get; set; }
		public uint? PvpLevel { get; set; }

		public uint? StaminaCurrentRaw { get; set; }

		public void Apply(LegacyCharacterStatEntry entry)
		{
			switch (entry.StatId)
			{
				case LegacyCharacterStatId.HealthMax:
					HealthMaxRaw = entry.RawValue;
					break;

				case LegacyCharacterStatId.ManaMax:
					ManaMaxRaw = entry.RawValue;
					break;

				case LegacyCharacterStatId.StaminaCurrent:
					StaminaCurrentRaw = entry.RawValue;
					break;

				case LegacyCharacterStatId.Level:
					Level = entry.RawValue;
					break;

				case LegacyCharacterStatId.PvpLevel:
					PvpLevel = entry.RawValue;
					break;

				case LegacyCharacterStatId.KillingBlows:
					OverallKills = entry.RawValue;
					break;

				case LegacyCharacterStatId.Death:
					OverallDeaths = entry.RawValue;
					break;

				case LegacyCharacterStatId.AnnihilationLose:
					AnnihilationLosses = entry.RawValue;
					break;

				case LegacyCharacterStatId.CaptureSkullWin:
					CaptureSkullWins = entry.RawValue;
					break;

				case LegacyCharacterStatId.CaptureSkullLose:
					CaptureSkullLosses = entry.RawValue;
					break;
			}
		}
	}
}
