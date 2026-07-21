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

		// Combat ratings carried by the item-template stat-modifier region (docs/rdb/item-template-stats.md, #3).
		// Decoded 3/3 against the in-client Royal Signet tooltip (+52 Magic Damage / +15 Hit Rating): Magic Damage is
		// wire-scaled ×100 (5200 -> 52), Hit Rating ×1 (15 -> 15).
		HitRating = 0x0136,
		MagicDamage = 0x035D,

		// The VALUE at this slot in the FullChar-snapshot stat sheet is the PLAYFIELD-INSTANCE token (framed by stat ids
		// 0xBF..0x40B in the snapshot; see WorldDespawn) - session/map state, not a real character stat. The snapshot is
		// built with the live zone's token here.
		PlayfieldToken = 0x00BF,

		// The mounted-state stats retail pushes with the ride buff (#27, mount-dismount capture frame 520): Mount = the
		// mount id/appearance the player is riding (0 = dismounted), MountSpeed = the mount speed value (100). Cleared to
		// 0 on dismount (frame 1449).
		Mount = 0x00BA,
		MountSpeed = 0x00BB,

		// The current LEVEL's base experience: the total XP at which the current level began, so the in-client XP bar
		// fills (Exp - ExpCurrentLevelBase) / (MaxExp - ExpCurrentLevelBase). Decoded by value-correlation across 29
		// archetype snapshots (#393): ExpCurrentLevelBase <= Exp <= MaxExp everywhere, it equals Exp for a freshly-
		// dinged char (loot L2: base==Exp==100), and level 24's base (263640) is EXACTLY level 23's MaxExp (263640) -
		// each level's base is the previous level's cap. See docs/re/fullplayer-snapshot-reader.md.
		ExpCurrentLevelBase = 0x0039,

		// A UNIX EPOCH timestamp slot (#393). Every non-zero value in the corpus decodes to a valid 2010 date
		// (1263095211 = 2010-01-10, 1280838357 = 2010-08-03), the SAME value recurs across DIFFERENT characters, and
		// one character carries different values in different captures - so it is a shared session/server time, not a
		// per-character property (0 in most snapshots). Which event it stamps (save/login/server clock) is not pinned;
		// the epoch decode is the grounded part.
		Timestamp = 0x044A,

		// Undecoded slots of the 0x737A6DF9 tails stat sheet, numbered Unknown{i} so the whole block is enumerable.
		// Values are the raw wire stat ids; rename to a real name as each is decoded.
		//
		// ⚠️ #393 finding: this sheet is a BASELINE SKELETON, not the character's live stats. The attributes are the
		// fixed base (Strength 0x324 / Intelligence 0x328 == 10 for EVERY char, L1 through L80), and ~two-thirds of the
		// slots are 0/constant across all 29 archetype captures. Only vitals (HP/mana/stamina), XP/level, the PvP
		// counters, PlayfieldToken and a handful below actually vary per character - the real leveled stats arrive via
		// the live StatIIR (0xF98E10B3) / vitals (0x96B8DC59) updates, not here. So the remaining Unknown## are mostly
		// not worth chasing from captures; the few that vary need in-client .setstat probing (the method that pinned
		// HP/mana/stamina) to name. Two more are characterised across a character's MANY captures - TYPE pinned, meaning
		// not (still Unknown):
		//   0x025F (Unknown30) - a per-character CONSTANT (byte-identical across NecroL80's 42 captures), only 3 distinct
		//                        values corpus-wide. NOT a scalar, and NOT race: two Stygian-only necros (Faolanx,
		//                        Stickienecro) carry different values. An appearance/creation bucket or identity hash.
		//   0x0398/0x039A/0x039B/0x039D (Unknown42/43/41/40) - four SIGNED values, base -300 (0xFFFFFED4) fresh, all four
		//                        EQUAL per char, and they scale WITH LEVEL (Hoxxling's quad tracks her ExpCurrentLevelBase
		//                        across captures, 100->9300 as base 0->263640). A level-scaled rating quad; exact stat
		//                        needs .setstat probing.
		Unknown0 = 0x252,
		Unknown1 = 0x253,
		Unknown2 = 0x254,
		Unknown3 = 0x255,
		Unknown4 = 0x000,
		Unknown5 = 0x0E0,
		Unknown6 = 0x0AE,
		Unknown7 = 0x0AD,
		Unknown9 = 0x40B,
		Unknown11 = 0x0A9,
		Unknown12 = 0x534,
		Unknown13 = 0x16B,
		Unknown15 = 0x163,
		Unknown16 = 0x449,
		Unknown17 = 0x0A5,
		Unknown18 = 0x241,
		Unknown19 = 0x113,
		Unknown20 = 0x04F,
		Unknown21 = 0x00C,
		Unknown22 = 0x022,
		Unknown23 = 0x03B,
		Unknown24 = 0x00B,
		Unknown25 = 0x03A,
		Unknown26 = 0x021,
		Unknown27 = 0x04B,
		Unknown28 = 0x0D7,
		Unknown29 = 0x185,
		Unknown30 = 0x25F,
		Unknown31 = 0x06A,
		Unknown32 = 0x0F3,
		Unknown33 = 0x445,
		Unknown34 = 0x43D,
		Unknown35 = 0x33E,
		Unknown36 = 0x33C,
		Unknown37 = 0x08C,
		Unknown38 = 0x338,
		Unknown39 = 0x332,
		Unknown40 = 0x39D,
		Unknown41 = 0x39B,
		// Two of the level-scaled rating quad (0x398/0x39A/0x39B/0x39D) pinned via the client (Ghidra 2026-07-21):
		// CoreCombatManager::GetAttackRating() reads 0x398, GetDefenseRating() reads 0x39A. The other two
		// (Unknown40/41 = 0x39D/0x39B) are the quad's remaining members (_rating/_effect or melee/ranged - Unknown).
		AttackRating = 0x398,
		DefenseRating = 0x39A,
		Unknown44 = 0x07F,
		Unknown45 = 0x05B,
		Unknown46 = 0x03C,
		Unknown47 = 0x1C8,
		Unknown48 = 0x14B,
		Unknown49 = 0x03D,
		Unknown50 = 0x575,
		Unknown51 = 0x560,
		Unknown52 = 0x573,
		Unknown53 = 0x29F,
		Unknown54 = 0x56E,
		Unknown55 = 0x29E,
		Unknown56 = 0x571,
		Unknown57 = 0x56B,
		Unknown58 = 0x570,
		Unknown59 = 0x56A,
		Unknown60 = 0x569,
		Unknown61 = 0x564,
		Unknown62 = 0x567,
		Unknown63 = 0x566
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
					or CharacterStatId.ManaCurrent or CharacterStatId.MagicDamage => 100,

				CharacterStatId.Strength or CharacterStatId.Constitution or CharacterStatId.Intelligence or CharacterStatId.Wisdom or CharacterStatId.Dexterity => 10,

				_ => 1
			};
		}
	}
}
