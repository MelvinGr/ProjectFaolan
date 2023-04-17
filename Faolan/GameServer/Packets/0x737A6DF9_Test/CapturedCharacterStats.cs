namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	/// <summary>
	///     Known legacy stat ids seen in the mixed SpawnPlayer / bigboy capture families.
	///     Two encodings are currently confirmed:
	///     - 64-bit big-endian: (statId &lt;&lt; 32) | rawValue
	///     - compact 7-byte big-endian: 3-byte statId + 4-byte rawValue
	/// </summary>
	public enum LegacyCharacterStatId : uint
	{
		MaxHealth = 0x0001,
		Health = 0x001B,
		PvpExp = 0x0033,
		Exp = 0x0034,
		Level = 0x0036,
		PvpLevel = 0x0046,
		MaxExp = 0x015E,
		PvpMaxExp = 0x0161,
		Stamina = 0x01F8,
		MaxStamina = 0x01F9,
		Mana = 0x01FA,
		MaxMana = 0x01FB,
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

	public enum LegacyCharacterStatEncoding
	{
		TaggedUInt64,
		PackedStatId24AndUInt32Value
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

		public required LegacyCharacterStatEncoding Encoding { get; init; }
		public required LegacyCharacterStatId StatId { get; init; }

		public required uint RawValue { get; init; }

		public static uint GetScale(LegacyCharacterStatId statId)
		{
			return statId switch
			{
				LegacyCharacterStatId.MaxHealth or LegacyCharacterStatId.Health or LegacyCharacterStatId.Stamina or LegacyCharacterStatId.MaxStamina or LegacyCharacterStatId.Mana or LegacyCharacterStatId.MaxMana => 100,

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
		public decimal? Health =>
			HealthRaw is null
				? null
				: HealthRaw.Value / 100m;

		public decimal? Mana =>
			ManaRaw is null
				? null
				: ManaRaw.Value / 100m;

		public decimal? MaxStamina =>
			MaxStaminaRaw is null
				? null
				: MaxStaminaRaw.Value / 100m;

		public uint? AnnihilationLosses { get; set; }
		public uint? CaptureSkullLosses { get; set; }
		public uint? CaptureSkullWins { get; set; }
		public uint? HealthRaw { get; set; }

		public uint? Level { get; set; }

		public uint? ManaRaw { get; set; }

		public uint? MaxStaminaRaw { get; set; }
		public uint? OverallDeaths { get; set; }
		public uint? OverallKills { get; set; }
		public uint? PvpLevel { get; set; }

		public void Apply(LegacyCharacterStatEntry entry)
		{
			switch (entry.StatId)
			{
				case LegacyCharacterStatId.Health:
					HealthRaw = entry.RawValue;
					break;

				case LegacyCharacterStatId.Mana:
					ManaRaw = entry.RawValue;
					break;

				case LegacyCharacterStatId.MaxStamina:
					MaxStaminaRaw = entry.RawValue;
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
