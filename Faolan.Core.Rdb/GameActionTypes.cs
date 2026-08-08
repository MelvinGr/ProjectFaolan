using System.Collections.Generic;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     One GameAction type as the client registers it: its id, its name, and its KEY LIST - the ordered field
	///     grammar of every element of that type. An element carries exactly one u32 per key, after a fixed 8-word
	///     prefix, so <see cref="Keys" />.Length is what makes the element's width knowable.
	/// </summary>
	public sealed record GameActionType(uint Id, string Name, int[] Keys)
	{
		/// <summary>
		///     Element width in u32 words: the 8-word prefix plus one value per key. Meaningful only when
		///     <see cref="IsFixedWidth" /> - a TEXT slot is not one word.
		/// </summary>
		public int WidthWords => 8 + Keys.Length;

		/// <summary>
		///     False when any slot holds TEXT, which makes the element variable-width and <see cref="WidthWords" />
		///     a fiction. See <see cref="GameActionTypes.TextKeys" />.
		/// </summary>
		public bool IsFixedWidth => System.Array.TrueForAll(Keys, k => !GameActionTypes.TextKeys.Contains(k));

		/// <summary>Payload slot carrying the given key, or -1.</summary>
		public int SlotOf(int key)
		{
			return System.Array.IndexOf(Keys, key);
		}

		/// <summary>
		///     True when this type carries the INTERPOLATED family's four ramp slots - the two VALUES (174 low, 175
		///     high) and the two LEVELS they are reached at (172 low, 173 high). Asked of the key list rather than of
		///     a kind allowlist, so a fourth ramp type registered later is one automatically. See
		///     <see cref="LevelRamp" />.
		/// </summary>
		public bool IsLevelRamp => SlotOf(GameActionTypes.KeyLowValue) >= 0 && SlotOf(GameActionTypes.KeyHighValue) >= 0
		                                                                   && SlotOf(GameActionTypes.KeyLowLevel) >= 0 && SlotOf(GameActionTypes.KeyHighLevel) >= 0;
	}

	// The client's GameAction type catalog, recovered STATICALLY from the initialiser FUN_008bdf40
	// (tools/ghidra/DumpGameActionKeyLists.java -> docs/re/gameaction-keylists.tsv, which this mirrors; the
	// GameActionKeyListTests gate keeps the two in step). See docs/re/gameactionlist-grammar.md.
	//
	// Why it lives here rather than next to the wire codecs: the same descriptor governs the RDB feat/spell/item
	// modifier record, which is a GameAction element stored in a template rather than sent on the wire, and that
	// is the reader this table exists to drive (ItemTemplateStatModifiers).
	public static class GameActionTypes
	{
		// Key indices the client NAMES in its own descriptors (docs/re/gameaction-keylists.tsv, `fields` column).
		// Only the ones a modifier record needs are named here.
		public const int KeyStat = 0;
		public const int KeyLine = 1;      // unnamed by the client, but it is the slot every `...Line...` type
		                                   // carries where its per-spell twin carries a spell (53069 vs 53031)
		public const int KeyValue = 39;
		public const int KeyAmount = 175;
		public const int KeySpell = 88;    // the slot SpellStatModifier carries where SpellLineStatModifier has KeyLine
		public const int KeyRadius = 11;   // stat 0x2DD, client-named `Radius` - metres, carried by 17 of the 260 types

		// The four slots of the INTERPOLATED family (53220 `InterpolatedModify`, 53221 `InterpolatedTempChangeSkill`,
		// 53068 `InterpolatedModifyAsRating`, all `[Stat, 174, 172, 175, 173]`). The client names 174 `Substate` and
		// 175 `Amount`; what they actually are is the two ENDPOINTS of a level ramp, and 172/173 the levels they are
		// reached at - see docs/re/gameactionlist-grammar.md for the decompiled lerp (Gitea #597).
		public const int KeyLowValue = 174;
		public const int KeyLowLevel = 172;
		public const int KeyHighLevel = 173;

		// The ramp's HIGH endpoint. Deliberately an ALIAS of KeyAmount rather than a second constant: it is the same
		// slot, and the two names are the two things the client calls it - `Amount` in its descriptor (which on the
		// plain `ModifyAsRating` really is a percentage) and the ramp's upper value in its apply code. Reading a ramp
		// through `KeyAmount` compiles to the same thing and reads as the wrong unit.
		public const int KeyHighValue = KeyAmount;

		// ⚠️ THE KEYS BELOW ARE PER-TYPE, unlike the ones above. A key index the client NAMES (0 `Stat`, 39 `Value`,
		// 88 `Spell`, 175 `Amount`) means the same thing on every type that carries it - that is what makes a name
		// transfer BY KEY. An UNNAMED one carries no such promise, and the catalog proves it does not hold:
		//
		//   key 187  a per-SPELL target on `ChangeRecastTime` (53031) and `ChangeHitPercent` (53030) - the slot their
		//            `...Line...` twins fill with KeyLine - but a per-COMBO target on `UpgradeCombo` (53209).
		//   key 66   the combo's damage delta on `UpgradeCombo`, a follower SCRIPT GROUP on `SetFollowerScriptGroup`
		//            (53024), and the proc's incoming/outgoing DIRECTION on the `AddProc` family - the tooltip
		//            compares it to 1 to pick `AddProcGettingHit` over `AddProcOnHit`.
		//   key 67   the combo's reuse-time delta on `UpgradeCombo`, and the combo attack's CRITICAL CHANCE on
		//            `ChangeInheritedWeaponAttack` (53250).
		//
		// So these names are read as `<Type>_<slot>` and are only valid inside the parser for that type. Naming an
		// unnamed key globally is how a decode gets exported to a type it was never checked against.
		//
		// ⚠️ And a SCALE transfers even less than a name does. Keys 66 and 67 are read from the same two
		// `GameActionStat_e` slots (0x315 / 0x316) on `UpgradeCombo` and on `ChangeInheritedWeaponAttack`, and the
		// tooltip renders them through DIFFERENT arithmetic on each: `× 0.01` and the float formatter on 53209,
		// against a raw int on 53250's key 66 and `× 0.025` on its key 67. Reading a 53250 value as 53209's
		// hundredths-of-a-percent would report a +30% opening-damage feat as +0.30%.
		//
		// `UpgradeCombo` (53209): the combo it upgrades plus the two axes it moves. Named from the client's own
		// item-tooltip builder `FUN_0149e450`, which reads GameActionStat_e 0x315/0x316 (= key 66/67 through the
		// key-index table) and branches on each value's SIGN to pick between the localised
		// `UpgradeCombo_IncreaseDamage`/`_DecreaseDamage` and `UpgradeCombo_IncreaseCooldown`/`_DecreaseCooldown`
		// strings. Both are SIGNED hundredths of a percent (rendered `× 0.01` with a `%` suffix).
		// See docs/re/gameactionlist-grammar.md.
		public const int KeyUpgradeComboTarget = 187;
		public const int KeyUpgradeComboDamage = 66;
		public const int KeyUpgradeComboCooldown = 67;

		// The PROC family - `AddComboProc` (53212, `[88, 236, 187 combo, 117, 237]`) and `AddSpellLineProc` (53070,
		// `[88, 236, 1 line, 117, 237, 73]`). The same tooltip builder settles the two spell slots: it tests key 88
		// and key 236 for a valid spell independently and renders `AddComboProcSelf` when only 88 is set,
		// `AddComboProcOther` when only 236 is, and `AddComboProcBoth` when both are - so 88 is the spell the proc
		// puts on the CASTER and 236 the one it puts on the TARGET. 117 is the trigger kind (the `AddProc` branch
		// dispatches the Hit / Damage / SpellDamage string family on it) and 237 the magnitude it renders last.
		// `ChangeHitPercent` (53030): the per-SPELL twin of `ChangeLineHitPercent` (53075), which carries its target
		// in KeyLine. 238 of the 240 distinct references resolve against Spells.Id.
		public const int KeySpellHitPercentTarget = 187;

		public const int KeyProcSelfSpell = KeySpell;
		public const int KeyProcTargetSpell = 236;
		public const int KeyProcTrigger = 117;
		public const int KeyProcAmount = 237;
		public const int KeyProcComboTarget = 187;

		// `ChangeInheritedWeaponAttack` (53250, `[187 combo, 84, 66, 67, 68, 69]`): the combo plus WHICH of its
		// weapon attacks, and then three magnitudes and a damage type. Every slot is named by the same tooltip
		// builder `FUN_0149e450` (branch at `014a1dc5`), which reads GameActionStat_e 0x45F / 0x315 / 0x316 / 0x317 /
		// 0x318 in that order and renders one localised string per non-zero slot:
		//
		//   key 84   the ATTACK: `0` -> `FinishingComboAttack`, `1` -> `OpeningComboAttack`, and the branch RETURNS
		//            on anything else (`CMP EDI,1; JNZ <bail>` at `014a1e46`) - so it is a two-valued selector, not
		//            the `Level` its descriptor label suggests. Corroborated by the feats themselves: `Fierce
		//            Opening` and `Dual Master` ("increases the OPENING damage done by all combos") carry 1.
		//   key 66   `IncreaseComboDPS` / `DecreaseComboDPS` on the sign - a RAW int with a `%` suffix, so a WHOLE
		//            percent, unlike 53209's hundredths.
		//   key 67   `IncreaseComboCriticalChance` / `DecreaseComboCriticalChance` - rendered `× 0.025` (the float at
		//            `0x020b5944`) with a `%` suffix, i.e. a RATING, 40 points to the percentage point.
		//   key 68   `IncreaseComboHateAdjustment` / `DecreaseComboHateAdjustment` - raw int + `%`, like key 66.
		//   key 69   `ChangeComboDamageType` - NOT a magnitude: the client feeds it to `GetLocalisedString(600, v)`,
		//            so it is an id in the damage-type name table. Zero on all 315 shipped records.
		public const int KeyComboAttackTarget = 187;
		public const int KeyComboAttackSlot = 84;
		public const int KeyComboAttackDps = 66;
		public const int KeyComboAttackCriticalChance = 67;
		public const int KeyComboAttackHate = 68;
		public const int KeyComboAttackDamageType = 69;

		// The TEXT slots (see <see cref="TextKeys" />). 209 is the one every string-carrying type uses; 210 is the
		// second string on `GenerateName` and `NewTextEffect`, the only two types that carry both.
		public const int KeyEventString = 209;
		public const int KeyEventString2 = 210;

		/// <summary>
		///     Keys whose slot holds TEXT rather than a u32 - `[u16 byteLength][bytes]` - which is what makes an
		///     element carrying one VARIABLE-width, and by an ODD number of bytes, so `8 + len(keyList)` does not
		///     describe it and the payload has to be WALKED rather than indexed.
		///     <para>
		///         The set is CLOSED, and read out of the client rather than inferred (Gitea #596): the GameAction
		///         deserializer `FUN_00899a60` takes the string branch for exactly two `GameActionStat_e` ids,
		///         `0x4d9` and `0x4da`, and the key-index table `FUN_00897bd0` builds maps index `209` to `0x4d9` and
		///         index `210` to `0x4da` and nothing else to either. So a type is variable-width iff its key list
		///         contains 209 or 210.
		///     </para>
		///     <para>
		///         The u16 is likewise the client's own string primitive, and it is the ONLY form on this path -
		///         settled on both sides of the stream (#596). The reader `FUN_00899a60` calls `FUN_0048f840` ->
		///         `FUN_00cbf840` with mode 0, which reads a u16 and treats `>= 0x8000` as an escape into a long
		///         form; the WRITER `FUN_00899440` emits that same u16 with the same escape (`0xffff` then `0x1ff`).
		///         Neither branches on the type - the string branch is chosen by the STAT KEY - so `FUN_00cbf840`'s
		///         u32 mode (`param_2 == 2`) belongs to some other stream and can never be reached from a
		///         GameAction element.
		///     </para>
		///     <para>
		///         The shipped records agree: 653 of the 880 text-carrying elements in the feat and spell catalogs
		///         tile EXACTLY under the u16 reading. ⚠️ SEVEN do not - all of them `HeadText`, reading
		///         `[u32 7]["Dimach\0"]` with the NUL counted - and those seven are RETIRED DATA, not a second form:
		///         they sit in feat templates 3753 and 12362..12386, the same low-id band that carries the retired
		///         kind 53044 (see <see cref="UnregisteredIds" />) and a `ChangeHitPercent` padded with four
		///         terminator words, while every u16 record in either catalog is id 791547 or higher. The client
		///         cannot read them either. So the walk stops at such a slot rather than guessing a second length
		///         width. See <c>ItemTemplateStatModifiers.ParseElements</c>.
		///     </para>
		/// </summary>
		public static readonly HashSet<int> TextKeys = [KeyEventString, KeyEventString2];

		/// <summary>
		///     The kinds whose <see cref="KeyRadius" /> is a spell's AREA OF EFFECT - the elements that SELECT the
		///     targets an area spell lands on, and so the one place the client states an AoE's blast radius (Gitea
		///     #349). Every one of them pairs the radius with the thing to apply inside it: `KeySpell` for the five
		///     formula kinds (the sub-spell put on everyone found) and `KeyStat` for `AreaHit` (a stat hit directly).
		/// </summary>
		/// <remarks>
		///     17 types carry key 11 and this is deliberately NOT all of them - a radius is only an AoE when the
		///     element is doing target selection. The excluded ones are an audio falloff (`AudioEffect`), a facility
		///     range (`SetTradingPostAccess`), the three MODIFIER kinds that adjust someone else's radius rather than
		///     state one (`AOERadiusModification`, `LineAOERadiusModification`, `GlobalAOERadiusModification`), the
		///     melee cleave add-targets (`ColumnAddTargets`, `AddCleaveTargets`, `AttackAddTargets`, which extend a
		///     swing rather than define a blast), `AreaKnockback` (a displacement, not the damage area) and
		///     `TimedEffect` (a duration kind that happens to carry one).
		/// </remarks>
		public static readonly HashSet<uint> AreaTargetKinds =
		[
			53018, // AreaRangeFormula        [88 Spell, 2, 11 Radius]
			53073, // AreaHit                 [0 Stat, 2, 37, 71, 11 Radius]
			53087, // AreaFormula             [88 Spell, 11 Radius]
			53088, // FilteredAreaFormula     [88 Spell, 11 Radius, 170, 93, 250]
			53096, // CoreFilteredAreaFormula [88 Spell, 11 Radius, 170, 93, 250, 66, 67, 138, 212, 2]
			53159, // CoreRandomPointAreaFormula [88 Spell, 11 Radius, 170, 171]
			53259  // ConeFormula             [88 Spell, 212 from-angle, 213 to-angle, 11 Radius]
		];

		/// <summary>
		///     The id space the client asserts on, straight out of the descriptor lookup `FUN_008c3910`:
		///     `descriptor = &amp;DAT_025077c8 + (id - 0xCF09) * 0x104` bytes, guarded by `(id - 0xCF09) &lt;= 0x11D`.
		///     So the descriptor table is a flat ARRAY of 286 slots indexed by the type id itself.
		/// </summary>
		public const uint FirstId = 0xCF09;

		/// <inheritdoc cref="FirstId" />
		public const uint LastId = 0xD026;

		/// <summary>
		///     The 26 ids inside <see cref="FirstId" />..<see cref="LastId" /> that the client registers NOTHING for -
		///     retired types, not types the recovery missed (Gitea #596).
		///     <para>
		///         Proven three ways, because "the dump skipped it" is the obvious competing explanation:
		///         the initialiser `FUN_008bdf40` writes 260 of the array's 286 slots and never mentions these ids;
		///         a scan of the whole image finds their id constant NOWHERE (`0xCF34` has zero hits); and the image
		///         holds exactly 260 `e_Type_GC_GameAction_*` name strings, so every NAMED type was reached. A
		///         retired id therefore has no descriptor, no key list and no name anywhere in the client.
		///     </para>
		///     <para>
		///         What that means for a reader: both the element reader `FUN_00899a60` and the writer
		///         `FUN_00899440` test the slot's id word for 0 and log `"Tried to stream unknown GameActionData with
		///         type N"` rather than reading a payload - so shipped data carrying one of these (kind 53044 sits in
		///         seven low-id feat templates) is content the client itself cannot parse. Reading it is not a gap to
		///         close; it is data that predates the catalog.
		///     </para>
		/// </summary>
		public static readonly IReadOnlySet<uint> UnregisteredIds = new HashSet<uint>
		{
			53001, 53017, 53039, 53044, 53126, 53127, 53131, 53133, 53142, 53143, 53145, 53146, 53147,
			53149, 53151, 53156, 53157, 53158, 53161, 53181, 53186, 53216, 53248, 53258, 53279, 53280
		};

		private static readonly Dictionary<uint, GameActionType> ById = new Dictionary<uint, GameActionType>
		{
			[53002] = new GameActionType(53002, "Hit", [0, 2, 37, 71]),  // 0=Stat
			[53003] = new GameActionType(53003, "AddLayeredSkinnedMesh", [194, 39]),
			[53004] = new GameActionType(53004, "StartParticleEffectOnHead", [209, 86, 138, 28, 29, 30]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53005] = new GameActionType(53005, "BindAtLocation", []),
			[53006] = new GameActionType(53006, "TeleportToBindLocation", []),
			[53007] = new GameActionType(53007, "HitPercentage", [0, 39, 1, 71]),  // 0=Stat
			[53008] = new GameActionType(53008, "Remove", [12]),
			[53009] = new GameActionType(53009, "Charge", [187, 248, 245, 243, 254]),
			[53010] = new GameActionType(53010, "SetMorphValue", [209, 39]),
			[53011] = new GameActionType(53011, "AudioEffect", [17, 18, 19, 20, 11]),  // 17=AudioEffectID 18=AudioEffectDuration 19=AudioEffectRepeat 20=AudioEffectVolume 11=Radius
			[53012] = new GameActionType(53012, "Skill", [0, 39, 24]),  // 0=Stat 39=Value 24=SkillType
			[53013] = new GameActionType(53013, "DPSHit", [0, 39, 66, 71]),  // 0=Stat
			[53014] = new GameActionType(53014, "TimedEffect", [0, 39, 11, 46, 25]),  // 0=Stat 11=Radius 25=TimedLength
			[53015] = new GameActionType(53015, "AddFormation", [187]),
			[53016] = new GameActionType(53016, "Teleport", [28, 29, 30, 151, 31]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53018] = new GameActionType(53018, "AreaRangeFormula", [88, 2, 11]),  // 11=Radius
			[53019] = new GameActionType(53019, "Spell", [39]),
			[53020] = new GameActionType(53020, "Bandaging", []),
			[53021] = new GameActionType(53021, "RenewMana", []),
			[53022] = new GameActionType(53022, "LayeredVisualLocation", [193, 194, 195, 196, 197, 198, 199, 200, 201, 39]),
			[53023] = new GameActionType(53023, "CatMesh", [44]),
			[53024] = new GameActionType(53024, "SetFollowerScriptGroup", [66]),
			[53025] = new GameActionType(53025, "AddHateToPets", [66]),
			[53026] = new GameActionType(53026, "Set", [55, 0, 39]),  // 0=Stat
			[53027] = new GameActionType(53027, "ItemText", [39, 209]),
			[53028] = new GameActionType(53028, "PolyMorph", []),
			[53029] = new GameActionType(53029, "ChangeModifyPercent", [187, 39, 55]),
			[53030] = new GameActionType(53030, "ChangeHitPercent", [187, 39, 55]),
			[53031] = new GameActionType(53031, "ChangeRecastTime", [187, 39]),
			[53032] = new GameActionType(53032, "AOERadiusModification", [187, 11]),  // 11=Radius
			[53033] = new GameActionType(53033, "ApplyFactionRank", [66, 67]),
			[53034] = new GameActionType(53034, "SummonFollower", []),
			[53035] = new GameActionType(53035, "EnterSiegeTargeting", []),
			[53036] = new GameActionType(53036, "SpawnQuest", [209]),
			[53037] = new GameActionType(53037, "Charmed", [66, 67, 68, 69, 163, 39]),
			[53038] = new GameActionType(53038, "PlaceTrap", [77, 84, 88, 25, 66, 67, 68]),  // 77=Hash 84=Level 25=TimedLength
			[53040] = new GameActionType(53040, "StartTracking", [39]),  // 39=Tier
			[53041] = new GameActionType(53041, "ModifyBasedOnStat", [174, 175, 0, 207]),  // 174=Substate 175=Amount 0=Stat
			[53042] = new GameActionType(53042, "Modify_PowerSurge", [39]),
			[53043] = new GameActionType(53043, "ExecuteBccAction", [243]),
			[53045] = new GameActionType(53045, "Modify", [0, 39]),  // 0=Stat
			[53046] = new GameActionType(53046, "AddAbsorb", [0, 70, 33, 34, 88]),  // 0=Stat
			[53047] = new GameActionType(53047, "CameraShake", [204, 249, 191]),
			[53048] = new GameActionType(53048, "Name", [209]),
			[53049] = new GameActionType(53049, "SpawnMonster", [47, 48, 39, 72, 73, 28, 29, 30, 77]),  // 72=TimeExist 73=Flags 28=RelXPos 29=RelYPos 30=RelZPos 77=Hash
			[53050] = new GameActionType(53050, "RemoveBuffs", []),
			[53051] = new GameActionType(53051, "CastNano", [39]),
			[53052] = new GameActionType(53052, "StrTexture", [58, 56, 8]),
			[53053] = new GameActionType(53053, "CameraRoll", [191]),
			[53054] = new GameActionType(53054, "SpellStatModifier", [88, 0, 39, 175]),  // 0=Stat 175=Amount
			[53055] = new GameActionType(53055, "AttractorMesh", [56, 8]),
			[53056] = new GameActionType(53056, "RemoveCowardStatus", []),
			[53057] = new GameActionType(53057, "HeadText", [209, 39]),
			[53058] = new GameActionType(53058, "SpawnRemotePet", [77, 39, 88, 175]),  // 77=Hash 175=Amount
			[53059] = new GameActionType(53059, "LineTeleport", [9, 10, 31, 100]),
			[53060] = new GameActionType(53060, "MonsterShape", [39, 25]),  // 39=Value 25=TimedLength
			[53061] = new GameActionType(53061, "BCCCastSpell", [39]),
			[53062] = new GameActionType(53062, "SpellLineStatModifier", [1, 0, 39, 175]),  // 0=Stat 175=Amount
			[53063] = new GameActionType(53063, "SpawnMonsterACG", [77, 84, 72, 73, 28, 29, 30, 103]),  // 77=Hash 84=Level 72=TimeExist 73=Flags 28=RelXPos 29=RelYPos 30=RelZPos 103=NPCMaster
			[53064] = new GameActionType(53064, "SpawnItemOnCharacter", [77, 84, 85, 72, 73, 28, 29, 30]),  // 77=Hash 84=Level 72=TimeExist 73=Flags 28=RelXPos 29=RelYPos 30=RelZPos
			[53065] = new GameActionType(53065, "CoreSpellStatModifier", [88, 0, 39, 175, 1305]),  // 0=Stat 175=Amount 1305=FloatValue
			[53066] = new GameActionType(53066, "TeamFormula", [88]),
			[53067] = new GameActionType(53067, "ModifyAsRating", [0, 39]),  // 0=Stat
			[53068] = new GameActionType(53068, "InterpolatedModifyAsRating", [0, 174, 172, 175, 173]),  // 0=Stat 174=Substate 175=Amount
			[53069] = new GameActionType(53069, "ChangeLineRecastTime", [1, 39]),
			[53070] = new GameActionType(53070, "AddSpellLineProc", [88, 236, 1, 117, 237, 73]),  // 73=Flags
			[53071] = new GameActionType(53071, "DoorToAutoDoor", [31, 90, 91]),
			[53072] = new GameActionType(53072, "StopAudioEffect", [17]),  // 17=AudioEffectID
			[53073] = new GameActionType(53073, "AreaHit", [0, 2, 37, 71, 11]),  // 0=Stat 11=Radius
			[53074] = new GameActionType(53074, "LineAOERadiusModification", [1, 11]),  // 11=Radius
			[53075] = new GameActionType(53075, "ChangeLineHitPercent", [1, 39, 55]),
			[53076] = new GameActionType(53076, "CoreSpellLineStatModifier", [1, 0, 39, 175, 1305]),  // 0=Stat 175=Amount 1305=FloatValue
			[53077] = new GameActionType(53077, "SetSpellLineRecastTime", [1, 39]),
			[53078] = new GameActionType(53078, "CastSpellOnOwner", [39]),
			[53079] = new GameActionType(53079, "CastInvertibleBuff", [39]),
			[53080] = new GameActionType(53080, "PetFormula", [39, 64]),
			[53081] = new GameActionType(53081, "ProxyForceTeleport", []),
			[53082] = new GameActionType(53082, "ProxyTeleport", [94, 95, 96, 97, 98, 99]),
			[53083] = new GameActionType(53083, "ProxyTeleportLine", [94, 95, 96, 97, 98, 99, 100]),
			[53084] = new GameActionType(53084, "ColumnFormula", [88, 238, 39, 170, 93, 250, 66, 67]),
			[53085] = new GameActionType(53085, "Recovery", []),
			[53086] = new GameActionType(53086, "EnableShieldTaunt", []),
			[53087] = new GameActionType(53087, "AreaFormula", [88, 11]),  // 11=Radius
			[53088] = new GameActionType(53088, "FilteredAreaFormula", [88, 11, 170, 93, 250]),  // 11=Radius
			[53089] = new GameActionType(53089, "ItemFormula", [88, 39]),
			[53090] = new GameActionType(53090, "EnableCriminalStatus", []),
			[53091] = new GameActionType(53091, "FEDOCSTest", []),
			[53092] = new GameActionType(53092, "SetTradingPostAccess", [39, 11]),  // 39=Value 11=Radius
			[53093] = new GameActionType(53093, "LockMetaline", [39, 72, 66, 67]),  // 72=TimeExist
			[53094] = new GameActionType(53094, "RestoreChar", []),
			[53095] = new GameActionType(53095, "DisableMinigames", []),
			[53096] = new GameActionType(53096, "CoreFilteredAreaFormula", [88, 11, 170, 93, 250, 66, 67, 138, 212, 2]),  // 11=Radius
			[53097] = new GameActionType(53097, "ChangeTokenPoints", [66, 67]),
			[53098] = new GameActionType(53098, "TargetSpellStatModifier", [88, 0, 39, 175]),  // 0=Stat 175=Amount
			[53099] = new GameActionType(53099, "TargetSpellLineStatModifier", [1, 0, 39, 175]),  // 0=Stat 175=Amount
			[53100] = new GameActionType(53100, "HateValueAdd", [239, 240]),
			[53101] = new GameActionType(53101, "HateSetAdd", [242, 240]),
			[53102] = new GameActionType(53102, "HateModify", [241, 240]),
			[53103] = new GameActionType(53103, "HatelistStepUp", []),
			[53104] = new GameActionType(53104, "HatelistStepDown", []),
			[53105] = new GameActionType(53105, "RemoveBuff", [84, 116, 39]),  // 84=Level
			[53106] = new GameActionType(53106, "TriggerUse", []),
			[53107] = new GameActionType(53107, "HatelistStepToTop", []),
			[53108] = new GameActionType(53108, "AddBuffCounter", [1, 39, 187]),
			[53109] = new GameActionType(53109, "RemoveBuffCounter", [1, 39, 187, 66]),
			[53110] = new GameActionType(53110, "TempChangeSkill", [0, 39]),  // 0=Stat
			[53111] = new GameActionType(53111, "Assign", [0, 39]),  // 0=Stat
			[53112] = new GameActionType(53112, "TransferBuffCounter", []),
			[53113] = new GameActionType(53113, "ModifyGameAction", [1, 193, 174, 175, 1307, 1305]),  // 174=Substate 175=Amount 1305=FloatValue
			[53114] = new GameActionType(53114, "RegisterTemplateSetEntry", []),
			[53115] = new GameActionType(53115, "OpenDialog", [209, 39, 117]),
			[53116] = new GameActionType(53116, "LockSpellLine", []),
			[53117] = new GameActionType(53117, "ModifySpellLineCooldown", []),
			[53118] = new GameActionType(53118, "ModifyTemplateCooldown", []),
			[53119] = new GameActionType(53119, "LockAllSpells", []),
			[53120] = new GameActionType(53120, "AddSkill", [0, 39, 1305]),  // 0=Stat 39=Value 1305=FloatValue
			[53121] = new GameActionType(53121, "Feared", [66, 163, 67]),
			[53122] = new GameActionType(53122, "Stun", []),
			[53123] = new GameActionType(53123, "RemoveSpellLineBuff", [39, 174]),  // 174=Substate
			[53124] = new GameActionType(53124, "CoreLockSpellLineTemplate", [39, 66, 67]),
			[53125] = new GameActionType(53125, "RndSpawnMonsterACG", [39]),
			[53128] = new GameActionType(53128, "AddTokenStateCounter", [66, 39]),
			[53129] = new GameActionType(53129, "Root", []),
			[53130] = new GameActionType(53130, "GrantAccessToPlayfield", []),
			[53132] = new GameActionType(53132, "GenerateName", [209, 210, 39]),
			[53134] = new GameActionType(53134, "NewTextEffect", [209, 210, 39]),
			[53135] = new GameActionType(53135, "RemoveTokenStateCounter", [66, 39]),
			[53136] = new GameActionType(53136, "TransferTokenStateCounter", [66, 67, 39, 1]),
			[53137] = new GameActionType(53137, "StartParticleEffectOnMount", [209, 86, 28, 29, 30]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53138] = new GameActionType(53138, "StartFlymode", []),
			[53139] = new GameActionType(53139, "SetFlag", [0, 39]),  // 0=Stat 39=Value
			[53140] = new GameActionType(53140, "ClearFlag", [0, 39]),  // 0=Stat 39=Value
			[53141] = new GameActionType(53141, "ToggleFlag", [0, 39]),  // 0=Stat 39=Value
			[53144] = new GameActionType(53144, "TeleportToSaveSpot", []),
			[53148] = new GameActionType(53148, "PersistentStatChange", []),
			[53150] = new GameActionType(53150, "ExecuteGenericAttack", []),
			[53152] = new GameActionType(53152, "ActivateFeatSpec", []),
			[53153] = new GameActionType(53153, "CorePersistentGroundEffect", []),
			[53154] = new GameActionType(53154, "SummonTeamMember", []),
			[53155] = new GameActionType(53155, "SummonWholeTeam", []),
			[53159] = new GameActionType(53159, "CoreRandomPointAreaFormula", [88, 11, 170, 171]),  // 11=Radius
			[53160] = new GameActionType(53160, "Clone", []),
			[53162] = new GameActionType(53162, "NanoResistance", [152, 39]),
			[53163] = new GameActionType(53163, "AddDiminishingReturn", [39, 174, 1]),  // 174=Substate
			[53164] = new GameActionType(53164, "SaveHere", []),
			[53165] = new GameActionType(53165, "ProxyTeleport_WithPetHandling", [94, 95, 96, 97, 98, 99]),
			[53166] = new GameActionType(53166, "TransferBuff", []),
			[53167] = new GameActionType(53167, "TransferSpellLine", []),
			[53168] = new GameActionType(53168, "StartEffect", [187, 6, 5, 28, 29, 30]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53169] = new GameActionType(53169, "DamageRedirect", []),
			[53170] = new GameActionType(53170, "InheritedComboAttack", [224, 222, 226, 71, 221]),
			[53171] = new GameActionType(53171, "InheritedComboHit", [224, 66, 71, 221, 88]),
			[53172] = new GameActionType(53172, "AdjustHitTable", [67, 66, 39]),
			[53173] = new GameActionType(53173, "AdjustSpellHitTable", [67, 66, 187, 39]),
			[53174] = new GameActionType(53174, "AdjustSpellLineHitTable", [67, 66, 187, 39]),
			[53175] = new GameActionType(53175, "AddLeech", [39, 174, 192]),  // 174=Substate
			[53176] = new GameActionType(53176, "DisplayLearnAbility", [1, 187]),
			[53177] = new GameActionType(53177, "Dispell", [0, 39]),  // 0=Stat
			[53178] = new GameActionType(53178, "MultiHit", [187, 3]),
			[53179] = new GameActionType(53179, "TeleportToGuildCity", []),
			[53180] = new GameActionType(53180, "Tracer", [28, 29, 30, 134, 135, 136, 87, 73, 89]),  // 28=RelXPos 29=RelYPos 30=RelZPos 73=Flags
			[53182] = new GameActionType(53182, "SpecialAbility", [39, 77, 84, 174]),  // 39=Value 77=Hash 84=Level 174=Substate
			[53183] = new GameActionType(53183, "AddSelfResurrectionSpell", [88, 67, 188]),
			[53184] = new GameActionType(53184, "StatMultiplier", [0, 39]),  // 0=Stat
			[53185] = new GameActionType(53185, "Drain", [0, 2, 37, 71, 39]),  // 0=Stat
			[53187] = new GameActionType(53187, "Heal", []),
			[53188] = new GameActionType(53188, "DialogFeedback", [209]),
			[53189] = new GameActionType(53189, "AddPercentSkillModifier", [66, 24, 67, 39]),  // 24=SkillType
			[53190] = new GameActionType(53190, "StartSpellWeavingMode", [88, 66, 67, 68, 69]),
			[53191] = new GameActionType(53191, "AddComboHate", [110, 240, 111]),
			[53192] = new GameActionType(53192, "SpawnMonsterACGRot", [77, 84, 72, 73, 28, 29, 30, 103, 151]),  // 77=Hash 84=Level 72=TimeExist 73=Flags 28=RelXPos 29=RelYPos 30=RelZPos 103=NPCMaster
			[53193] = new GameActionType(53193, "PolymorphAttack", [44, 7]),
			[53194] = new GameActionType(53194, "Interrupt", [123]),
			[53195] = new GameActionType(53195, "DebuffDPS", [66, 174, 67, 175, 68, 176, 69, 177]),  // 174=Substate 175=Amount
			[53196] = new GameActionType(53196, "SpecialHit", [0, 2, 37, 71]),  // 0=Stat
			[53197] = new GameActionType(53197, "BCCCastSpellOnGround", [39]),
			[53198] = new GameActionType(53198, "LearnRecipe", [39]),
			[53199] = new GameActionType(53199, "InheritedWeaponAttackHateFlag", []),
			[53200] = new GameActionType(53200, "LockPicking", [66]),
			[53201] = new GameActionType(53201, "RemoveSpell", [39]),
			[53202] = new GameActionType(53202, "LockTemplate", [187, 66, 67]),
			[53203] = new GameActionType(53203, "PickPocket", []),
			[53204] = new GameActionType(53204, "AddBliss", [174]),  // 174=Substate
			[53205] = new GameActionType(53205, "AddSwaying", [174]),  // 174=Substate
			[53206] = new GameActionType(53206, "AddFocus", [174]),  // 174=Substate
			[53207] = new GameActionType(53207, "Feedback", [39]),
			[53208] = new GameActionType(53208, "PerformCorruptionCheck", [66, 67]),
			[53209] = new GameActionType(53209, "UpgradeCombo", [187, 66, 67]),
			[53210] = new GameActionType(53210, "AddGuildRenown", [39, 175]),  // 39=Type 175=Amount
			[53211] = new GameActionType(53211, "AddSpellProc", [88, 236, 187, 117, 237, 73]),  // 73=Flags
			[53212] = new GameActionType(53212, "AddComboProc", [88, 236, 187, 117, 237]),
			[53213] = new GameActionType(53213, "AddProc", [88, 236, 117, 73, 237, 66]),  // 73=Flags
			[53214] = new GameActionType(53214, "DrunkBrawlingSpecial", [224]),
			[53215] = new GameActionType(53215, "SetMaxModifier", [0, 39]),  // 0=Stat 39=Value
			[53217] = new GameActionType(53217, "Knockback", [191, 238]),
			[53218] = new GameActionType(53218, "EnableDefensiveStance", []),
			[53219] = new GameActionType(53219, "InterpolatedHit", [0, 168, 170, 172, 169, 171, 173, 71]),  // 0=Stat
			[53220] = new GameActionType(53220, "InterpolatedModify", [0, 174, 172, 175, 173]),  // 0=Stat 174=Substate 175=Amount
			[53221] = new GameActionType(53221, "InterpolatedTempChangeSkill", [0, 174, 172, 175, 173]),  // 0=Stat 174=Substate 175=Amount
			[53222] = new GameActionType(53222, "InterpolatedSpawnPetACG", [77, 176, 172, 177, 173, 72, 73, 28, 29, 30, 103, 128, 129, 130, 131, 132, 133]),  // 77=Hash 72=TimeExist 73=Flags 28=RelXPos 29=RelYPos 30=RelZPos 103=NPCMaster
			[53223] = new GameActionType(53223, "CastingLevelModifier", [0, 39]),  // 0=Stat
			[53224] = new GameActionType(53224, "TeamPropagatedBuff", [88]),
			[53225] = new GameActionType(53225, "InheritedWeaponAttack", [71, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235]),
			[53226] = new GameActionType(53226, "SetSpellRecastTime", [88, 39]),
			[53227] = new GameActionType(53227, "ResurrectTarget", [188]),
			[53228] = new GameActionType(53228, "VisualLocation", [193, 194, 195, 196, 197, 198, 199, 200, 201]),
			[53229] = new GameActionType(53229, "StartClientSideScryScript", [39]),  // 39=ScriptID
			[53230] = new GameActionType(53230, "StartServerSideScryScript", [39]),  // 39=ScriptID
			[53231] = new GameActionType(53231, "StopScryScript", [39]),  // 39=ScriptID
			[53232] = new GameActionType(53232, "DynamicKDTree", [39]),
			[53233] = new GameActionType(53233, "SetBaseAnim", [209]),
			[53234] = new GameActionType(53234, "InterpolatedMagicHit", []),
			[53235] = new GameActionType(53235, "CinematicCamera", [202, 203, 204, 205, 209]),
			[53236] = new GameActionType(53236, "CombatSpecialMove", [209, 189, 39, 207, 43, 206, 187]),
			[53237] = new GameActionType(53237, "PlayActionAnim", [209]),
			[53238] = new GameActionType(53238, "StartParticleEffect", [209, 86, 138, 28, 29, 30]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53239] = new GameActionType(53239, "StopParticleEffect", [209, 86]),
			[53240] = new GameActionType(53240, "AddSkinnedMesh", [194]),
			[53241] = new GameActionType(53241, "SendScryEvent", [209, 39, 5, 6]),  // 209=EventString
			[53242] = new GameActionType(53242, "Disarm", [172, 173, 168, 170, 169, 171]),
			[53243] = new GameActionType(53243, "StartParticleEffectOnEquippedItem", [209, 193, 86, 138, 28, 29, 30]),  // 28=RelXPos 29=RelYPos 30=RelZPos
			[53244] = new GameActionType(53244, "SummonMount", [76, 205, 201]),
			[53245] = new GameActionType(53245, "BanishMount", []),
			[53246] = new GameActionType(53246, "RegisterSubSpellForSpellLine", []),
			[53247] = new GameActionType(53247, "AreaKnockback", [11, 191, 238]),  // 11=Radius
			[53249] = new GameActionType(53249, "ColumnAddTargets", [11, 238]),  // 11=Radius
			[53250] = new GameActionType(53250, "ChangeInheritedWeaponAttack", [187, 84, 66, 67, 68, 69]),  // 84=Level
			[53251] = new GameActionType(53251, "CodeHooks", [39, 175]),  // 175=Amount
			[53252] = new GameActionType(53252, "OverrideMostHatedTarget", []),
			[53253] = new GameActionType(53253, "ChargeSpecialAbility", [187, 248, 245, 243]),
			[53254] = new GameActionType(53254, "UpdateCommunityCharacter", [39, 175]),  // 175=Amount
			[53255] = new GameActionType(53255, "AddCleaveTargets", [212, 213, 11, 33]),  // 11=Radius
			[53256] = new GameActionType(53256, "MagicHit", [39, 71]),
			[53257] = new GameActionType(53257, "WeaponHit", [244]),
			[53259] = new GameActionType(53259, "ConeFormula", [88, 212, 213, 11]),  // 11=Radius
			[53260] = new GameActionType(53260, "StartClientSideScryScriptWithParams", [39, 209]),  // 39=ScriptID 209=ScriptParams
			[53261] = new GameActionType(53261, "StartServerSideScryScriptWithParams", [39, 209]),  // 39=ScriptID 209=ScriptParams
			[53262] = new GameActionType(53262, "DefensiveStance", [88]),
			[53263] = new GameActionType(53263, "ParryStance", [88]),
			[53264] = new GameActionType(53264, "FrenzyStance", [88]),
			[53265] = new GameActionType(53265, "ChangeSubstate", [39]),
			[53266] = new GameActionType(53266, "SetCatMesh", [193, 194, 195, 196, 197, 198, 199, 200, 201]),
			[53267] = new GameActionType(53267, "Fatality", []),
			[53268] = new GameActionType(53268, "SetIdleBaseAnim", [209]),
			[53269] = new GameActionType(53269, "AddTexScript", [39]),  // 39=ScriptID
			[53270] = new GameActionType(53270, "RemoveTexScript", [39, 204]),  // 39=ScriptID
			[53271] = new GameActionType(53271, "Search", []),
			[53272] = new GameActionType(53272, "Sneak", []),
			[53273] = new GameActionType(53273, "AttackAddTargets", [212, 213, 11]),  // 11=Radius
			[53274] = new GameActionType(53274, "RefreshAirSupply", []),
			[53275] = new GameActionType(53275, "ToggleSubstate", [39]),
			[53276] = new GameActionType(53276, "AddTexScriptWithParams", [39, 204, 54, 209]),
			[53277] = new GameActionType(53277, "TryEnterDrunkenBrawling", []),
			[53278] = new GameActionType(53278, "BindAtSetLocation", [251, 252, 253]),
			[53281] = new GameActionType(53281, "AddTag", []),
			[53282] = new GameActionType(53282, "GlobalAOERadiusModification", [11]),  // 11=Radius
			[53283] = new GameActionType(53283, "GlobalSpellStatModifier", [0, 39, 175]),  // 0=Stat 175=Amount
			[53284] = new GameActionType(53284, "GlobalCoreSpellStatModifier", [0, 39, 175, 1305]),  // 0=Stat 175=Amount 1305=FloatValue
			[53285] = new GameActionType(53285, "GlobalTargetSpellStatModifier", [0, 39, 175]),  // 0=Stat 175=Amount
			[53286] = new GameActionType(53286, "GlobalModifyGameAction", [193, 174, 175, 1307, 1305]),  // 174=Substate 175=Amount 1305=FloatValue
		};

		public static IReadOnlyCollection<GameActionType> All => ById.Values;

		/// <summary>The registered type, or null - an id outside 0xCF09..0xD026 is not a GameAction at all.</summary>
		public static GameActionType? Get(uint id)
		{
			return ById.GetValueOrDefault(id);
		}
	}
}
