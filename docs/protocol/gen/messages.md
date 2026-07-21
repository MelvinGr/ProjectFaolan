# Message index (generated)

> **GENERATED — do not edit.** One row per IIR carrier type: opcode, verification level, the codec
> that builds it, and (V3 only) the golden test that pins it byte-exact — emitted as a live `[V3: …]`
> tag the citation linter resolves. The field-level LAYOUT lives in the linked codec source, which is
> the authoritative spec; this page is the index into it. Shares `CodecIndex` with [`coverage.md`](coverage.md).
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

## How to read a message's layout

1. Find the type below → note its **codec**. 2. Open that codec in `Faolan.Core.Network/Codecs/` — its
   header comment is the byte grammar, and its `Build`/`Parse`/`Read`/`Write` is the executable spec.
   3. The **golden** column names the test that proves those bytes match a real capture.

**77** IIR carrier types · **73 V3** (73 with an auto-cited golden). The
carrier framing that wraps every one of these is in [`03-iir-carrier.md`](../03-iir-carrier.md); the
encoding rules for the fields inside are in [`04-encoding.md`](../04-encoding.md).

| Level | IIR type | Opcode | Codec | Golden (proof of layout) |
|-------|----------|--------|-------|--------------------------|
| V3 | `AreaFormulaIIR` | `0xCE30BE4E` | `AreaFormulaCodec` | [V3: V1IirCodecTests.AreaFormula_RoundTripsCapturedBody] |
| V3 | `BankIIR` | `0x0BA1C1CB` | `BankIirCodec` | [V3: BankStorageTests.BuildBankIirRecord_Matches_CapturedBank] |
| V3 | `BehaviorControlCenterIIR` | `0x642CD3D6` | `BehaviorAckBody`, `BehaviorAnimationCodec`, `BehaviorHeartbeatCodec`, `CombatPacketCodec`, `WorldDeath` | [V3: BehaviorPhaseFieldTests.InsertBehaviorPhaseField_AppendsWhenNoField2] |
| V3 | `ChainKillIIR` | `0x0EED7068` | `ChainKillCodec` | [V3: V1IirCodecTests.ChainKill_RoundTripsCapturedBody] |
| V3 | `CharDCMoveIIR` | `0x4517AA81` | `CharDCMoveCodec` | [V3: DeadReckoningCodecTests.CharDCMove_RoundTripsByteExact] |
| V3 | `CharInPlayIIR` | `0x864CFEF8` | `CreateEditorRecordBuilder` | [V3: CreateEditorRecordTests.BehaviorControlCenterIir_MatchesCapturedBytes] |
| V3 | `CharacterActionIIR` | `0x6B6FD368` | `CreateEditorRecordBuilder` | [V3: CreateEditorRecordTests.BehaviorControlCenterIir_MatchesCapturedBytes] |
| V3 | `ChatTextIIR` | `0xE0B51102` | `ChatTextCodec` | [V3: ChatTextCodecTests.Build_FromParsedFields_MatchesCapture] |
| V3 | `ClimbingDynelFullUpdateIIR` | `0x1FD6412B` | `DynelFullUpdateCodec` | [V3: DynelFullUpdateCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `CombatReactiveIIR` | `0x54C02089` | `SpellweaveCodec` | [V3: SpellweaveCodecTests.BuildReactiveList_ConsumedElementZeroed_MatchesCapture] |
| V3 | `CombatShieldIIR` | `0xCFFD0519` | `CombatShieldCodec` | [V3: CombatRecordCodecTests.CombatShield_BuildsByteExact] |
| V3 | `ComboBookIIR` | `0xA8BBEB7F` | `ComboBookIirCodec` | [V3: ComboBookIirCodecTests.Build_MatchesCapturedLevelUpCombo] |
| V3 | `CoreClaimIIR` | `0x06EC1255` | `CoreClaimListCodec` | [V3: CoreClaimListCodecTests.Build_MatchesCapturedClaim] |
| V3 | `CrimeIIR` | `0xC7954504` | `CrimeCodec` | [V3: V1IirCodecTests.Crime_RoundTripsCapturedBody] |
| V3 | `DamageResultIIR` | `0xFEC4158A` | `CombatEffectBuilder`, `CombatPacketCodec` | [V3: CombatEffectBuilderTests.DamageResult_IsByteIdentical] |
| V3 | `DestructibleFullUpdateIIR` | `0x6BD56736` | `DynelFullUpdateCodec` | [V3: DynelFullUpdateCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `DoorFullUpdateIIR` | `0x33A56FB0` | `DynelFullUpdateCodec` | [V3: DynelFullUpdateCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `EnergyIIR` | `0x40749B8A` | `CombatPacketCodec` | [V3: CombatPacketCodecTests.DamageResultBody_MatchesExpectedLayout] |
| V3 | `FeatIIR` | `0x28DA68A0` | `FeatIirCodec` | [V3: FeatIirCodecTests.Build_MatchesTheCapturedAck] |
| V3 | `FeedbackIIR` | `0xBA1FD222` | `FeedbackIirCodec` | [V3: V1IirCodecTests.Feedback_RoundTripsCapturedBody] |
| V3 | `FormatFeedbackIIR` | `0x8D5A8B6B` | `FormatFeedbackCodec` | [V3: AttributeChangedFeedbackTests.Build_FromFields_MatchesCapturedLevelUpMessage] |
| V3 | `FullPlayerCharacterIIR` | `0x737A6DF9` | `FullPlayerCharacterBuilder` | [V3: FullCharIirRoundTripTests.BuildRoot_FromCapturedFields_ReEncodesByteIdentically] |
| V3 | `GameActionListIIR` | `0xF508F4C1` | `GameActionListCodec` | [V3: GameActionListCodecTests.Parse_ThenBuild_RoundTripsByteExact] |
| V3 | `GameTimeIIR` | `0x4F57DC08` | `GameTimeCodec` | [V3: GameTimeCodecTests.Build_MatchesCapturedSnapshot] |
| V3 | `GuildUpdateIIR` | `0x2550C99A` | `GuildTagBody` | [V3: GuildPacketTests.GuildTagBody_Matches_ExpectedLayout] |
| V3 | `HateListUpdateIIR` | `0x8A01E5A1` | `HateListCodec` | [V3: CombatRecordCodecTests.HateList_BuildsByteExact] |
| V3 | `IntendedTeleportIIR` | `0x10D27BC0` | `TeleportCodec` | [V3: TeleportHandoffTests.BuildTeleportBeginBody_MatchesCapture] |
| V3 | `KnubotAnswerIIR` | `0x4D7D9123` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotAnswerListIIR` | `0xF164A4A5` | `ConversationScreenDecoder`, `KnubotCodec` | [V3: ConversationConstructTests.BuildDialogueCameraPacket_ReproducesTheCapturedPacket] |
| V3 | `KnubotAppendTextIIR` | `0x4A3D899A` | `ConversationScreenDecoder`, `KnubotCodec` | [V3: ConversationConstructTests.BuildDialogueCameraPacket_ReproducesTheCapturedPacket] |
| V3 | `KnubotCloseChatWindowIIR` | `0xC4AF39EB` | `ConversationScreenDecoder`, `KnubotCodec` | [V3: ConversationConstructTests.BuildDialogueCameraPacket_ReproducesTheCapturedPacket] |
| V3 | `KnubotFinishTradeIIR` | `0x7446F3FA` | `KnubotFinishTradeCodec` | [V3: V1IirCodecTests.KnubotFinishTrade_RoundTripsCapturedBody] |
| V3 | `KnubotOpenChatWindowIIR` | `0x045B0B0F` | `ConversationScreenDecoder`, `KnubotCodec` | [V3: ConversationConstructTests.BuildDialogueCameraPacket_ReproducesTheCapturedPacket] |
| V3 | `KnubotStartTradeIIR` | `0x4B2A47BF` | `KnubotStartTradeCodec` | [V3: V1IirCodecTests.KnubotStartTrade_FramesAndCarriesTheDialoguePayloadVerbatim] |
| V3 | `LockedToChangedIIR` | `0x6B0AEC58` | `LockedToChangedCodec` | [V3: V1IirCodecTests.LockedToChanged_RoundTripsCapturedBody] |
| V3 | `LookAtIIR` | `0x0D155695` | `ShopOpenBuilder` | [V3: ShopOpenBuilderTests.BuildTradeSelectEcho_IsByteIdenticalToFrame1189] |
| V3 | `LootBagFullUpdateIIR` | `0xF096251E` | `LootBagMessageCodec`, `LootContainerTemplate` | [V3: LootBagMessageTests.BuildContents_RoundTripsByteExact] |
| V3 | `LootRemoveItemsIIR` | `0x70010162` | `WorldLoot` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V3 | `MagicBuffIIR` | `0x6B87E316` | `NpcSpellCastBuilder`, `WorldDeath` | [V3: NpcSpellCastBuilderTests.Build_EmptyPayload_IsTheBuffRemovalEndTwin] |
| V3 | `MoveTaskIIR` | `0x96C46740` | `MoveTaskCodec`, `MoveTaskComposite`, `WorldDeath` | [V3: MoveTaskCodecTests.ApproachTargetBody_MatchesCapture] |
| V3 | `N3TeleportIIR` | `0x5A32F0D7` | `TeleportCodec` | [V3: TeleportHandoffTests.BuildTeleportBeginBody_MatchesCapture] |
| V3 | `N3ToClientFadeIIR` | `0x66180B20` | `WorldLoot` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V3 | `N3ToClientQuitIIR` | `0x04B82B2D` | `WorldDespawn` | [V3: WorldDespawnTests.BuildBody_MatchesRetailLayout] |
| V3 | `NewAllowedLooterIIR` | `0x1E2E0139` | `WorldLoot` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V3 | `NewCurrentLooterIIR` | `0xD06568C8` | `WorldLoot` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V3 | `NewLevelIIR` | `0x6D314743` | `NewLevelIirCodec` | [V3: NewLevelIirCodecTests.Build_Convenience_CarriesLevelWithZeroDeltas] |
| V3 | `PetIIR` | `0x1F4CB0F7` | `PetIirCodec` | [V3: PetIirCodecTests.BuildAssign_Combat_MatchesCapture] |
| V3 | `QuestFullUpdateIIR` | `0x63268258` | `WorldQuest` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `QuestGoalIIR` | `0x0774C8C1` | `WorldQuest` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `QuestIIR` | `0x97E02314` | `WorldQuest` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `QuestJournalIIR` | `0x79F8C7A1` | `WorldQuest` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `QuestLogListIIR` | `0x504B7924` | `WorldQuest` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `RegenIIR` | `0x28D0AC99` | `RegenCodec` | [V3: V1IirCodecTests.Regen_RoundTripsCapturedBody] |
| V3 | `ScryScriptIIR` | `0xA36D3B74` | `ScryScript` | [V3: ScryScriptCreateBurstTests.PlayOrSkipStep_BuildsTheCapturedRecord] |
| V3 | `SetMorphValueIIR` | `0xBADF5A4B` | `CreateEditorRecordBuilder` | [V3: CreateEditorRecordTests.BehaviorControlCenterIir_MatchesCapturedBytes] |
| V3 | `SetNameIIR` | `0xADCE0CDA` | `CreateEditorRecordBuilder` | [V3: CreateEditorRecordTests.BehaviorControlCenterIir_MatchesCapturedBytes] |
| V3 | `SetPosIIR` | `0x04B80B24` | `SetPosCodec` | [V3: DeadReckoningCodecTests.SetPos_RoundTripsByteExact] |
| V3 | `SimpleCharFullUpdateIIR` | `0xFA015807` | `PlayerSpawnScaffoldCodec` | [V3: PlayerSpawnScaffoldCodecTests.Generate_AfterCharacterIirSwap_StaysParseable] |
| V3 | `SimpleCharKilledByUpdateIIR` | `0xB066AFC2` | `WorldDeath` | [V3: WorldDeathTests.BuildDeathBcc_ReproducesCapturedNpcDeath_ByteExact] |
| V3 | `SimpleDynelFullUpdateIIR` | `0x66AEDD50` | `SimpleDynelMessageCodec` | [V3: CreateEditorSceneDynelTests.InitialBurst_WithDb_IsByteIdenticalToGolden] |
| V3 | `SimpleDynelVisualChangeIIR` | `0xE7F4F8F8` | `SimpleDynelMessageCodec`, `WorldLoot` | [V3: CreateEditorSceneDynelTests.InitialBurst_WithDb_IsByteIdenticalToGolden] |
| V3 | `SpellBookIIR` | `0x6BA10936` | `SpellBookCodec` | [V3: V1IirCodecTests.SpellBook_RoundTripsCapturedBody] |
| V3 | `StatDamageIIR` | `0xE9F24184` | `CombatEffectBuilder`, `SpellweaveCodec`, `StatDamageCodec` | [V3: CombatEffectBuilderTests.DamageResult_IsByteIdentical] |
| V3 | `StatIIR` | `0xF98E10B3` | `SpellweaveCodec` | [V3: SpellweaveCodecTests.BuildReactiveList_ConsumedElementZeroed_MatchesCapture] |
| V3 | `StatUpdateIIR` | `0x96B8DC59` | `StatUpdateCodec` | [V3: CharacterStatBlockTests.BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame] |
| V3 | `StopIIR` | `0xD751EBB4` | `StopCodec` | [V3: DeadReckoningCodecTests.Stop_RoundTripsByteExact] |
| V3 | `TemplateActionIIR` | `0x6C300937` | `TemplateActionCodec` | [V3: V1IirCodecTests.TemplateAction_RoundTripsCapturedBody] |
| V3 | `ThreatLevelIIR` | `0x1745EF6C` | `ThreatLevelCodec` | [V3: CombatRecordCodecTests.ThreatLevel_BuildsByteExact] |
| V3 | `TombStoneFullUpdateIIR` | `0x0D80E7C9` | `DynelFullUpdateCodec` | [V3: DynelFullUpdateCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `TradeskillReplyIIR` | `0x9062D559` | `TradeskillRecipeCodec` | [V3: TradeskillRecipeCodecTests.BuildReplyBody_EmptyCatalog_IsAValidEmptyList] |
| V3 | `TradeskillRequestIIR` | `0x06B9CD94` | `TradeskillRecipeCodec` | [V3: TradeskillRecipeCodecTests.BuildReplyBody_EmptyCatalog_IsAValidEmptyList] |
| V3 | `UpdateInventoryIIR` | `0xA74D2814` | `UpdateInventoryCodec` | [V3: V1IirCodecTests.UpdateInventory_RoundTripsCapturedBody_AndCarriesTheItemProtobuf] |
| V3 | `UpdateSimpleDynelIIR` | `0x30536411` | `WorldLoot` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V2 | `DeleteItemIIR` | `0x72491DE9` | `InventoryDeleteCodec` |  |
| V2 | `MoveInventoryItemIIR` | `0x2A8A1B49` | `InventoryMoveCodec` |  |
| V2 | `PlayfieldFIIR` | `0x5D85BFC7` | `MapChangeCodec` |  |
| V1 | `SetShadowCoverageIIR` | `0xD6C1F0CC` | — |  |

**V2** rows are built but have no byte-exact golden yet; **V1** rows are genuinely undecoded (see
[`gaps.md`](../gaps.md)). A V3 row with an empty golden column is byte-exact via a codec whose test the
scan could not name to a single method (rare); its codec still has a golden per `coverage.md`.
