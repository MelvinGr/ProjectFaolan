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

		// Sheet stats measured with `.setstat` (2026-07-27), named from LDB 615. Their SCALES are in GetScale, and the
		// two fatality stats are the warning against inferring one from a neighbour: despite the names, 0x0177 renders
		// its raw value verbatim (×1) while 0x017C is a ×100 percent.
		HateModifier = 0x00AA, // LDB 170 - the sheet's "Hate Modification"
		Immunity = 0x0146, // LDB 326 - the sheet's "Immunity Chance"
		FatalityChance = 0x0177, // LDB 375
		SpellFatalityChance = 0x017C, // LDB 380

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
		// The rest of the ids the item [2][9] region actually carries, named from LDB 615 in one pass (#464). They
		// were the tail of `--report region-stat-census`: every id ANY item grants, whether or not we import it.
		// Nothing reads them yet and none are whitelisted for import - naming is not importing - but an id with a
		// name is one nobody has to re-derive, and several answer questions that were open in prose:
		//
		//   - the per-SCHOOL Combat Ratings complete the family we already had (Cold/Electrical/Holy/Unholy),
		//   - the TAP ratings are the leech family (health/mana/stamina/magic-life),
		//   - Evade Rating is the counterpart of the EvadeChance percent GetScale already knows about,
		//   - and three are not combat stats at all (Hair Render Type, Scale, Sprinting Stamina Drain) - a reminder
		//     that the region is an item's whole property list, not a damage sheet.
		CombatRatingCold = 0x00A2, // LDB 162
		AttackRange = 0x011F, // LDB 287
		CrossbowRangeModifier = 0x0124, // LDB 292
		EvadeRating = 0x0134, // LDB 308 - the RATING beside EvadeChance (0xFE), which is the percent
		Scale = 0x0168, // LDB 360 - render scale, not a combat stat
		MagicLifeTapRating = 0x01DC, // LDB 476
		HealthTapRating = 0x01DD, // LDB 477
		MagicManaTapRating = 0x01E0, // LDB 480
		StaminaTapRating = 0x01ED, // LDB 493
		MagicDamageModifier = 0x0218, // LDB 536
		PvpXpModifier = 0x02B2, // LDB 690
		CastingConcentration = 0x0330, // LDB 816
		DamageMultiplier = 0x0348, // LDB 840
		DefenseRatingHeroic = 0x03B4, // LDB 948
		CombatRatingElectrical = 0x03F0, // LDB 1008
		CombatRatingHoly = 0x03F1, // LDB 1009
		CombatRatingUnholy = 0x03F2, // LDB 1010
		XpFromPvpKills = 0x0562, // LDB 1378
		HairRenderType = 0x002F, // LDB 47 - appearance, not combat
		SprintingStaminaDrain = 0x0025, // LDB 37

		// ⚠️ TWO region ids have NO LDB 615 entry and stay unnamed: 10 (on 89 items, values 0..6 - the most common
		// unnamed id by a wide margin) and 1669. LDB 615 is the client's own stat-name catalog, so an id missing
		// from it will not be named by reading harder - it needs a consumer in the client to identify it.
		// The FOURTH most common stat on an item's [2][9] region - 249 of 3,317 templates carry it, more than
		// HealthMax - and it sat unnamed while rarer neighbours had names. Found by censusing the region rather than
		// the whitelist (`--report region-stat-census`), then read straight out of LDB 615.
		KnockbackResistance = 0x040D, // LDB 1037

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
		// 0xBF..0x40B in the snapshot; see N3ToClientQuitCodec) - session/map state, not a real character stat. The snapshot is
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
		// The SKILL-POINT pool (#507) - the number the client reports as "gained N skillpoints". Identified from the
		// annotation `gained 1892 skillpoints after the relog`: exactly ONE stat in that login's 630 KB
		// FullPlayerCharacterIIR equals 1892, and it is this one. Purely LEVEL-derived - see SkillPointCurve for the
		// measured values and why there is no formula. It has no LDB name (the stat-name table jumps 242 -> 248), which
		// fits an internal pool the UI labels itself rather than a displayed stat.
		SkillPoints = 0x0F3,
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

		// ---- The SPELL half of the same space (#593), likewise named from LDB 615. ----
		// A stat id does not have to name something a CHARACTER carries: the `SpellStatModifier` /
		// `SpellLineStatModifier` feat records (kinds 53054 / 53062) target a SPELL's own properties - its mana cost,
		// its casting time, its splash radius - through this one id space. Two of the fourteen they use were already
		// here from elsewhere (<see cref="SpellColumnRadius" /> 0x0DE, <see cref="AttackRange" /> 0x11F), which is what
		// says it IS one space rather than a parallel one; the twelve below are the rest.
		//
		// Confirmed by the client: its item-tooltip builder resolves this word through
		// `GetLocalisedString(615, stat)` - LDB category 615 is `0x267`, the literal it pushes at 014a2714 - so the
		// name the client would print for the id is the name it has.
		Duration = 0x008,
		SpellCriticalDamage = 0x081,
		ManaCost = 0x084,
		SpellCriticalChance = 0x093,
		CastingTimeSeconds = 0x126,
		SplashRadius = 0x15B,
		SplashDamage = 0x15C,
		SplashDamageMaxTargets = 0x15D,
		ChanceOfBreakOnSpellAttack = 0x181,
		HealthBreakThreshold = 0x183,
		ChanceOfBreakOnAttack = 0x1A6,
		ChannelingInterval = 0x1BF,

		// ---- The ENERGY pool: the yellow sprint/spellweave bar (#141). ----
		// Named by the CLIENT, not by us and not by the LDB - LDB category 615 names none of the 1000000+ range, but
		// the client registers every stat id against a name string in a static metadata table
		// (`tools/ghidra/DumpStatNameTable.java`; `MaxEnergy` is the literal at 0x01f72a00, bound to 0xF425D at
		// 0x01de6e20). The table's low half round-trips against ids we already had from an entirely different
		// direction - 0x324 `Strength`, 0x42F `OffhandChance`, 0x547 `NaturalEnergyRegeneration` - which is what says
		// these four names are the client's own and not a guess.
		//
		// The four together ARE the client's energy simulation (`FUN_0112ad70` listens on exactly these ids and caches
		// them at +0x30/+0x34/+0x38/+0x3c): it holds the pool, its ceiling, and the two rates that move it. That is why
		// sprinting drains the bar with no server traffic at all - the drain is the CLIENT running this model - and why
		// only the SPELLWEAVE drain, which the server drives, shows up on the wire as `DispatchUpdateSprintBar`.
		//
		// ⚠️ Of the four, retail is only ever observed sending <see cref="Energy" /> (1,650 records across the 120-capture
		// corpus, on `StatUpdateIIR` 0x96B8DC59, per entity). `MaxEnergy`, `EnergySprintDrain` and
		// `NaturalEnergyRegeneration` appear ZERO times in any stat block - the only 0x000F425D bytes in the corpus sit
		// inside the retail server's leaked scaffold region on 0xFA015807 spawns (#603), which is not a value it wrote.
		// So the client sources the ceiling and the rates itself; do not invent a push for them.
		Energy = 0xF425C,

		// The DENOMINATOR of the sprint/spellweave bar. `SprintBarView::Update` (FUN_0155b390) re-reads it on every
		// update and the renderer (FUN_0155b570) computes `current / max * 100` only `if (max > 0)` - so a MaxEnergy of
		// zero draws the bar at 0% no matter what current is pushed. Read with kind 2 (base + modifiers), unlike
		// <see cref="Energy" /> which is read raw (kind 0).
		MaxEnergy = 0xF425D,

		// Energy drained per `Energy_StatsTimeBase` while sprinting - the rate the client's local drain runs at.
		EnergySprintDrain = 0xF425F,

		// The energy pool's regeneration rate. Already named the same way by LDB 615 ("Natural Energy Regeneration"),
		// which is the cross-check on the client table above.
		NaturalEnergyRegeneration = 0x547,

		// ---- The rest of the client's own stat-name table (docs/re/aoc-client-stat-names.md). ----
		// AgeOfConan.exe binds every stat id to a NAME in a static metadata table, recovered by
		// tools/ghidra/DumpStatNameTable.java. It is a far wider name source than LDB category 615, which names none of
		// the 1000000+ range and most of the 0x545..0x593 run not at all - and it is self-checking, because its low half
		// reproduces ids we already had from unrelated directions (0x324 Strength, 0x42F OffhandChance, 0x547
		// NaturalEnergyRegeneration). The members ABOVE this block keep the names they were given by whatever decoded
		// them; only ids the enum did not already carry are added here, so nothing is renamed out from under a caller.
		//
		// ⚠ A name here is strong evidence, not a citation. The sweep pairs a PUSH of a string with the following
		// store of an id, so a name is only as good as that adjacency; ids below 768 were dominated by false pairs from
		// an unrelated audio-preset init and are excluded entirely. Verify before relying on one for wire semantics -
		// and note that the client uses `Deaths` for BOTH 0x292 (ours) and 0x542, so 0x542 is deliberately absent.
		HasKnubotData = 0x300,
		GameAction_Icon = 0x301,
		GameAction_Sex = 0x302,
		GameAction_Breed = 0x303,
		GameAction_GfxLife = 0x304,
		GameAction_GfxSize = 0x305,
		GameAction_GfxRed = 0x306,
		GameAction_GfxGreen = 0x307,
		GameAction_GfxBlue = 0x308,
		GameAction_GfxFade = 0x309,
		GameAction_Action = 0x30A,
		GameAction_BodyPart = 0x30B,
		GameAction_Texture = 0x30C,
		GameAction_SeeSkin = 0x30D,
		GameAction_WeaponEffect = 0x30E,
		GameAction_BaseAmount = 0x30F,
		GameAction_RegenAmount = 0x310,
		GameAction_RegenInterval = 0x311,
		GameAction_RarityValue = 0x312,
		GameAction_Cost = 0x313,
		GameAction_Text = 0x314,
		GameAction_Arg1 = 0x315,
		GameAction_Arg2 = 0x316,
		GameAction_Arg3 = 0x317,
		GameAction_Arg4 = 0x318,
		GameAction_Ratio = 0x319,
		GameAction_DamageType = 0x31A,
		GameAction_TimeExist = 0x31B,
		GameAction_Flags = 0x31C,
		GameAction_StrMesh = 0x31D,
		GameAction_StrTexture = 0x31E,
		GameAction_BodyCatMesh = 0x31F,
		QuestBoothDifficulty = 0x320,
		QuestASMinimumRange = 0x321,
		QuestASMaximumRange = 0x322,
		SpellInturruptionModifierPercentage = 0x323,
		Pickpocket = 0x334,
		Bandaging = 0x336,
		Lockpicking = 0x33A,
		RenewMana = 0x340,
		FastHealing = 0x342,
		ManaAttractor = 0x344,
		AddedCorruption = 0x346,
		CorruptionFactor = 0x347,
		MountRules = 0x349,
		GuaranteedCorruption = 0x34A,
		SecondaryResistRating = 0x34B,
		SecondaryMagicRating = 0x34C,
		ATPercentMagicModifier = 0x34D,
		DCPercentMagicModifier = 0x34E,
		AttackAttributeBonus = 0x34F,
		DefenceAttributeBonus = 0x350,
		ATPercentRangeModifier = 0x351,
		DCPercentRangeModifier = 0x352,
		ATPercentMeleeModifier = 0x353,
		DCPercentMeleeModifier = 0x354,
		SpellMagicModifier = 0x355,
		SpellResistModifier = 0x356,
		BlockingBonus = 0x357,
		DisarmResist = 0x358,
		SecondaryAttackRating = 0x359,
		VisAttackRating = 0x35A,
		VisMagicRating = 0x35B,
		VisDefenceRating = 0x35C,
		ImmunityFlags = 0x35E,
		WeaponFlags = 0x35F,
		BleedingResistance = 0x361,
		MagicSiegeDamageModifier = 0x371,
		MagicSlashingDamageModifier = 0x372,
		MagicCrushingDamageModifier = 0x373,
		MagicPiercingDamageModifier = 0x374,
		NpcMeleeWeaponType = 0x375,
		UnarmedSpeed = 0x376,
		NpcUnarmedAttackRange = 0x377,
		VisualLODLevel = 0x378,
		TargetDistanceChange = 0x379,
		ResurrectionPointID = 0x37A,
		ElderUnarmedSpecialization = 0x37B,
		UnarmedCriticalChance = 0x37C,
		BonusPiercingDamage = 0x37D,
		HealthTapAmount = 0x37E,
		ManaTapAmount = 0x37F,
		StaminaTapAmount = 0x380,
		AdditionalAttacks = 0x381,
		AdditionalAttackChance = 0x382,
		TideRequiredDynelID = 0x384,
		SiegeInvulnerabilityRating = 0x38E,
		GenericInvulnerabilityRating = 0x38F,
		BonusCarryingCapacity = 0x390,
		CorruptionPool = 0x391,
		SoulCorruption = 0x392,
		CorruptionPoolModifier = 0x393,
		GemTier = 0x394,
		BuffSlots = 0x395,
		CriticalHealChance = 0x396,
		CriticalBlastChance = 0x397,
		ElderAttackRatingBonus = 0x399,
		ElderMagicRatingBonus = 0x39C,
		CurseResist = 0x3A2,
		AttackRatingProficiency = 0x3B3,
		MagicRatingProficiency = 0x3B5,
		ResistRatingProficiency = 0x3B6,
		EpicRating = 0x3B7,
		ElderPoisonSpecialization = 0x3B8,
		Elder1HEdgedSpecialization = 0x3B9,
		Elder2HEdgedSpecialization = 0x3BA,
		Elder1HBluntSpecialization = 0x3BB,
		Elder2HBluntSpecialization = 0x3BC,
		ElderDaggerSpecialization = 0x3BD,
		NextMailPoint = 0x3BE,
		MailPoints = 0x3BF,
		MentorPupilBonusCasterLevel = 0x3C0,
		MentorPupilBonusMeleeLevel = 0x3C1,
		Slow = 0x3C2,
		BonusCasterLevel = 0x3C3,
		BonusMeleeLevel = 0x3C4,
		BreakingMultiplier = 0x3C6,
		Durability = 0x3C7,
		ComboAttackNumber = 0x3C8,
		DisarmSpeed = 0x3C9,
		LungCapacity = 0x3CA,
		WeaponCriticalChance = 0x3CB,
		WeaponAttribute = 0x3CC,
		WeaponCriticalMultiplier = 0x3CD,
		DamageDeflection = 0x3CE,
		DamageAbsorb = 0x3CF,
		DamageAbsorbCap = 0x3D0,
		MeleeShield = 0x3D1,
		RangedShield = 0x3D2,
		MagicShield = 0x3D3,
		MaxStrengthModifier = 0x3D4,
		MaxConstitutionModifier = 0x3D5,
		MaxDexterityModifier = 0x3D6,
		MaxIntelligenceModifier = 0x3D7,
		MaxWisdomModifier = 0x3D8,
		MaxCharismaModifier = 0x3D9,
		BonusRangedSlashingDamage = 0x3DA,
		BonusRangedPiercingDamage = 0x3DB,
		BonusRangedCrushingDamage = 0x3DC,
		BonusRangedFireDamage = 0x3DD,
		BonusRangedElectricalDamage = 0x3DE,
		BonusRangedHolyDamage = 0x3DF,
		BonusRangedUnholyDamage = 0x3E0,
		BonusRangedPoisonDamage = 0x3E1,
		BonusRangedSiegeDamage = 0x3E2,
		WeaponBonusSlashingDamage = 0x3E3,
		WeaponBonusPiercingDamage = 0x3E4,
		WeaponBonusCrushingDamage = 0x3E5,
		WeaponBonusFireDamage = 0x3E6,
		StreamCheckMagic = 0x3E7,
		WeaponBonusHolyDamage = 0x3E8,
		WeaponBonusUnholyDamage = 0x3E9,
		WeaponBonusPoisonDamage = 0x3EA,
		WeaponBonusSiegeDamage = 0x3EB,
		BonusMeleeSlashingDamage = 0x3EC,
		BonusMeleePiercingDamage = 0x3ED,
		BonusMeleeCrushingDamage = 0x3EE,
		BonusMeleePoisonDamage = 0x3F3,
		BonusMeleeSiegeDamage = 0x3F4,
		PercentSlashingDamage = 0x3F5,
		PercentPiercingDamage = 0x3F6,
		PercentCrushingDamage = 0x3F7,
		PercentFireDamage = 0x3F8,
		PercentElectricalDamage = 0x3F9,
		PercentHolyDamage = 0x3FA,
		PercentUnholyDamage = 0x3FB,
		PercentPoisonDamage = 0x3FC,
		PercentSiegeDamage = 0x3FD,
		TotalDamage = 0x3FE,
		TrackSlashingDamage = 0x3FF,
		TrackPiercingDamage = 0x400,
		TrackCrushingDamage = 0x401,
		TrackFireDamage = 0x402,
		TrackElectricalDamage = 0x403,
		TrackHolyDamage = 0x404,
		TrackUnholyDamage = 0x405,
		TrackPoisonDamage = 0x406,
		TrackSiegeDamage = 0x407,
		ChallengePet = 0x408,
		SnareEffect = 0x409,
		MaxTeamSize = 0x40A,
		DualWieldProficiency = 0x40C,
		CombatFlags = 0x40E,
		ShieldingBonus = 0x40F,
		BaseSpellDamage = 0x411,
		MaxNumberOfPupils = 0x412,
		NoiseLevel = 0x413,
		HearingSkill = 0x414,
		SmellLevel = 0x415,
		SmellingSkill = 0x416,
		ElfLevel = 0x417,
		ElfSensingSkill = 0x418,
		SightSkill = 0x419,
		CarsGroup = 0x41A,
		AttackUpLeft = 0x41B,
		AttackUpRight = 0x41C,
		AttackHead = 0x41D,
		AttackDownLeft = 0x41E,
		AttackDownRight = 0x41F,
		AttackThrust = 0x420,
		DefenseUpLeft = 0x421,
		DefenseUpRight = 0x422,
		DefenseHead = 0x423,
		DefenseDownLeft = 0x424,
		DefenseDownRight = 0x425,
		DefenseThrust = 0x426,
		DefenseBack = 0x427,
		NpcRangedWeaponRange = 0x428,
		NpcRangedWeaponSpeed = 0x429,
		NpcRangedWeaponDamageType = 0x42A,
		NpcRangedWeaponCritChance = 0x42B,
		NpcRangedWeaponCritMultiplier = 0x42C,
		NpcRangedWeaponMinDamage = 0x42D,
		NpcRangedWeaponMaxDamage = 0x42E,
		ItemScryScriptGroupID = 0x430,
		TotalDamageDoneThisCombat = 0x431,
		TotalSecondsThisCombat = 0x432,
		TotalDamageDoneLastCombat = 0x433,
		TotalSecondsLastCombat = 0x434,
		WeaponTrailStartColor = 0x435,
		WeaponTrailEndColor = 0x436,
		WeaponTrailStartAlpha = 0x437,
		WeaponTrailEndAlpha = 0x438,
		WeaponTrailTime = 0x439,
		AttackIgnoreLevel = 0x43A,
		Endurance = 0x43B,
		MaxEndurance = 0x43C,
		MaxRunspeed = 0x43E,
		Subtlety = 0x43F,
		MaxSubtlety = 0x440,
		Recovery = 0x441,
		MaxRecovery = 0x442,
		FastRecovery = 0x443,
		MaxFastRecovery = 0x444,
		MaxSkulk = 0x446,
		PvPImmunityRating = 0x44C,
		Type = 0x44D,
		Instance = 0x44E,
		None = 0x44F,
		HairMesh = 0x450,
		BeardMesh = 0x451,
		DummyStringStat = 0x452,
		DummyFloatStat = 0x453,
		GameAction_TokenStateType = 0x454,
		EffectPackage = 0x455,
		GameAction_Hash = 0x456,
		GemFamily = 0x457,
		GameAction_ToClient = 0x45A,
		GameAction_MinLevel = 0x45B,
		GameAction_MaxLevel = 0x45C,
		GameAction_MonsterData = 0x45D,
		GameAction_Cluster = 0x45E,
		GameAction_Level = 0x45F,
		GameAction_Seed = 0x460,
		GameAction_AttractorID = 0x461,
		GameAction_GfxID = 0x462,
		GameAction_NanoTemplate = 0x463,
		GameAction_GfxColor = 0x464,
		GameAction_DestDoorID = 0x465,
		GameAction_NextDoorID = 0x466,
		GameAction_NPCSelectGroup = 0x467,
		GameAction_NPCSelectMode = 0x468,
		GameAction_DestinationProxyModelType = 0x469,
		GameAction_DestinationProxyModelInstance = 0x46A,
		GameAction_DestinationProxyGroupInstance = 0x46B,
		GameAction_DestinationProxySubgroupInstance = 0x46C,
		GameAction_LocalizerType = 0x46D,
		GameAction_LocalizerInstance = 0x46E,
		GameAction_TeleportLinePromille = 0x46F,
		GameAction_TeleportHeightPromille = 0x470,
		GameAction_Side = 0x471,
		GameAction_NPCMaster = 0x472,
		GameAction_WeaponTemplate1 = 0x473,
		GameAction_WeaponTemplate2 = 0x474,
		GameAction_Command = 0x475,
		GameAction_CommandArg = 0x476,
		GameAction_ToAllPets = 0x477,
		GameAction_NPCNanoFormulaCatagory = 0x478,
		GameAction_Class = 0x479,
		GameAction_Multiplier = 0x47A,
		GameAction_CameraLockLookat = 0x47B,
		GameAction_CameraEnableZoom = 0x47C,
		GameAction_CameraAbruptness = 0x47D,
		GameAction_School = 0x47E,
		GameAction_FormatType = 0x47F,
		GameAction_NPCMoveMode = 0x480,
		GameAction_NPCStuckResolveScheme = 0x481,
		GameAction_TauntWhoseHate = 0x482,
		GameAction_TauntType = 0x483,
		GameAction_BecomePet = 0x484,
		GameAction_Signal = 0x485,
		GameAction_BadGuy = 0x486,
		GameAction_SideFilter = 0x487,
		GameAction_OnlyToTarget = 0x488,
		GameAction_ToTexture = 0x489,
		GameAction_NPCStuckDetectScheme = 0x48A,
		GameAction_AutoLockTime = 0x48B,
		GameAction_AutoUnlockTime = 0x48C,
		GameAction_OpenAlso = 0x48D,
		GameAction_InterruptType = 0x48E,
		GameAction_OverrideMostHatedTarget = 0x48F,
		GameAction_PetReq1 = 0x490,
		GameAction_PetReq2 = 0x491,
		GameAction_PetReq3 = 0x492,
		GameAction_PetReqVal1 = 0x493,
		GameAction_PetReqVal2 = 0x494,
		GameAction_PetReqVal3 = 0x495,
		GameAction_RelXPos2 = 0x496,
		GameAction_RelYPos2 = 0x497,
		GameAction_RelZPos2 = 0x498,
		GameAction_InFight = 0x499,
		GameAction_Angle = 0x49A,
		GameAction_SpeedLeft = 0x49B,
		GameAction_SpeedRight = 0x49C,
		GameAction_MovementAction = 0x49D,
		GameAction_Rotation = 0x49E,
		GameAction_Strain = 0x49F,
		GameAction_StartGfxRed = 0x4A0,
		GameAction_StartGfxGreen = 0x4A1,
		GameAction_StartGfxBlue = 0x4A2,
		GameAction_StartGfxAlpha = 0x4A3,
		GameAction_StopGfxRed = 0x4A4,
		GameAction_StopGfxGreen = 0x4A5,
		GameAction_StopGfxBlue = 0x4A6,
		GameAction_StopGfxAlpha = 0x4A7,
		GameAction_NumRepetitions = 0x4A8,
		GameAction_Profession = 0x4A9,
		GameAction_AddHate = 0x4AA,
		GameAction_ScaleHate = 0x4AB,
		GameAction_TmpHate = 0x4AC,
		GameAction_TmpHateTimeout = 0x4AD,
		GameAction_Randomness = 0x4AE,
		GameAction_Min1 = 0x4AF,
		Max = 0x4B0,
		GameAction_Min2 = 0x4B1,
		GameAction_Max1 = 0x4B2,
		GameAction_Max2 = 0x4B3,
		GameAction_StartLevel = 0x4B4,
		GameAction_EndLevel = 0x4B5,
		GameAction_Value1 = 0x4B6,
		GameAction_Value2 = 0x4B7,
		GameAction_Level1 = 0x4B8,
		GameAction_Level2 = 0x4B9,
		GameAction_WeaponSlot = 0x4BA,
		GameAction_AttackLocation = 0x4BB,
		GameAction_AttackRating = 0x4BC,
		GameAction_AreaType = 0x4BD,
		GameAction_AreaSize1Start = 0x4BE,
		GameAction_AreaSize2Start = 0x4BF,
		GameAction_AreaSize1End = 0x4C0,
		GameAction_AreaSize2End = 0x4C1,
		GameAction_TemplateFlag = 0x4C2,
		GameAction_TemplateID = 0x4C3,
		GameAction_Strength = 0x4C4,
		GameAction_Horizontal_Dir = 0x4C5,
		GameAction_JumpHeight = 0x4C6,
		GameAction_PowerRating = 0x4C7,
		GameAction_RunOnTarget = 0x4C8,
		GameAction_LocationID = 0x4C9,
		GameAction_MeshID = 0x4CA,
		GameAction_RotX = 0x4CB,
		GameAction_RotY = 0x4CC,
		GameAction_RotZ = 0x4CD,
		GameAction_DisplaceMentX = 0x4CE,
		GameAction_DisplaceMentY = 0x4CF,
		GameAction_DisplaceMentZ = 0x4D0,
		GameAction_SizeScale = 0x4D1,
		GameAction_CameraID = 0x4D2,
		GameAction_TransitionID = 0x4D3,
		GameAction_TransitionTime = 0x4D4,
		GameAction_DelayTime = 0x4D5,
		GameAction_CollisionFlag = 0x4D6,
		GameAction_TargetingInfo = 0x4D7,
		GameAction_MeshType = 0x4D8,
		GameAction_String0 = 0x4D9,
		GameAction_String1 = 0x4DA,
		GameAction_MinMembers = 0x4DB,
		GameAction_AngleStart = 0x4DC,
		GameAction_Degrees = 0x4DD,
		GameAction_DamageIncreasePercentStart = 0x4DE,
		GameAction_DpsIncreaseStart = 0x4DF,
		GameAction_CritChangeModStart = 0x4E0,
		GameAction_DamageIncreasePercentEnd = 0x4E1,
		GameAction_DpsIncreaseEnd = 0x4E2,
		GameAction_CritChangeModEnd = 0x4E3,
		GameAction_StartLevel_group1 = 0x4E4,
		GameAction_EndLevel_group1 = 0x4E5,
		GameAction_AddHateStart_group1 = 0x4E6,
		GameAction_AddHateEnd_group1 = 0x4E7,
		GameAction_DPSIncreaseStart_group1 = 0x4E8,
		GameAction_DPSIncreaseEnd_group1 = 0x4E9,
		GameAction_CritChanceModStart_group1 = 0x4EA,
		GameAction_CritChanceModEnd_group1 = 0x4EB,
		GameAction_StartLevel_group2 = 0x4EC,
		GameAction_EndLevel_group2 = 0x4ED,
		GameAction_AddHateStart_group2 = 0x4EE,
		GameAction_AddHateEnd_group2 = 0x4EF,
		GameAction_DPSIncreaseStart_group2 = 0x4F0,
		GameAction_DPSIncreaseEnd_group2 = 0x4F1,
		GameAction_CritChanceModStart_group2 = 0x4F2,
		GameAction_CritChanceModEnd_group2 = 0x4F3,
		GameAction_NanoTemplate2 = 0x4F4,
		GameAction_RatePerMinute = 0x4F5,
		GameAction_Distance = 0x4F6,
		GameAction_HateValue = 0x4F7,
		GameAction_HateType = 0x4F8,
		GameAction_HateModification = 0x4F9,
		GameAction_HateSetSize = 0x4FA,
		GameAction_BccEvent = 0x4FB,
		GameAction_DamageMultiplier = 0x4FC,
		GameAction_CanUseIfMounted = 0x4FD,
		GameAction_PerformAttack = 0x4FE,
		GameAction_MovementMultiplier = 0x4FF,
		GameAction_TemplateID2 = 0x500,
		GameAction_Fade = 0x501,
		GameAction_Ascending = 0x502,
		GameAction_BindAtLocation_x = 0x503,
		GameAction_BindAtLocation_y = 0x504,
		GameAction_BindAtLocation_z = 0x505,
		GameAction_PoseEvent = 0x506,
		ValorRenownModifier = 0x507,
		GloryRenownModifier = 0x508,
		ArtistryRenownModifier = 0x509,
		ItemSaleID = 0x50A,
		SpellLine2 = 0x50B,
		SpellLine3 = 0x50C,
		SpellLine4 = 0x50D,
		SpellLine5 = 0x50E,
		GameAction_ItemStack = 0x50F,
		ACGItemTemplateID3 = 0x510,
		FacebookWebID = 0x511,
		LuaScriptID = 0x512,
		SpellLine6 = 0x513,
		SpellLine7 = 0x514,
		SpellLine8 = 0x515,
		MaxBuffCounters = 0x516,
		BuffCounterDecayCombat = 0x517,
		BuffCounterDecayNotCombat = 0x518,
		GameAction_FloatValue1 = 0x519,
		GameAction_FloatValue2 = 0x51A,
		GameAction_FloatMultiplier = 0x51B,
		LooksPacket = 0x51C,
		FloatModifier = 0x51D,
		GameAction_Time = 0x51E,
		TokenSellToVendorPrice1 = 0x51F,
		TokenSellToVendorPrice2 = 0x520,
		TokenSellToVendorType1 = 0x521,
		TokenSellToVendorType2 = 0x522,
		QuestTaskID = 0x523,
		QuestTaskKeepFlags = 0x524,
		CraftingRecipeMark = 0x525,
		CoreLuaScriptID = 0x526,
		PrefixLuaScriptID = 0x527,
		SuffixLuaScriptID = 0x528,
		Phase = 0x529,
		CraftingDisassemblyID = 0x52A,
		LootPermissions1 = 0x52B,
		LootPermissions2 = 0x52C,
		MinimumHealthCap = 0x52D,
		LootDropChance = 0x52E,
		GhostingDisabled = 0x531,
		ExpandQuestInventory = 0x532,
		ExpandResourceInventory = 0x533,
		BeardType = 0x535,
		CharVisualRadius = 0x536,
		GameAction_PrefixHash = 0x537,
		GameAction_SuffixHash = 0x538,
		GameAction_StackSize = 0x539,
		PowerRank = 0x53A,
		PvPPerformance = 0x53B,
		DamageDone = 0x53C,
		HealingDone = 0x53D,
		CrowdControlDone = 0x53E,
		DamageAbsorbed = 0x53F,
		EnemyFlagsTaken = 0x540,
		KillingBlows = 0x541,
		TimeInDominationZone = 0x543,
		ItemEquipSkill = 0x544,
		ResurrectionTime = 0x545,
		ResurrectionTimePvP = 0x546,
		TagCounterStat = 0x548,
		TagCounterValue = 0x549,
		HitCollision = 0x54A,
		SprintDrain = 0x54B,
		PercentRemainingStamina = 0x54C,
		PercentRemainingMana = 0x54D,
		GemsReferenceID1 = 0x54E,
		GemsReferenceID2 = 0x54F,
		GemsReferenceID3 = 0x550,
		GemScale = 0x551,
		VisualItemOverride = 0x552,
		CraftingMaterialType = 0x553,
		ItemOrigin = 0x554,
		BaseCraftingReward = 0x555,
		BaseCraftingCost = 0x556,
		CraftingRewardPoints = 0x557,
		CraftingResourcePoints = 0x558,
		CraftingResourceLastUpdate = 0x559,
		CraftingResourcePointsUpgrade = 0x55A,
		CraftingResourcePointsRegenRate = 0x55B,
		CraftingResourcePointsMax = 0x55C,
		CraftingResourcePointsRegenUpgrades = 0x55D,
		CraftingCriticalUpgrades = 0x55E,
		SalvageCriticalUpgrades = 0x55F,
		NoXPGain = 0x561,
		PvEFatalities = 0x563,
		TimesReported = 0x565,
		ArenaPvPKillsSeasonal = 0x568,
		PvPConquestMatchesWonSeasonal = 0x56C,
		PvPTournamentMatchesWonSeasonal = 0x56D,
		CounterAlcoholConsumptionSeasonal = 0x56F,
		PvPConquestMatchesLostSeasonal = 0x572,
		DEPRECATED_MurderPointsSeasonal = 0x574,
		Difficulty = 0x576,
		GroupSize = 0x577,
		HardcoreLevel = 0x578,
		SelectedBadgeTag = 0x579,
		IconFile = 0x57A,
		GameAction_DestinationProxyDifficultyLevel = 0x57C,
		SpawnLuaScriptId = 0x57D,
		ControlPointValue = 0x57E,
		DifficultyOverride = 0x57F,
		HardcoreLevelSeasonal = 0x580,
		AATokensGainedSeasonal = 0x581,
		DungeonBossesKilled = 0x582,
		OnslaughtKuthchemesHighestWave = 0x583,
		OnslaughtWavesCompleted = 0x584,
		OnslaughtEnemiesKilled = 0x585,
		OnslaughtSpecialsKilled = 0x586,
		OnslaughtBossesKilled = 0x587,
		OnslaughtWildlifeCampsCleared = 0x588,
		LargestKillStreak = 0x589,
		OnslaughtLongestWaveStreak = 0x58A,
		ForceDSLayerThree = 0x58B,
		OnslaughtKuthchemesHighestWaveSeasonal = 0x58C,
		Onslaught2HighestWave = 0x58D,
		Onslaught2HighestWaveSeasonal = 0x58E,
		Onslaught2WavesCompleted = 0x58F,
		Onslaught2EnemiesKilled = 0x590,
		Onslaught2SpecialsKilled = 0x591,
		Onslaught2BossesKilled = 0x592,
		Onslaught2LongestWaveStreak = 0x593,
		PowerLevelLight = 0xF4241,
		CombatStance = 0xF4242,
		AACurrentFeatPerfected = 0xF4243,
		AAFeatPerfectionTimeStamp = 0xF4244,
		AATimeSpentPerfectingFeats = 0xF4245,
		AAPvPXP = 0xF4246,
		AAPvEXP = 0xF4247,
		CraftingRenownGain = 0xF4248,
		CraftingTokenType1 = 0xF4249,
		CraftingTokenAmount1 = 0xF424A,
		CraftingTokenType2 = 0xF424B,
		CraftingTokenAmount2 = 0xF424C,
		PerkLine = 0xF424D,
		PerkSize = 0xF424E,
		SubscriptionFlagUpdateTimer = 0xF424F,
		CriticalMitigationChance = 0xF4250,
		CriticalInsuredValue = 0xF4251,
		CriticalMitigationAmount = 0xF4252,
		DamageBarrier = 0xF4253,
		AAFeatPerfectionTimeRemaining = 0xF4254,
		PvEAAXPModifier = 0xF4255,
		PvPAAXPModifier = 0xF4256,
		VanityArmorFilter = 0xF4257,
		VanityOccupySlots = 0xF4258,
		PvPTournamentMatchesWon = 0xF4259,
		PvPConquestMatchesWon = 0xF425A,
		PvPConquestMatchesLost = 0xF425B,
		ExpandedBankInventory = 0xF425E,
		ActiveFeatSpec = 0xF4260,
		NumFeatSpecs = 0xF4261,
		CraftingCritical = 0xF4262,
		SalvageCritical = 0xF4263,
		GemEffectId = 0xF4264,
		GemEffectTemplate = 0xF4265,
		AchievementPoints = 0xF4266,
		CounterUndeadKills = 0xF4267,
		CounterDemonKills = 0xF4268,
		CounterAcheronianKills = 0xF4269,
		CounterPictKills = 0xF426A,
		CounterSerpentManKills = 0xF426B,
		CounterHyperboreanKills = 0xF426C,
		CounterPvPMatchesCompleted = 0xF426D,
		CounterRarePet = 0xF426E,
		CounterPet = 0xF426F,
		CounterMount = 0xF4270,
		CounterAlcoholConsumption = 0xF4271,
		CounterPvPAssassinations = 0xF4272,
		CounterPvPGullahWins = 0xF4273,
		CounterPvPFestivalEvents = 0xF4274,
		CounterPvPArenaWins = 0xF4275,
		CounterPvPTeamArenaWins = 0xF4276,
		CounterPvPFoSWins = 0xF4277,
		TagIsAccountWide = 0xF4278,
		CounterNemedianKills = 0xF4279,
		CounterVanirKills = 0xF427A,
		CounterInsectKills = 0xF427B,
		CounterPreyKills = 0xF427C,
		CounterCorruptedKills = 0xF427D,
		ArenaPvEKills = 0xF427E,
		ArenaPvPKills = 0xF427F,
		ArenaPvEFatalities = 0xF4280,
		ArenaPvPFatalities = 0xF4281,
		DailyResetTimestamp = 0xF4282,
		WeeklyResetTimestamp = 0xF4283,
		MonthlyResetTimestamp = 0xF4284,
		DailyPvEKills = 0xF4285,
		DailyPvPKills = 0xF4286,
		DailyMinigameWins = 0xF4287,
		DailyQuestsCompleted = 0xF4288,
		DailyUnchainedDungeonsCompleted = 0xF4289,
		DailyDailyCounter = 0xF428A,
		DailyWeeklyCounter = 0xF428B,
		DailyMonthlyCounter = 0xF428C,
		CounterDailyChallenges = 0xF428D,
		CurrentSeason = 0xF428E,
		AchievementPointsSeasonal = 0xF428F,
		DungeonBossesKilledSeasonal = 0xF4290,
		PlayerFaction = 0x1E84DC,
		Mesh2 = 0x1E84F7,
		LootTable = 0x1E850B,
		LootTable2 = 0x1E861D,
		LootTable3 = 0x1E861E,
		LootTable4 = 0x1E861F,
		LootTable5 = 0x1E8620,
		ItemAbility = 0x1E863F,
		FadeInOverride = 0x1E8655,
		SelectedTag = 0x1E86AE,
		TagId = 0x1E86B0,
	}

	/// <summary>
	///     The wire scale a stat's player-facing value is multiplied by - vitals and most percents ×100, attributes
	///     ×10, five composed ids ×10000, the rest ×1. <see cref="CharacterStatBlock" />.Set applies it, so callers
	///     pass DISPLAY values.
	/// </summary>
	/// <remarks>
	///     <b>This is no longer a hand-maintained list.</b> It forwards to <see cref="StatDisplayScale" />, which is the
	///     client's own rule transcribed from <c>AgeOfConan.exe</c>'s tooltip renderer (#251) and therefore covers every
	///     stat id, not just the ones somebody has hovered. The remark this used to carry - "assume an id absent from
	///     this switch is UNMEASURED rather than known to be ×1", after
	///     <see cref="CharacterStatId.WeaponDamageMelee" /> sat in the enum but not the table and would have applied
	///     +3800% where the client draws +38% - no longer applies: an id not in either percent set is ×1 because the
	///     client prints it through its integer formatter.
	///     <para>
	///         The 27 values that WERE established by hand (the character sheet via <c>.setstat</c>, and item/feat
	///         tooltips) are kept as the cross-check in <c>StatDisplayScaleTests</c> - all 27 reproduce, which is what
	///         licenses replacing the measured table with the transcribed one.
	///     </para>
	/// </remarks>
	public static class CharacterStatIdExtensions
	{
		public static uint GetScale(this CharacterStatId statId)
		{
			return StatDisplayScale.Of((uint)statId);
		}
	}
}
