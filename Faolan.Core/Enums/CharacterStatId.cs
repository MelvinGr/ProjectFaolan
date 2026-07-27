namespace Faolan.Core.Enums
{
	/// <summary>
	///     Character stat ids seen in the SpawnPlayer / snapshot / 0x96B8DC59 vitals capture families.
	///     Two on-wire encodings are confirmed:
	///     - 64-bit big-endian: (statId &lt;&lt; 32) | rawValue
	///     - compact 7-byte big-endian: 3-byte statId + 4-byte rawValue
	///     <para>
	///         Member names are held to the client's own LDB 615 names by
	///         <c>Faolan.Importer --report ldb-stat-names</c>, which prints every divergence. The divergences it still
	///         reports are DELIBERATE, so don't "fix" them:
	///         <list type="bullet">
	///             <item>
	///                 <b>Health / Stamina / Mana.</b> LDB gives the current and max ids the SAME name ("Health" for
	///                 both 0x0001 and 0x001B), which C# cannot express - hence the Current/Max suffixes.
	///             </item>
	///             <item>
	///                 <b>Names carrying a decode the client's label does not.</b> 0x0014 is "BCC State" to the
	///                 client and <see cref="CombatMode" /> here because that is what it was proven to DO;
	///                 0x00BF "Last Concrete Playfield Instance" is <see cref="PlayfieldToken" />; 0x015E "Next XP"
	///                 is <see cref="MaxExp" />; 0x0039 "Last XP" is <see cref="ExpCurrentLevelBase" />.
	///             </item>
	///             <item><b>Exp vs the client's XP</b> - a spelling choice the whole codebase shares.</item>
	///         </list>
	///     </para>
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

		// "Apprentice Level" (LDB 965). The apprentice/mentor system's effective level, and it is NOT cosmetic: the
		// client divides BY it when it normalises armour mitigation by level - `(2.0 - Level / ApprenticeLevel) *
		// totalArmor` (FUN_0113ba00, docs/subsystems/combat.md). Unset it defaults to 1, so a level-80 character gets
		// `2 - 80/1` = -78 and the whole mitigation computation goes hugely negative. Proven from the client's own
		// GameCode/ArmorMitigation trace (2026-07-26): with it unset, NecroL80 logged
		// `ApprenticeLevel Adjusted ArmorValue:-168246`, `Mitigation Modification:-1532.57`, then
		// `No mitigation entry found ... [AdjustedRoll:1632.57]` - so the table lookup missed entirely and the client
		// fell through to a closed-form fallback. Sending it EQUAL TO the character's level makes the term 1.0, which
		// is what an un-mentored character should have.
		ApprenticeLevel = 0x03C5,
		PvpLevel = 0x0046,
		MaxExp = 0x015E,
		PvpMaxExp = 0x0161,
		StaminaCurrent = 0x01F8,
		StaminaMax = 0x01F9,
		ManaCurrent = 0x01FA,
		ManaMax = 0x01FB,
		// The PvP scoreboard counters, named as the client's own LDB 615 names them (2026-07-27) - they were singular
		// and one was an interpretation ("killing blows" for what the client just calls kills).
		Kills = 0x0290,
		Deaths = 0x0292,
		AnnihilationWins = 0x029A,
		AnnihilationLosses = 0x029B,
		CaptureSkullWins = 0x029C,
		CaptureSkullLosses = 0x029D,
		// The out-of-combat regen rows the character sheet shows (#442). The client SEEDS 278 -> 2000 and 279 -> 1000
		// (display 20.00 / 10.00) and adds an attribute "trickledown" on top at display time; 280's base is CLASS data,
		// carried by the class-feat template (0 for a class with no mana base, 2000/2200 for the casters).
		NonCombatHealthRegen = 0x0116,
		NonCombatStaminaRegen = 0x0117,
		NonCombatManaRegen = 0x0118,

		Strength = 0x0324,
		Constitution = 0x0326,
		Intelligence = 0x0328,
		Wisdom = 0x032A,
		Dexterity = 0x032E,

		// The SIXTH attribute the character sheet lists under the five above (LDB 615 names it "Ferocity"). Nothing on
		// our side grants it: zero rows in ClassAttributeBases / ItemStatModifiers / FeatEffects, 3 in SpellStatEffects,
		// and the client's own live stat table carries no entry for it either - so the sheet renders 0 for every
		// character, which is what it should read until something sets it. Named here so the id is not re-derived.
		Ferocity = 0x057B,

		// Combat ratings carried by the item-template stat-modifier region (docs/rdb/item-template-stats.md, #3).
		// Decoded 3/3 against the in-client Royal Signet tooltip (+52 Magic Damage / +15 Hit Rating): Magic Damage is
		// wire-scaled ×100 (5200 -> 52), Hit Rating ×1 (15 -> 15).
		HitRating = 0x0136,
		MagicDamage = 0x035D,

		// The PHYSICAL armor rating (#401): the client's armor-mitigation routine (FUN_0113ba00,
		// GameCode/ArmorMitigation) reads stat 0x1C0 as the base armor for every physical damage type (Slashing/
		// Piercing/Crushing/…), scaled by Strength through the TotalArmorValue formula. Carried per-item in the
		// [2][9] region (armor-specific: on 26% of armor pieces vs 7% elsewhere), ×1 scale. The magical-protection
		// sibling (0x1C3, Int/Wis-scaled) is left unnamed until it's consumed.
		Armor = 0x01C0,

		// Item stat-modifier ratings named from in-game tooltip screenshots (2026-07-21), matched by value:
		//   Combat Rating (0x36B, x1): Defiler Belt 14; the single most common region rating (63 items).
		//   Combat Rating - Fire (0x3EF, x1): Red Edge 11 (a per-damage-type Combat Rating variant).
		//   Offhand Rating (0x44B, x1): Red Edge 5.
		//   Natural Mana Regen (0x1F2, x100): Initiate's Wraps 500 -> +5, Fine Steelsilk Pants 450 -> +4.5.
		//   Natural Stamina Regen (0x385, x100): Defiler Belt 150 -> +1.5.
		//   Immunity Rating (0x1EA, x1): Initiate's Wraps 32 -> +32.
		CombatRating = 0x036B,
		CombatRatingFire = 0x03EF,
		OffhandRating = 0x044B,
		NaturalManaRegen = 0x01F2,
		NaturalStaminaRegen = 0x0385,
		ImmunityRating = 0x01EA,

		// More region ratings named from tooltip screenshots (2026-07-21), matched by value across several items:
		//   Critical Rating 0x138 (crit CHANCE): Skirmisher 75, Helm 45. Critical Damage Rating 0x2C7 (crit MAGNITUDE):
		//   Skirmisher 95, Poderes 50. Protection 0x1C3 (magical mitigation, the armor-fn sibling): Helm 184->185.
		//   Hate Decrease 0x1E8 / Hate Increase 0x1EB. Magic Damage (Holy) 0x36E x100: Poderes 2400->24.
		//   The PvP-* ratings only apply in PvP (named, not consumed): PvP Combat 0xE1, PvP Magic Dmg 0xE2 x100,
		//   PvP Hit 0x137, PvP Armor 0x1C6, PvP Protection 0x1CA.
		CriticalRating = 0x0138,
		CriticalDamageRating = 0x02C7,
		Protection = 0x01C3,
		HateDecreaseRating = 0x01E8,
		HateIncreaseRating = 0x01EB,
		MagicDamageHoly = 0x036E,
		PvpCombatRating = 0x00E1,
		PvpMagicDamage = 0x00E2,
		PvpHitRating = 0x0137,
		PvpArmor = 0x01C6,
		PvpProtection = 0x01CA,

		// Further region ratings named from granted-item tooltips (2026-07-22), matched by value. Fatality/Heal/XP/
		// movement are their own (unmodelled) systems; Combat Rating and Protection have per-weapon-type / per-element
		// variants (0x362-0x369 = Combat Rating by weapon; 0x39x = Protection by element) - only the CONFIRMED ids are
		// named here (Polearm 0x367, Bow 0x368; Protection(Electrical) 0x39f).
		FatalityRating = 0x01DB,
		HealRating = 0x02C9,
		CombatRatingBow = 0x0368,
		CombatRatingPolearm = 0x0367,
		ProtectionElectrical = 0x039F,

		// The remaining inputs of the client's ARMOUR-MITIGATION pipeline (FUN_0113ba00), all named by LDB category 615
		// and all confirmed by reading which stat the client asks for on each branch - so these are consumed ids, not
		// speculative ones. See docs/subsystems/combat.md.
		//
		// The two "Bonus" ids are PERCENT modifiers, not flat: the client computes
		// `(bonus * 0.01 + 1.0) * <armour|protection>`, so a value of 25 means +25%. BonusArmor feeds the physical
		// branch and BonusProtection the magical one - which is what identifies them, since the LDB names alone do not
		// say they are percentages.
		BonusArmor = 0x01C2, // LDB 450 "Bonus Armor" - percent, physical branch
		BonusProtection = 0x014E, // LDB 334 "Bonus Protection" - percent, magical branch

		// Per-element Protection completing the 0x39x family already begun above. The mitigation function selects one
		// of these by the incoming damage type (Cold on type 10, Fire on 4, Holy/Unholy on the 6/7 branch).
		ProtectionCold = 0x009D, // LDB 157
		ProtectionFire = 0x039E, // LDB 926
		ProtectionHoly = 0x03A0, // LDB 928
		ProtectionUnholy = 0x03A1, // LDB 929
		Perception = 0x033E, // LDB 830; the "Skill" suffix was ours, the client names it plain Perception
		OutOfCombatMovementSpeed = 0x00F1,
		BowRangeModifier = 0x0123,
		XpModifier = 0x013F,

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

		// The remaining slots of the 0x737A6DF9 tails stat sheet. These were numbered Unknown{i} until the client's own
		// stat-name table was found: LDB (RDB 1030002) **category 615** is an id -> name map keyed by the WIRE STAT ID
		// (891 ids, in 7 localized pages - the English one is cribbed against the names we already had, not picked by
		// order). It agrees with every id we had already named the hard way (310 Hit Rating, 312 Critical Rating,
		// 0x324 Strength, 0x398 Attack Rating from Ghidra…), so the names below are the client's, not a guess.
		// Regenerate with
		// `dotnet run --project Faolan.Importer -- --report ldb-stat-names`; the full table is docs/re/ldb-stat-names.md.
		// A few ids remain Unknown##: the client's table has no entry for them at all.
		//
		// The Anarchy Online leftovers (Nano Interval, Awarded Omni-Tek Tokens, Memorize Delay) are named as the client
		// names them - they are dead slots in Conan, and a made-up "better" name would not match the client's table.
		//
		// ⚠️ #393 finding: this sheet is a BASELINE SKELETON, not the character's live stats. The attributes are the
		// fixed base (Strength 0x324 / Intelligence 0x328 == 10 for EVERY char, L1 through L80), and ~two-thirds of the
		// slots are 0/constant across all 29 archetype captures. Only vitals (HP/mana/stamina), XP/level, the PvP
		// counters, PlayfieldToken and a handful below actually vary per character - the real leveled stats arrive via
		// the live StatIIR (0xF98E10B3) / vitals (0x96B8DC59) updates, not here. Naming a slot does NOT mean the server
		// fills it; what varies per character is characterised there.
		TempSaveTeamId = 0x252,
		TempSavePlayfield = 0x253,
		TempSaveX = 0x254,
		TempSaveY = 0x255,
		Flags = 0x000,
		Features = 0x0E0,
		PreviousMovementMode = 0x0AE,
		CurrentMovementMode = 0x0AD,
		DeathEffectXp = 0x40B,
		ChosenGod = 0x0A9,
		Unknown12 = 0x534,
		NanoInterval = 0x16B,
		Unknown15 = 0x163,
		MurderPoints = 0x449,
		ShieldAllocation = 0x0A5,
		FeatResets = 0x241,
		XpKillRange = 0x113,
		Icon = 0x04F,
		Mesh = 0x00C,
		DeadTimer = 0x022,
		Sex = 0x03B,
		PreviousHealth = 0x00B,
		Played = 0x03A,
		Side = 0x021,
		AwardedOmniTekTokens = 0x04B,
		GmLevel = 0x0D7,
		Expansions = 0x185,
		PlayerId = 0x25F,
		LastKillNumber = 0x06A,
		Unknown32 = 0x0F3,
		Skulk = 0x445,
		RunSpeed = 0x43D,
		Hiding = 0x33C,
		Resilence = 0x08C,
		TrapDisarm = 0x338,
		Climbing = 0x332,
		MagicalResist = 0x39D,
		MagicRating = 0x39B,

		// The level-scaled rating quad (0x398/0x39A/0x39B/0x39D), now named end to end: two came from the client's code
		// (Ghidra 2026-07-21 - CoreCombatManager::GetAttackRating() reads 0x398, GetDefenseRating() reads 0x39A) and the
		// other two from its stat-name table (0x39B Magic Rating, 0x39D Magical Resist, declared just above). So the
		// quad is attack/defense/magic-attack/magic-defense - which is why all four move together with level.
		AttackRating = 0x398,
		DefenseRating = 0x39A,
		MemorizeDelay = 0x07F,
		ElderFrostSpecialization = 0x05B,
		Unknown46 = 0x03C,
		PetPoints = 0x1C8,
		Unknown48 = 0x14B,
		Money = 0x03D,
		Unknown50 = 0x575,
		MinigamesAbandoned = 0x560,
		Unknown52 = 0x573,
		SiegeLosses = 0x29F,
		Unknown54 = 0x56E,
		SiegeWins = 0x29E,
		Unknown56 = 0x571,
		Unknown57 = 0x56B,
		Unknown58 = 0x570,
		Unknown59 = 0x56A,
		Unknown60 = 0x569,
		Fatalities = 0x564,
		Unknown62 = 0x567,
		Unknown63 = 0x566,

		// ---- Named from the CLIENT's own stat-name table, LDB category 615 (`--report ldb-stat-names`). ----
		// These are the stats the FEAT catalog modifies (FeatEffect) that nothing else had made us name: items touch
		// 17 stat ids, feats touch 158. The names are the client's, not ours - which is also why several read oddly
		// ("Anim", "BCC", "Volume Mass"): the stat space carries engine fields alongside player-facing stats.
		//
		// 17 further feat stat ids remain UNNAMED because LDB 615 has no entry for them - the 1084..1094 family
		// (values 100..900, plainly a real group) plus a handful of singletons with implausible values (44231 at
		// -5000, 1253 at 860633420) that are more likely over-reads by the effect parser than real stats.
		VolumeMass = 0x002,
		Anim = 0x00D,
		BCC = 0x015,
		StaticInstance = 0x017,
		StaticType = 0x019,
		ShoulderMesh = 0x027,
		MagicRatingFrost = 0x05A,
		RespecPoints = 0x064,
		BleedingChance = 0x066,
		ExtraInventory = 0x068,
		BerserkPercentLevel = 0x06E,
		MaxSpellWeave = 0x083,
		MaxBarter = 0x087,
		MaxDisruptive = 0x08B,
		MaxResilence = 0x08D,
		MaxTaunt = 0x08F,
		AvailableSlots = 0x095,
		MagicDamageCold = 0x09E,
		ColdInvulnerability = 0x0A7,
		PvPAttackRating = 0x0D8,
		PvPMagicRating = 0x0D9,
		PvPDefenceRating = 0x0DA,
		PvPResistanceRating = 0x0DB,
		CurrentBodyLocation = 0x0DC,
		SpellColumnRadius = 0x0DE,
		PhysicalHealthTap = 0x0E3,
		Slashing = 0x0E4,
		Piercing = 0x0E5,
		Crushing = 0x0E6,
		Fire = 0x0E7,
		Cold = 0x0E8,
		Electrical = 0x0E9,
		Holy = 0x0EA,
		RideSkill = 0x0EE,
		MovementMultiplier = 0x0F0,
		EvadeChance = 0x0FE,
		EvadeLevelGrowth = 0x0FF,
		ClanHierarchy = 0x104,
		GatherSound = 0x10D,
		TravelSound = 0x10F,
		Unholy = 0x119,
		Poison = 0x11A,
		ItemSIS = 0x128,
		Members = 0x12C,
		MinMembers = 0x12D,
		MagicVariance = 0x140,
		ReceivedHealingModifier = 0x14A,
		TradeLimit = 0x15A,
		PetDamageCoefficient = 0x1D1,
		MagicHealthTap = 0x230,
		MagicManaTap = 0x231,
		MaxStrength = 0x325,
		MaxConstitution = 0x327,
		MaxIntelligence = 0x329,
		MaxWisdom = 0x32B,
		Charisma = 0x32C,
		MaxCharisma = 0x32D,
		MaxDexterity = 0x32F,
		MaxCastingConcentration = 0x331,
		MaxClimbing = 0x333,
		MaxPickpocket = 0x335,
		MaxBandaging = 0x337,
		MaxTrapDisarm = 0x339,
		MaxLockpicking = 0x33B,
		MaxHiding = 0x33D,
		MaxPerception = 0x33F,
		MaxRenewMana = 0x341,
		MaxFastHealing = 0x343,
		MaxManaAttractor = 0x345,
		PlayerFlags = 0x360,
		CombatRating1HE = 0x362,
		CombatRating2HE = 0x363,
		CombatRating1HB = 0x364,
		CombatRating2HB = 0x365,
		CombatRatingDagger = 0x366,
		CombatRatingCrossbow = 0x369,
		CombatRatingThrown = 0x36A,
		MagicDamageFire = 0x36C,
		MagicDamageElectrical = 0x36D,
		MagicDamageUnholy = 0x36F,
		PoisonDamage = 0x370,
		NaturalHealthRegen = 0x383,
		SlashingInvulnerability = 0x386,
		PiercingInvulnerability = 0x387,
		CrushingInvulnerability = 0x388,
		FireInvulnerability = 0x389,
		ElectricalInvulnerability = 0x38A,
		HolyInvulnerability = 0x38B,
		UnholyInvulnerability = 0x38C,
		PoisonInvulnerability = 0x38D,
		ProtectionPoison = 0x3A3,
		MagicRatingFire = 0x3A4,
		MagicRatingElectrical = 0x3A5,
		MagicRatingHoly = 0x3A6,
		MagicRatingUnholy = 0x3A7,
		MagicRatingCurse = 0x3A8,
		MagicRatingPoison = 0x3A9,
		AttackRatingOneHandedEdged = 0x3AA,
		AttackRatingTwoHandedEdged = 0x3AB,
		AttackRatingOneHandedBlunt = 0x3AC,
		AttackRatingTwoHandedBlunt = 0x3AD,
		AttackRatingDagger = 0x3AE,
		AttackRatingPolearm = 0x3AF,
		AttackRatingBow = 0x3B0,
		AttackRatingCrossbow = 0x3B1,
		AttackRatingThrown = 0x3B2,
		MaxBlockingChance = 0x410,
		ChanceOfOffhandAttack = 0x42F,
		WeaponDamageMelee = 0x447,
		WeaponDamageRanged = 0x448,
	}

	/// <summary>
	///     The wire scale a stat's player-facing value is multiplied by - vitals ×100, attributes ×10, the rest ×1.
	///     <see cref="CharacterStatBlock" />.Set applies it, so callers pass DISPLAY values.
	/// </summary>
	/// <remarks>
	///     ⚠️ <b>This table is INCOMPLETE, and its default is a silent wrong answer.</b> An id it does not name falls to
	///     ×1, which reads exactly like "this stat is unscaled" - and that is how a ×100 stat went unnoticed:
	///     <see cref="CharacterStatId.WeaponDamageMelee" /> was in the enum but not here, so a feat granting 3800 would
	///     have been applied as +3800% where the client renders +38%. Assume an id absent from this switch is
	///     UNMEASURED rather than known to be ×1.
	///     <para>
	///         <b>How an entry gets added: only from a RENDERED value.</b> The client keeps its whole stat table in WIRE
	///         units and divides only when it draws, so the `ClientInterface` log cannot tell you a scale - mining 64
	///         client-applied deltas across 11 stats against our stored amounts returns a ratio of 1 for every one of
	///         them (which does independently confirm our <c>ItemStatModifier.Amount</c> values are right, just not
	///         their scale). A scale needs a number a human can read: a tooltip or the character sheet, against the
	///         wire value we hold. That is how the three groups below were each established.
	///     </para>
	/// </remarks>
	public static class CharacterStatIdExtensions
	{
		public static uint GetScale(this CharacterStatId statId)
		{
			return statId switch
			{
				// Vitals: proven on screen twice (2026-07-27) - wire 428500 renders as 4285 health, 205300 as 2053
				// stamina. MagicDamage's ×100 came from the in-client Royal Signet tooltip (#3).
				CharacterStatId.HealthCurrent or CharacterStatId.HealthMax or CharacterStatId.StaminaMax or CharacterStatId.StaminaCurrent or CharacterStatId.ManaMax or CharacterStatId.ManaCurrent
					or CharacterStatId.MagicDamage or CharacterStatId.NaturalManaRegen or CharacterStatId.NaturalStaminaRegen or CharacterStatId.PvpMagicDamage or CharacterStatId.MagicDamageHoly
					or CharacterStatId.BowRangeModifier => 100,

				// The weapon-damage PERCENTS. Proven in-client 2026-07-27: the feat `Lotus Blades` holds 3800/7600/
				// 11400/15200/19000 across its five ranks on 1095 and the client's own tooltip renders rank 1 as
				// "+38% Weapon Damage (Melee)" - exactly ×100, and the whole curve reads 38/76/114/152/190. 1096 is
				// its ranged twin: same LDB 615 family, adjacent id, and the same feat trees carry both.
				CharacterStatId.WeaponDamageMelee or CharacterStatId.WeaponDamageRanged => 100,

				// Attributes: proven on a freshly created Necromancer's sheet (2026-07-27) - a 10-wire class base
				// renders as 1. Ferocity is the sixth attribute and shares their scale: `.setstat 1403 10000` draws as
				// 1000 on the General tab.
				CharacterStatId.Strength or CharacterStatId.Constitution or CharacterStatId.Intelligence or CharacterStatId.Wisdom or CharacterStatId.Dexterity
					or CharacterStatId.Ferocity => 10,

				// MEASURED x1 (`.setstat <id> 10000`, 2026-07-27, read off the character sheet): 448 Armor drew 10002
				// = 10000 + its base of 2; 310 Hit Bonus Chance and 711 Critical Bonus Damage drew 10000 in the Rating
				// column (their "Effect" percent is a derived game formula, not a scale); 451 Protection drew 10000 and
				// feeds all five schools off the one stat. They land on the default below - listed here so they read as
				// measured rather than assumed.
				_ => 1
			};
		}
	}
}
