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

		// ⭐ The entity's BEHAVIOUR-TREE STATE - the client calls this slot "BCC State" and it is right; our name is
		// the narrow one, kept only because every caller so far uses it for the weapon stance. Pushed in a 0x96B8DC59
		// update; `4` the tick before a mob's first swing is what DRAWS its weapon, and without it an NPC swings
		// empty-handed (#218).
		//
		// ⭐ **The values are STATE ids out of the client's own behaviour trees** (RDB 1010203, plain-text XML -
		// `--report bcc-trees --report-arg <ids>`), which decodes every value the corpus carries (#905):
		//
		//    1  Idle          28 trees      9  Sitting       16 trees      39  Spellweaving   Character
		//    2  Movement      11 trees     29  Limbo         24 trees      41  Drunk          Character
		//    4  Fighting      22 trees     69  Evade          3 trees      72  Talking_idle   Character
		//
		// - `1` is **Idle**, NOT "the player is in combat" as this comment used to say. The player's captured 1 is the
		//   client sitting in its idle state.
		// - `29` is **Limbo**, which is the client's own name for death (protocol_death_resurrect).
		// - `9` is **Sitting** (#355's `/sit`), `39` **Spellweaving** - the state `combos.md` already keys the
		//   `Spellweaving Remover` criterion off (`!= 39`) without knowing what the number was.
		//
		// ⚠️ **`0` is not a state.** No shipped tree declares id 0, so pushing 0 to sheathe a weapon - which this
		// comment used to prescribe, inferred from "idle entities carry no record at all" - was sending a value retail
		// has no meaning for. The sheathe value is `1` (Idle).
		//
		// ⚠️ State ids COLLIDE across trees (11 is `LoopingEmote` in Animal/Character and `SneakHiding` in seven
		// others), so a value only resolves against the tree the entity actually runs. The table above lists the ids
		// whose reading is unambiguous or whose tree is named.
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
		// The PvP CAREER counters. ⭐ Renamed from `Kills`/`Deaths` in #625, on three independent statements that
		// they are the PvP totals and not general ones:
		//   * the exe's compiled registry calls them `PvPTotalKills` / `PvPTotalDeaths` (docs/re/aoc-client-stat-names.tsv),
		//     and this ticket established that the registry beats LDB 615 wherever they differ;
		//   * CONTIGUITY - 0x291 `PvPFinishingBlows`, 0x293 `PvPMatchesWon` and 0x294 `PvPMatchesLost` are ids this
		//     enum ALREADY agreed with the registry on, so a bare `Deaths` at 0x292 was a hole punched in an
		//     otherwise-matching run. That is the same argument that settled the 0x45B-0x461 misplacement;
		//   * ⭐ RETAIL'S OWN BLOCK - the captured `SubClassStatBlock` pairs each of these with its `*Seasonal` twin
		//     (0x566 `PvPTotalKillsSeasonal`, 0x567 `PvPTotalDeathsSeasonal`) four slots apart. A seasonal twin's
		//     partner is the career total of the SAME thing, which is wire evidence rather than a name.
		//
		// ⚠️ The server increments these on EVERY kill and death, PvE included, while the client has separate
		// `NPCKills` (0x298) / `NPCDeaths` (0x299) counters - see #808. The names are correct; what rides them is not.
		PvPTotalKills = 0x0290,
		PvPTotalDeaths = 0x0292,
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
		/// <summary>
		///     The spawn record's <c>CharacterStats.flags</c> (protobuf field 16) verbatim - the client pushes the whole
		///     word into the character under this id. Bit 3 = a trade session is open, 5 = a teleport is in flight,
		///     22 = the first/last name fields are populated, 28 = NPC (which the client always recomputes locally
		///     rather than trusting ours). See <c>docs/re/corecharacter-flags-bitfield.md</c>.
		/// </summary>
		Flags = 0x000,
		Features = 0x0E0,
		PreviousMovementMode = 0x0AE,
		CurrentMovementMode = 0x0AD,
		DeathEffectXp = 0x40B,
		HeroicMode = 0x0A9,
		ExpandBankInventory = 0x534,
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
		PowerSessionRemaining = 0x06A,
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
		PvPMatchesAbandonedSeasonal = 0x575,
		MinigamesAbandoned = 0x560,
		PvPSiegeMatchesLostSeasonal = 0x573,
		SiegeLosses = 0x29F,
		PvPSiegeMatchesWonSeasonal = 0x56E,
		SiegeWins = 0x29E,
		PvPCTFMatchesLostSeasonal = 0x571,
		PvPCTFMatchesWonSeasonal = 0x56B,
		PvPAnnihilationMatchesLostSeasonal = 0x570,
		PvPAnnihilationMatchesWonSeasonal = 0x56A,
		PvPFatalitiesSeasonal = 0x569,
		Fatalities = 0x564,
		PvPTotalDeathsSeasonal = 0x567,
		PvPTotalKillsSeasonal = 0x566,

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
		// ⚠️ `Duration` is the name LDB category 615 gives stat 8, and 615 is the right source: it is the DISPLAY name,
		// shipped in seven languages, and it is what the client's own tooltip prints. The exe's static stat-name table
		// and LDB 633 call it `TimeExist` instead - those carry INTERNAL identifiers and are not a safe citation for
		// semantics (#625). `1037` is the proof: 615 says `Knockback Resistance` in all seven languages while 633/the
		// exe table say `Weight`, and our KnockbackService depends on that reading.
		//
		// Separately, and independent of the name: a `Set` of this stat (GameAction 53026, 100 rows) does NOT set the
		// carrier's own `Spell.DurationMs` - 16 of 100 agree even allowing a centisecond scale, and there is zero
		// overlap with the 335 spells carrying a summon. What it does set is open in #711.
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
		// only the SPELLWEAVE drain, which the server drives, shows up on the wire as `DispatchUpdateSprintbar`.
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
		// an unrelated audio-preset init and are excluded entirely. Verify before relying on one for wire semantics.
		//
		// ⚠ The sub-768 exclusion is LIFTED - see the block at the bottom of this enum (#625). And the note that used
		// to sit here, "the client uses `Deaths` for BOTH 0x292 and 0x542", was an artifact of the old sweep's
		// mispairing: the client calls 0x292 `PvPTotalDeaths` and 0x542 `Deaths`, two distinct names. Both are now
		// carried under those names - 0x292 above, 0x542 in the minigame scoreboard run below.
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

		// ⭐ 0x43B..0x446 (1083..1094) are SIX consecutive value/Max PAIRS - Endurance, Runspeed, Subtlety,
		// Recovery, FastRecovery, Skulk - and that structure is the point (#468). Feat records carry the family with
		// values 100..900, and because LDB 615 (a stale Anarchy Online display table) names none of them they read as
		// junk that a stat WHITELIST would silence. The exe's own descriptor registry names all twelve
		// (docs/re/aoc-client-stat-names.tsv), so they are deliberate stealth/movement stats, not noise.
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

		// ⭐ 0x45B..0x461 are `GameAction_*`, restored 2026-08-11 (#625). They were renamed to a
		// `PvP*InvulnerabilityRating` series on 2026-08-05 on the authority of **LDB 615** - which is the source that
		// loses. The exe's descriptor registry (`FUN_01db2740`, compiled with this build) names them
		// `MinLevel`/`MaxLevel`/`MonsterData`/`Cluster`/`Level`/`Seed`/`AttractorID`, and two independent things say
		// the LDB is simply wrong here rather than merely differently worded:
		//
		//   CONTIGUITY  0x454..0x57C is an unbroken `GameAction_*` run - 200+ consecutive ids where this enum and the
		//               registry agree exactly. Seven `PvP*` names punched into the middle of it is a discontinuity;
		//               seven more GameAction parameters are not.
		//   COMPLETENESS The PvP invulnerability family has NINE damage schools plus siege, and the registry has all
		//               of them, at 0xE4..0xEA + 0xEC + 0x119 + 0x11A (where this enum carries the bare school names
		//               `Slashing`/`Piercing`/... instead). The LDB's copy at 0x45B stops after seven, with no Unholy
		//               and no Poison - an incomplete duplicate of a family that already has a home.
		//
		// So LDB 615 has this family MISPLACED, labelling the GameAction block with names belonging 900 ids earlier.
		// That is the sharpest example of the staleness documented in docs/re/aoc-client-stat-names.md, and it is why
		// the registry is the source to take a stat name from.
		//
		// ⚠️ `GameAction_MinLevel` was a live FALSE LEAD and still is - the name is right, the inference from it was
		// not. #298 was hunting a second, level-based usability gate behind "Your class cannot use this item until
		// level %d" and this slot was the one datum it had. There is no such gate: that message is the LDB token
		// `YouCanNotUseYet`, emitted by the FEAT criterion (`FUN_006dd970` case 0x5d), which compares no level at all.
		// A correct NAME is not a decode of what the field is used for.
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
		// ⭐ The MINIGAME scoreboard's death column, and the id the client actually calls `Deaths` (#625). Its
		// neighbours are what say so: 0x53C-0x543 is one run of per-match scoreboard columns (`DamageDone`,
		// `HealingDone`, `CrowdControlDone`, `DamageAbsorbed`, `EnemyFlagsTaken`, `KillingBlows`, this, and
		// `TimeInDominationZone`), and this enum already carried the two either side of it under the registry's own
		// names. It is a per-match column, NOT the career total - that is `PvPTotalDeaths` (0x292).
		Deaths = 0x542,
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

		// ---- The SUB-768 half of the client's stat-name table (#625). ----
		// The block above stopped at 768 because the sweep keyed on the stored VALUE looking like a stat id, and below
		// 768 the false pairs from a neighbouring audio-preset init dominated it. The sweep now keys on the descriptor
		// SLOT instead - the address the id is written to, at a fixed offset inside a 0xA0-byte record in one of two
		// known global regions - which is a bound on the TABLE rather than a guess about the data, and it yields 1511
		// ids with no duplicates. It also reproduces all 747 of the old names with ZERO disagreements, so this is a
		// strict widening rather than a re-reading. See docs/re/aoc-client-stat-names.md.
		//
		// Same rule as the block above: an id the enum ALREADY carries keeps the name whatever decoded it first, so
		// nothing is renamed out from under a caller - unless the existing name is a GUESS or a stale-source reading,
		// which is what #625 spent its passes separating. `0x292` was the sharp one and is settled: it is
		// `PvPTotalDeaths` (above), which freed the name `Deaths` for the id that actually carries it, `0x542`.
		//
		// ⚠ A name here is strong evidence, not a citation - the same caveat the block above carries.
		AttackSpeed = 0x3,
		Breed = 0x4,
		Team = 0x6,
		State = 0x7,
		MaxConsciousness = 0x9,
		CharacterFlagChecking = 0xA,
		Name = 0xE,
		Info = 0xF,
		Amount = 0x10,
		MemorizationSlot = 0x11,
		SessionTimer = 0x12,
		FeatTrainerID = 0x13,
		SpellCategory = 0x16,
		MaxMass = 0x18,
		StackSize = 0x1A,
		Height = 0x1C,
		DMS = 0x1D,
		Can = 0x1E,
		Archetype = 0x1F,
		DivineFavor = 0x20,
		AccessCount = 0x23,
		AttackCount = 0x24,
		BuildingDamagedMesh = 0x26,
		BuildingDestrRepairMonsterdata = 0x28,
		ReceiverMaterial = 0x29,
		CATMesh = 0x2A,
		ParentType = 0x2B,
		ParentInstance = 0x2C,
		BeltSlots = 0x2D,
		ItemSource = 0x2E,
		ClanLevel = 0x30,
		SpellStatModifierPercent = 0x31,
		InventoryTimeout = 0x32,
		BuildingDmgRepairMesh = 0x35,
		InventoryId = 0x37,
		TimeSinceCreation = 0x38,
		Alignment = 0x3E,
		BuildingDestrRepairMesh = 0x3F,
		HeadMesh = 0x40,
		RecastTime = 0x41,
		CloakLevel = 0x42,
		SpecializedClass = 0x43,
		FactionGroupID = 0x44,
		ChatID = 0x45,
		TotalMass = 0x47,
		ItemType = 0x48,
		RepairDifficulty = 0x49,
		Price = 0x4A,
		ItemClass = 0x4C,
		RepairSkill = 0x4D,
		FormationStatus = 0x4E,
		PrimaryItemType = 0x50,
		PrimaryItemInstance = 0x51,
		SecondaryItemType = 0x52,
		SecondaryItemInstance = 0x53,
		UserType = 0x54,
		UserInstance = 0x55,
		AreaType = 0x56,
		AreaInstance = 0x57,
		DefaultPos = 0x58,
		Race = 0x59,
		GuildID = 0x5C,
		NpcTitle = 0x5D,
		PrimaryUnequippVisualAttractor = 0x5E,
		SecondaryUnequippVisualAttractor = 0x5F,
		PrimaryAlternateVisualAttractor = 0x60,
		SecondaryAlternateVisualAttractor = 0x61,
		StateAction = 0x62,
		FormationPosition = 0x63,
		Bleeding = 0x65,
		BleedingDamage = 0x67,
		NextPowersessionReset = 0x69,
		StunChance = 0x6B,
		StunDuration = 0x6C,
		MaxSlowLimit = 0x6D,
		SpellRadius = 0x6F,
		NPCTemplateInstance = 0x70,
		GemSize = 0x71,
		NumGemSlots = 0x72,
		GemSlot1 = 0x73,
		GemSlot2 = 0x74,
		GemSlot3 = 0x75,
		GemSlot4 = 0x76,
		BuffBreakInterval = 0x77,
		PickPocketCash = 0x78,
		SuperFamily = 0x79,
		MaxMemorizationSlots = 0x7A,
		SpellRank = 0x7B,
		AISpellCategory = 0x7C,
		AIPowerrating = 0x7D,
		TargetingMode = 0x7E,
		SpellLine = 0x80,
		IntoxicationLevel = 0x82,
		EntrancePlayfield = 0x85,
		EquippedLayerID = 0x86,
		Craftmanship = 0x88,
		MaxCraftmanship = 0x89,
		Disruptive = 0x8A,
		Taunt = 0x8E,
		SoulFragments = 0x90,
		SavableSubStates = 0x91,
		RespawnTimeInSeconds = 0x92,
		OccupySlots = 0x94,
		CastingLevel = 0x96,
		CastingLevelModifier = 0x97,
		PenetrationRating = 0x98,
		InterruptionReduction = 0x99,
		ShortcutPosition = 0x9A,
		ProjectileMesh = 0x9B,
		StaggeringChance = 0x9C,
		NPCRangedWeaponType = 0x9F,
		BonusRangedFrostDamage = 0xA0,
		WeaponBonusFrostDamage = 0xA1,
		PercentFrostDamage = 0xA3,
		DrunkBrawlWins = 0xA4,
		TrackFrostDamage = 0xA6,
		Attackable = 0xA8,
		MinCastingTimeForExecute = 0xAB,
		WaypointType = 0xAC,
		AutoLockTimeDefault = 0xAF,
		AutoUnlockTimeDefault = 0xB0,
		MoreFlags = 0xB1,
		FamePoints = 0xB2,
		NPCFlags = 0xB3,
		CurrentBuffSlots = 0xB4,
		MaxBuffSlots = 0xB5,
		Specialization = 0xB6,
		EffectIcon = 0xB7,
		BuildingType = 0xB8,
		BuildingInstance = 0xB9,
		MountGroup = 0xBC,
		ExitInstance = 0xBD,
		NextDoorInBuilding = 0xBE,
		ExtenalPlayfieldInstance = 0xC0,
		ExtenalDoorInstance = 0xC1,
		InPlay = 0xC2,
		PowerSessionBonusesCounter = 0xC3,
		SpellMaxStackCount = 0xC4,
		OrientationMode = 0xC5,
		SessionTime = 0xC6,
		Brain = 0xC8,
		NPCProfessionType = 0xC9,
		IsQuestItem = 0xCA,
		MinLevel = 0xCB,
		MaxLevel = 0xCC,
		Normalset = 0xCD,
		TradeskillFlags = 0xCE,
		ProgressbarAction = 0xCF,
		GM_DisableTargetSystem = 0xD0,
		BuildingDmgRepairMonsterdata = 0xD1,
		RechargeDelay = 0xD2,
		EquipDelay = 0xD3,
		MaxStackSize = 0xD4,
		TeamSide = 0xD5,
		OverridePlayfieldAttackRules = 0xD6,
		TrapDecalID = 0xDD,
		CanChangeClothes = 0xDF,
		BerserkMode = 0xEB,
		PvpSiegeInvulnerabilityRating = 0xEC,
		GmLDBNametagInstance = 0xED,
		CATMeshFemale = 0xEF,
		SneakingMovementMultiplier = 0xF2,
		SpellDisplayStackSize = 0xF4,
		EvadeGrowth = 0xF5,
		Resilience = 0xF6,
		MaxResilience = 0xF7,
		BirthDate = 0xF8,
		LastSaved = 0xF9,
		GmExcludeFromDataMining = 0xFA,
		AgeAtLastLevelUp = 0xFB,
		MissChance = 0xFC,
		MissLevelGrowth = 0xFD,
		MissionBits1 = 0x100,
		MissionBits2 = 0x101,
		TradepostPrice = 0x102,
		DoorFlags = 0x103,
		QuestStat = 0x105,
		ClientActivated = 0x106,
		PvPUnfinishedMinigames = 0x107,
		PvPKillCountLastReset = 0x108,
		TradepostStartTime = 0x109,
		SoundScript = 0x10A,
		BloodMoney = 0x10B,
		LiquidType = 0x10C,
		CastSound = 0x10E,
		HitSound = 0x110,
		SecondaryItemTemplate = 0x111,
		EquippedWeapons = 0x112,
		PvpManaTapAmount = 0x114,
		PvpStaminaTapAmount = 0x115,
		ItemHateValue = 0x11B,
		DamageBonus = 0x11C,
		MaxDamage = 0x11D,
		MinDamage = 0x11E,
		HateValueModifyer = 0x120,
		TrapDifficulty = 0x121,
		StatOne = 0x122,
		ItemSkill = 0x125,
		ItemOpposedSkill = 0x127,
		InteractionRadius = 0x129,
		Placement = 0x12A,
		LockDifficulty = 0x12B,
		PrestigeClass = 0x12E,
		ClanUpkeep = 0x12F,
		Class = 0x130,
		BloodType = 0x131,
		MinimumAttackRange = 0x132,
		OnLeavePlayfieldTimeStamp = 0x133,
		PvPEvadeRating = 0x135,
		Guile = 0x139,
		MaxGuile = 0x13A,
		SimpleDynelCreationFlags = 0x13B,
		Variance = 0x13C,
		SpeedScaleModifier = 0x13D,
		NPCostModifier = 0x13E,
		SpellParticleEffectSet = 0x141,
		WeaponParticleEffectFire = 0x142,
		WeaponParticleEffectImpact = 0x143,
		Fizzle = 0x144,
		FizzleLevelGrowth = 0x145,
		ImmunityLevelGrowth = 0x147,
		ImmunityGrowth = 0x148,
		ImmunityCap = 0x149,
		DebuffFormula = 0x14C,
		PvP_Rating = 0x14D,
		DoorBlockTime = 0x14F,
		PvPDropChance = 0x150,
		Heroism = 0x151,
		DeathReason = 0x152,
		DamageOverrideType = 0x153,
		SpellUpgrades = 0x154,
		XPBonus = 0x155,
		HealInterval = 0x156,
		HealDelta = 0x157,
		MonsterTexture = 0x158,
		HasAlwaysLootable = 0x159,
		TeleportPauseMilliSeconds = 0x15F,
		SISCap = 0x160,
		AttackType = 0x162,
		Hash = 0x164,
		CollisionRadius = 0x165,
		OuterRadius = 0x166,
		MonsterData = 0x167,
		HitEffectType = 0x169,
		ResurrectDest = 0x16A,
		NanoDelta = 0x16C,
		ReclaimItem = 0x16D,
		GatherEffectType = 0x16E,
		VisualBreed = 0x16F,
		ProfessionAnimation = 0x170,
		VisualSex = 0x171,
		RitualTargetInst = 0x172,
		SkillTimeOnSelectedTarget = 0x173,
		LastSaveXP = 0x174,
		ExtendedTime = 0x175,
		SpellWeaveTime = 0x176,
		ReactiveMeleeChance = 0x178,
		ReactivePvPMeleeChance = 0x179,
		ReactiveMagicChance = 0x17A,
		ReactivePvPMagicChance = 0x17B,
		RangeIncreaserNF = 0x17D,
		SkillLockModifier = 0x17E,
		InterruptChance = 0x17F,
		ACGEntranceStyles = 0x180,
		ChanceOfBreakOnDebuff = 0x182,
		TowerType = 0x184,
		LowresMesh = 0x186,
		CriticalDecrease = 0x187,
		OldTimeExist = 0x188,
		ResistModifier = 0x189,
		SimpleDynelFlags = 0x18A,
		PrimaryTemplateID = 0x18B,
		NumberOfItems = 0x18C,
		SelectedTargetType = 0x18D,
		Corpse_Hash = 0x18E,
		AmmoName = 0x18F,
		Rotation = 0x190,
		CATAnim = 0x191,
		CATAnimFlags = 0x192,
		DisplayCATAnim = 0x193,
		DisplayCATMesh = 0x194,
		School = 0x195,
		NanoSpeed = 0x196,
		NanoPoints = 0x197,
		CommandQueueSlots = 0x198,
		EmitterMaterial = 0x199,
		IsFightingMe = 0x19A,
		VoiceSet = 0x19B,
		SpawnAnimation = 0x19C,
		EffectType = 0x19D,
		ImpactEffectType = 0x19E,
		CorpseType = 0x19F,
		CorpseInstance = 0x1A0,
		CorpseLast = 0x1A1,
		GeneralClientScript = 0x1A2,
		TracerEffectType = 0x1A3,
		AmmoType = 0x1A4,
		CharRadius = 0x1A5,
		CurrentState = 0x1A7,
		ArmourType = 0x1A8,
		RestModifier = 0x1A9,
		BuyModifier = 0x1AA,
		SellModifier = 0x1AB,
		CastEffectType = 0x1AC,
		ProjectilePathType = 0x1AD,
		WaitState = 0x1AE,
		SelectedTarget = 0x1AF,
		ErrorCode = 0x1B0,
		OwnerInstance = 0x1B1,
		CharState = 0x1B2,
		ReadOnly = 0x1B3,
		DamageType = 0x1B4,
		CollideCheckInterval = 0x1B5,
		PlayfieldType = 0x1B6,
		NPCCommand = 0x1B7,
		InitiativeType = 0x1B8,
		CharTmp1 = 0x1B9,
		CharTmp2 = 0x1BA,
		CharTmp3 = 0x1BB,
		CharTmp4 = 0x1BC,
		NPCCommandArg = 0x1BD,
		NameTemplate = 0x1BE,
		ArmorPenetration = 0x1C1,
		SpellPenetration = 0x1C4,
		DamageCapModifier = 0x1C5,
		NPCCommunity = 0x1C7,
		PetType = 0x1C9,
		ODMinSizeAdd = 0x1CB,
		PrimaryEquipVisualAttractor = 0x1CC,
		SecondaryEquipVisualAttractor = 0x1CD,
		DPS = 0x1CE,
		ODMaxSizeAdd = 0x1CF,
		DurationModifier = 0x1D0,
		LOSHeight = 0x1D2,
		LootPassLevel = 0x1D3,
		PetReq2 = 0x1D4,
		PetReq3 = 0x1D5,
		MapOptions = 0x1D6,
		MapAreaPart1 = 0x1D7,
		MapAreaPart2 = 0x1D8,
		FixtureFlags = 0x1D9,
		FallDamageReduction = 0x1DA,
		PvpHealthTapAmountRating = 0x1DE,
		PvpMagicHealthTapAmountRating = 0x1DF,
		ManaTapAmountRating = 0x1E1,
		PvpManaTapAmountRating = 0x1E2,
		PvpMagicManaTapAmountRating = 0x1E3,
		NeverSleeps = 0x1E4,
		PetReqVal1 = 0x1E5,
		PetReqVal2 = 0x1E6,
		PetReqVal3 = 0x1E7,
		Stability = 0x1E9,
		MagicStaminaTapAmountRating = 0x1EC,
		PvpStaminaTapAmountRating = 0x1EE,
		PvpMagicStaminaTapAmountRating = 0x1EF,
		PowerTier = 0x1F0,
		WeaponBonusElectricalDamage = 0x1F1,
		PowerLevel = 0x1F3,
		AnimPos = 0x1F4,
		AnimPlay = 0x1F5,
		AnimSpeed = 0x1F6,
		BonusPoisonDamage = 0x1F7,
		WeaponItemType = 0x1FC,
		QuestIndex0 = 0x1FD,
		QuestTimeout = 0x1FE,
		ExpansionFlags = 0x1FF,
		SubscriptionFlags = 0x200,
		VeteranMonths = 0x201,
		OwnedTowers = 0x202,
		TowerInstance = 0x203,
		AttackShield = 0x204,
		SpecialAttackShield = 0x205,
		NPCVicinityPlayers = 0x206,
		NPCUseFightModeRegenRate = 0x207,
		Rnd = 0x208,
		SocialStatus = 0x209,
		LastRnd = 0x20A,
		ItemDelayCap = 0x20B,
		RechargeDelayCap = 0x20C,
		PercentRemainingHealth = 0x20D,
		PercentRemainingNano = 0x20E,
		TargetDistance = 0x20F,
		TeamCloseness = 0x210,
		NumberOnHateList = 0x211,
		ConditionState = 0x212,
		ExpansionPlayfield = 0x213,
		ShadowBreed = 0x214,
		MagicHealthTapAmount = 0x215,
		DudChance = 0x216,
		HealMultiplier = 0x217,
		SpellVulnerability = 0x219,
		AmsCap = 0x21A,
		ProcInitiative1 = 0x21B,
		ProcInitiative2 = 0x21C,
		ProcInitiative3 = 0x21D,
		ProcInitiative4 = 0x21E,
		FactionModifier = 0x21F,
		MagicManaTapAmount = 0x220,
		MagicStaminaTapAmount = 0x221,
		StackingLine2 = 0x222,
		StackingLine3 = 0x223,
		StackingLine4 = 0x224,
		StackingLine5 = 0x225,
		StackingLine6 = 0x226,
		StackingOrder = 0x227,
		ProcNano1 = 0x228,
		ProcNano2 = 0x229,
		ProcNano3 = 0x22A,
		ProcNano4 = 0x22B,
		ProcChance1 = 0x22C,
		ProcChance2 = 0x22D,
		ProcChance3 = 0x22E,
		ProcChance4 = 0x22F,
		PvpMagicStaminaTapAmount = 0x232,
		ClanGaia = 0x233,
		OTTrans = 0x234,
		ClanVanguards = 0x235,
		GOS = 0x236,
		OTFollowers = 0x237,
		OTOperator = 0x238,
		OTUnredeemed = 0x239,
		ClanDevoted = 0x23A,
		ClanConserver = 0x23B,
		ClanRedeemed = 0x23C,
		Drunkness = 0x23D,
		Consciousness = 0x23E,
		Bliss = 0x23F,
		PlayerOptions = 0x240,
		CurrentTime = 0x242,
		Focus = 0x243,
		NPCVicinityFamily = 0x244,
		Brawling = 0x245,
		ApartmentsAllowed = 0x246,
		ApartmentsOwned = 0x247,
		ApartmentAccessCard = 0x248,
		MapAreaPart3 = 0x249,
		MapAreaPart4 = 0x24A,
		NumberOfTeamMembers = 0x24B,
		ActionCategory = 0x24C,
		CurrentPlayfield = 0x24D,
		DistrictNano = 0x24E,
		DistrictNanoInterval = 0x24F,
		UnsavedXP = 0x250,
		RegainXPPercentage = 0x251,
		ExtendedFlags = 0x256,
		ShopPrice = 0x257,
		NewbieHP = 0x258,
		HPLevelUp = 0x259,
		HPPerSkill = 0x25A,
		NewbieNP = 0x25B,
		NPLevelUp = 0x25C,
		NPPerSkill = 0x25D,
		MaxShopItems = 0x25E,
		ShopRent = 0x260,
		SynergyHash = 0x261,
		ShopFlags = 0x262,
		ShopLastUsed = 0x263,
		ShopType = 0x264,
		LockDownTime = 0x265,
		LeaderLockDownTime = 0x266,
		WeaponAnimSet = 0x267,
		Swaying = 0x268,
		AbilityBccEvent = 0x269,
		CraftResets = 0x26A,
		NPCFactionID = 0x26B,
		HouseTemplate = 0x26C,
		AOPercentFireDamage = 0x26D,
		NPCFactionOverride = 0x26E,
		PercentMeleeDamage = 0x26F,
		PercentProjectileDamage = 0x270,
		AOPercentPoisonDamage = 0x271,
		PercentRadiationDamage = 0x272,
		PercentEnergyDamage = 0x273,
		PercentChemicalDamage = 0x274,
		AOTotalDamage = 0x275,
		TrackProjectileDamage = 0x276,
		TrackMeleeDamage = 0x277,
		TrackEnergyDamage = 0x278,
		TrackChemicalDamage = 0x279,
		TrackRadiationDamage = 0x27A,
		VendorFeatCriteria = 0x27B,
		AOTrackPoisonDamage = 0x27C,
		AOTrackFireDamage = 0x27D,
		NPCGameActionArg1 = 0x27E,
		NPCGameActionRet1 = 0x27F,
		CityInstance = 0x280,
		DistanceToSpawnpoint = 0x281,
		CityTerminalRechargePercent = 0x282,
		TokenType = 0x283,
		TokenCurrencyType1 = 0x284,
		TokenCurrencyPrice1 = 0x285,
		TokenCurrencyType2 = 0x286,
		TokenCurrencyPrice2 = 0x287,
		VeteranCurrency = 0x288,
		ItemShopCurrency = 0x289,
		PvECurrency1 = 0x28A,
		AdvantageHash1 = 0x28B,
		AdvantageHash2 = 0x28C,
		AdvantageHash3 = 0x28D,
		AdvantageHash4 = 0x28E,
		AdvantageHash5 = 0x28F,
		PvPFinishingBlows = 0x291,
		PvPMatchesWon = 0x293,
		PvPMatchesLost = 0x294,
		PvPMatchesPlayed = 0x295,
		PvPFlagsCaptured = 0x296,
		PvPFlagsReturned = 0x297,
		NPCKills = 0x298,
		NPCDeaths = 0x299,
		GuildWarID = 0x2A0,
		DefendingGuildWarID = 0x2A1,
		PvECurrency2 = 0x2A2,
		ItemSlot1ACG1 = 0x2A3,
		ItemSlot1ACG2 = 0x2A4,
		ItemSlot2ACG1 = 0x2A5,
		ItemSlot2ACG2 = 0x2A6,
		ItemSlot3ACG1 = 0x2A7,
		ItemSlot3ACG2 = 0x2A8,
		ItemSlot4ACG1 = 0x2A9,
		ItemSlot4ACG2 = 0x2AA,
		PvPCurrency1 = 0x2AB,
		PvPCurrency2 = 0x2AC,
		PvPCurrency3 = 0x2AD,
		VeteranCurrencySpent = 0x2AE,
		TokenRewardType = 0x2AF,
		TokenRewardAmount = 0x2B0,
		SpellFlags = 0x2B1,
		SpellType = 0x2B3,
		GuildRenownMultiplier = 0x2B4,
		GoldModifier = 0x2B5,
		GemSlot1Color = 0x2B6,
		GemSlot2Color = 0x2B7,
		GemSlot3Color = 0x2B8,
		GemSlot4Color = 0x2B9,
		GemColor = 0x2BA,
		GemItemType = 0x2BB,
		ACGItemSeed = 0x2BC,
		ACGItemLevel = 0x2BD,
		ACGItemTemplateID = 0x2BE,
		ACGItemTemplateID2 = 0x2BF,
		ACGItemCategoryID = 0x2C0,
		ACGItemNano1 = 0x2C1,
		ACGItemNano2 = 0x2C2,
		ACGItemNano3 = 0x2C3,
		ACGItemNano4 = 0x2C4,
		TotalOfflineLevels = 0x2C5,
		CriticalDamageBonus = 0x2C6,
		PersonalItemDrop = 0x2C8,
		HealModifierPercent = 0x2CA,
		ManaPercentageToBreak = 0x2CB,
		StaminaPercentageToBreak = 0x2CC,
		SecondarySpellCostStat = 0x2CD,
		SecondarySpellCostValue = 0x2CE,
		Alpha = 0x2CF,
		PvPEvadeChance = 0x2D0,
		PvPImmunity = 0x2D1,
		GameAction_Stat = 0x2D2,
		GameAction_Type = 0x2D3,
		GameAction_Min = 0x2D4,
		GameAction_Duration = 0x2D5,
		GameAction_Interval = 0x2D6,
		GameAction_TargetType = 0x2D7,
		GameAction_TargetInstance = 0x2D8,
		GameAction_AnimEffect = 0x2D9,
		GameAction_MeshEffect = 0x2DA,
		GameAction_ItemType = 0x2DB,
		GameAction_ItemInstance = 0x2DC,
		GameAction_Radius = 0x2DD,
		GameAction_RemoveType = 0x2DE,
		GameAction_TextID = 0x2DF,
		GameAction_VisualEffectMesh = 0x2E0,
		GameAction_VisualEffectAnim = 0x2E1,
		GameAction_VisualRadius = 0x2E2,
		GameAction_AudioEffectID = 0x2E3,
		GameAction_AudioEffectDuration = 0x2E4,
		GameAction_AudioEffectRepeat = 0x2E5,
		GameAction_AudioEffectVolume = 0x2E6,
		GameAction_PoisonType = 0x2E7,
		GameAction_PoisonDifficulty = 0x2E8,
		GameAction_SocialAnim = 0x2E9,
		GameAction_SkillType = 0x2EA,
		GameAction_TimedLength = 0x2EB,
		GameAction_Criteria = 0x2EC,
		GameAction_Operator = 0x2ED,
		GameAction_RelXPos = 0x2EE,
		GameAction_RelYPos = 0x2EF,
		GameAction_RelZPos = 0x2F0,
		GameAction_TeleportDest = 0x2F1,
		GameAction_ApplyOn = 0x2F2,
		GameAction_MaxAmount = 0x2F3,
		GameAction_MaxPercentage = 0x2F4,
		GameAction_TargetList = 0x2F5,
		GameAction_Music = 0x2F6,
		GameAction_Max = 0x2F7,
		GameAction_AnimReverse = 0x2F8,
		GameAction_Value = 0x2F9,
		GameAction_TargetExpression = 0x2FA,
		GameAction_Pos = 0x2FB,
		GameAction_Play = 0x2FC,
		GameAction_Speed = 0x2FD,
		GameAction_CatMeshEffect = 0x2FE,
		GameAction_AnimFlag = 0x2FF,
		TargetConcealment = 0x27B4,
		TargetBreedHostility = 0x27DC,
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
