namespace Faolan.Core.Enums
{
	/// <summary>
	///     Character stat ids seen in the SpawnPlayer / snapshot / 0x96B8DC59 vitals capture families.
	///     Two on-wire encodings are confirmed:
	///     - 64-bit big-endian: (statId &lt;&lt; 32) | rawValue
	///     - compact 7-byte big-endian: 3-byte statId + 4-byte rawValue
	/// </summary>
	public enum CharacterStatId : uint
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
		Dexterity = 0x032E,

		// Undecoded slots of the 0x737A6DF9 tails stat sheet, numbered Unknown{i} so the whole block is enumerable
		// (StatBlockSchema). Values are the raw wire stat ids; rename to a real name as each is decoded.
		Unknown0 = 0x252, Unknown1 = 0x253, Unknown2 = 0x254, Unknown3 = 0x255, Unknown4 = 0x000, Unknown5 = 0x0E0,
		Unknown6 = 0x0AE, Unknown7 = 0x0AD, Unknown8 = 0x0BF, Unknown9 = 0x40B, Unknown10 = 0x039, Unknown11 = 0x0A9,
		Unknown12 = 0x534, Unknown13 = 0x16B, Unknown14 = 0x44A, Unknown15 = 0x163, Unknown16 = 0x449, Unknown17 = 0x0A5,
		Unknown18 = 0x241, Unknown19 = 0x113, Unknown20 = 0x04F, Unknown21 = 0x00C, Unknown22 = 0x022, Unknown23 = 0x03B,
		Unknown24 = 0x00B, Unknown25 = 0x03A, Unknown26 = 0x021, Unknown27 = 0x04B, Unknown28 = 0x0D7, Unknown29 = 0x185,
		Unknown30 = 0x25F, Unknown31 = 0x06A, Unknown32 = 0x0F3, Unknown33 = 0x445, Unknown34 = 0x43D, Unknown35 = 0x33E,
		Unknown36 = 0x33C, Unknown37 = 0x08C, Unknown38 = 0x338, Unknown39 = 0x332, Unknown40 = 0x39D, Unknown41 = 0x39B,
		Unknown42 = 0x398, Unknown43 = 0x39A, Unknown44 = 0x07F, Unknown45 = 0x05B, Unknown46 = 0x03C, Unknown47 = 0x1C8,
		Unknown48 = 0x14B, Unknown49 = 0x03D, Unknown50 = 0x575, Unknown51 = 0x560, Unknown52 = 0x573, Unknown53 = 0x29F,
		Unknown54 = 0x56E, Unknown55 = 0x29E, Unknown56 = 0x571, Unknown57 = 0x56B, Unknown58 = 0x570, Unknown59 = 0x56A,
		Unknown60 = 0x569, Unknown61 = 0x564, Unknown62 = 0x567, Unknown63 = 0x566
	}

	// The x100 / x10 wire scale a stat's player-facing value is multiplied by (vitals x100, attributes x10, the rest
	// x1). CharacterStatBlock.Set applies it so callers pass display values.
	public static class CharacterStatIdExtensions
	{
		public static uint GetScale(this CharacterStatId statId)
		{
			return statId switch
			{
				CharacterStatId.HealthCurrent or CharacterStatId.HealthMax or CharacterStatId.StaminaMax or CharacterStatId.StaminaCurrent or CharacterStatId.ManaMax
					or CharacterStatId.ManaCurrent => 100,

				CharacterStatId.Strength or CharacterStatId.Constitution or CharacterStatId.Intelligence or CharacterStatId.Wisdom or CharacterStatId.Dexterity => 10,

				_ => 1
			};
		}
	}
}
