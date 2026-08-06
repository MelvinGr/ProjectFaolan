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

**80** IIR carrier types · **64 V3** (64 with an auto-cited golden). The
carrier framing that wraps every one of these is in [`03-iir-carrier.md`](../03-iir-carrier.md); the
encoding rules for the fields inside are in [`04-encoding.md`](../04-encoding.md).

| Level | IIR type | Opcode | Codec | Golden (proof of layout) |
|-------|----------|--------|-------|--------------------------|
| V3 | `AddToGroupIIR` | `0x0DF44B73` | `GroupMembershipCodec` | [V3: TeamTradeFriendCodecTests.AddToGroupIir_IsByteExact] |
| V3 | `BankIIR` | `0x0BA1C1CB` | `BankCodec` | [V3: BankStorageTests.BankCodec_Matches_CapturedBank] |
| V3 | `BehaviorControlCenterIIR` | `0x642CD3D6` | `BehaviorControlCenterCodec` | [V3: BehaviorAcksTests.HitResultAck_StatesTheZoneTheSwingCameFrom] |
| V3 | `CharDCMoveIIR` | `0x4517AA81` | `CharDCMoveCodec` | [V3: CrossPlayerMoveRelayTests.BroadcastPlayerMove_ForwardsTheJumpVerbatim_ToAViewerWhoKnowsTheMover] |
| V3 | `ChatTextIIR` | `0xE0B51102` | `ChatTextCodec` | [V3: ChatTextCodecTests.Build_FromParsedFields_MatchesCapture] |
| V3 | `ClimbingDynelFullUpdateIIR` | `0x1FD6412B` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `CombatReactiveIIR` | `0x54C02089` | `CombatReactiveCodec` | [V3: SpellweaveCodecTests.BuildReactiveList_ConsumedElementZeroed_MatchesCapture] |
| V3 | `CombatShieldIIR` | `0xCFFD0519` | `CombatShieldCodec` | [V3: CombatShieldCodecTests.RoundTrips_ACapturedRecord_ByteExact] |
| V3 | `ComboBookIIR` | `0xA8BBEB7F` | `ComboBookCodec` | [V3: ComboBookCodecTests.Build_MatchesCapturedLevelUpCombo] |
| V3 | `CoreClaimIIR` | `0x06EC1255` | `CoreClaimCodec` | [V3: CoreClaimCodecTests.Build_MatchesCapturedClaim] |
| V3 | `DamageResultIIR` | `0xFEC4158A` | `DamageResultCodec` | [V3: CombatDamageCodecTests.DamageResult_IsByteIdentical] |
| V3 | `DestructibleFullUpdateIIR` | `0x6BD56736` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `DoorFullUpdateIIR` | `0x33A56FB0` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `EnergyIIR` | `0x40749B8A` | `EnergyCodec` | [V3: EnergyCodecTests.EnergyBody_MatchesExpectedLayout] |
| V3 | `FeatIIR` | `0x28DA68A0` | `FeatCodec` | [V3: FeatCodecTests.Build_MatchesTheCapturedAck] |
| V3 | `FormatFeedbackIIR` | `0x8D5A8B6B` | `FormatFeedbackCodec` | [V3: FormatFeedbackCodecTests.BuildAreaNameXml_MatchesCapturedBanner] |
| V3 | `FullPlayerCharacterIIR` | `0x737A6DF9` | `FullPlayerCharacterCodec` | [V3: FullPlayerCharacterCodecTests.WriterOutput_SplitsBackIntoItsOwnInputs] |
| V3 | `GameActionListIIR` | `0xF508F4C1` | `GameActionListCodec` | [V3: GameActionListCodecTests.Parse_ThenBuild_RoundTripsByteExact] |
| V3 | `GameTimeIIR` | `0x4F57DC08` | `GameTimeCodec` | [V3: GameTimeCodecTests.Build_AtTheCapturedInstant_MatchesCapturedSnapshot] |
| V3 | `GuildUpdateIIR` | `0x2550C99A` | `GuildUpdateCodec` | [V3: GuildPacketTests.GuildUpdate_Matches_ExpectedLayout] |
| V3 | `HateListUpdateIIR` | `0x8A01E5A1` | `HateListUpdateCodec` | [V3: CombatRecordCodecTests.HateList_BuildsByteExact] |
| V3 | `IntendedTeleportIIR` | `0x10D27BC0` | `TeleportCodec` | [V3: TeleportHandoffTests.BuildTeleportBeginBody_MatchesCapture] |
| V3 | `KnubotAnswerIIR` | `0x4D7D9123` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotAnswerListIIR` | `0xF164A4A5` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotAppendTextIIR` | `0x4A3D899A` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotCloseChatWindowIIR` | `0xC4AF39EB` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotOpenChatWindowIIR` | `0x045B0B0F` | `KnubotCodec` | [V3: KnubotCodecTests.AnswerList_BuildsByteExact] |
| V3 | `KnubotStartTradeIIR` | `0x4B2A47BF` | `KnubotStartTradeCodec` | [V3: V1IirCodecTests.KnubotStartTrade_FramesAndCarriesTheDialoguePayloadVerbatim] |
| V3 | `LookAtIIR` | `0x0D155695` | `LookAtCodec` | [V3: MagicBuffLookAtCodecTests.LookAt_ParsesSelectAndDeselect] |
| V3 | `LootBagFullUpdateIIR` | `0xF096251E` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `LootRemoveItemsIIR` | `0x70010162` | `LootRemoveItemsCodec` | [V3: WorldLootTakeTests.LootRemoveItems_MatchesCapture] |
| V3 | `MagicBuffIIR` | `0x6B87E316` | `MagicBuffCodec` | [V3: MagicBuffLookAtCodecTests.MagicBuff_LongFormSpellIdIsEchoed] |
| V3 | `MainQuestUpdateIIR` | `0x504B7924` | `MainQuestUpdateCodec` | [V3: QuestTests.QuestDefinitionExtractor_RoundTripsCasilda] |
| V3 | `MoveTaskIIR` | `0x96C46740` | `MoveTaskCodec` | [V3: MoveTaskCodecTests.ApproachTargetBody_MatchesCapture] |
| V3 | `N3TeleportIIR` | `0x5A32F0D7` | `TeleportCodec` | [V3: TeleportHandoffTests.BuildTeleportBeginBody_MatchesCapture] |
| V3 | `N3ToClientFadeIIR` | `0x66180B20` | `N3ToClientFadeCodec` | [V3: LootTests.BuildFadeBody_MatchesCapture] |
| V3 | `N3ToClientQuitIIR` | `0x04B82B2D` | `N3ToClientQuitCodec` | [V3: N3ToClientQuitCodecTests.BuildBody_MatchesRetailLayout] |
| V3 | `NewAllowedLooterIIR` | `0x1E2E0139` | `NewLooterCodec` | [V3: LootTests.BuildNewCurrentLooterBody_MatchesCapture] |
| V3 | `NewCurrentLooterIIR` | `0xD06568C8` | `NewLooterCodec` | [V3: LootTests.BuildNewCurrentLooterBody_MatchesCapture] |
| V3 | `NewLevelIIR` | `0x6D314743` | `NewLevelCodec` | [V3: NewLevelCodecTests.Build_MatchesCapturedLevelUpEvent] |
| V3 | `PetIIR` | `0x1F4CB0F7` | `PetCodec` | [V3: PetCodecTests.Assign_Combat_MatchesCapture] |
| V3 | `PlayfieldFIIR` | `0x5D85BFC7` | `PlayfieldFCodec` | [V3: PlayfieldFCodecTests.Parse_ReadsBackWhatTheBuilderWrote] |
| V3 | `QuestFullUpdateIIR` | `0x63268258` | `QuestFullUpdateCodec` | [V3: QuestTests.BuildQuestFullUpdate_MatchesCasildaCapture] |
| V3 | `QuestGoalIIR` | `0x0774C8C1` | `QuestGoalCodec` | [V3: QuestTests.BuildQuestGoal_MatchesCapture] |
| V3 | `QuestIIR` | `0x97E02314` | `QuestCodec` | [V3: QuestTests.BuildQuestIir_MatchesCapture] |
| V3 | `QuestJournalIIR` | `0x79F8C7A1` | `QuestJournalCodec` | [V3: QuestTests.BuildQuestJournal_MatchesCasildaCapture] |
| V3 | `RemoveFromGroupIIR` | `0xE4FF0C85` | `GroupMembershipCodec` | [V3: TeamTradeFriendCodecTests.AddToGroupIir_IsByteExact] |
| V3 | `ScryScriptIIR` | `0xA36D3B74` | `ScryScriptCodec` | [V3: ScryScriptCodecTests.BuildStartScriptGroup_MatchesTheHandWrittenBytes] |
| V3 | `SetNameIIR` | `0xADCE0CDA` | `SetNameCodec` | [V3: WorldSetNameIirTests.BuildWorldSetNameIir_MatchesRecoveredGrammar] |
| V3 | `SetPosIIR` | `0x04B80B24` | `SetPosCodec` | [V3: DeadReckoningCodecTests.SetPos_RoundTripsByteExact] |
| V3 | `SimpleCharFullUpdateIIR` | `0xFA015807` | `SimpleCharFullUpdateCodec` | [V3: SimpleCharFullUpdateCodecTests.Build_AfterCharacterIirSwap_StaysParseable] |
| V3 | `SimpleCharKilledByUpdateIIR` | `0xB066AFC2` | `SimpleCharKilledByUpdateCodec` | [V3: DeathSequenceTests.BuildKilledBy_ReproducesCapturedRecord_ByteExact] |
| V3 | `SimpleDynelFullUpdateIIR` | `0x66AEDD50` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `SimpleDynelVisualChangeIIR` | `0xE7F4F8F8` | `SimpleDynelVisualChangeCodec` | [V3: SimpleDynelVisualChangeCodecTests.CapturedBody_ParsesToItsKind_AndReEncodesByteExact] |
| V3 | `StatDamageIIR` | `0xE9F24184` | `StatDamageCodec` | [V3: CombatDamageCodecTests.EnvironmentalDamage_IsByteIdentical] |
| V3 | `StatIIR` | `0xF98E10B3` | `StatCodec` | [V3: SimpleDynelVisualChangeCodecTests.TheHarvestSequence_MatchesTheCapturedBrick] |
| V3 | `StatUpdateIIR` | `0x96B8DC59` | `StatUpdateCodec` | [V3: CharacterStatBlockTests.BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame] |
| V3 | `StopIIR` | `0xD751EBB4` | `StopCodec` | [V3: DeadReckoningCodecTests.Stop_RoundTripsByteExact] |
| V3 | `ThreatLevelIIR` | `0x1745EF6C` | `ThreatLevelCodec` | [V3: CombatRecordCodecTests.ThreatLevel_BuildsByteExact] |
| V3 | `TombStoneFullUpdateIIR` | `0x0D80E7C9` | `WorldObjectCodec` | [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact] |
| V3 | `TradeskillReplyIIR` | `0x9062D559` | `TradeskillReplyCodec` | [V3: TradeskillReplyCodecTests.Build_EmptyCatalog_IsAValidEmptyList] |
| V3 | `TradeskillRequestIIR` | `0x06B9CD94` | `TradeskillRequestCodec` | [V3: TradeskillRequestCodecTests.CapturedRequest_RoundTripsByteExact] |
| V3 | `UpdateInventoryIIR` | `0xA74D2814` | `UpdateInventoryCodec` | [V3: V1IirCodecTests.UpdateInventory_RoundTripsCapturedBody_AndCarriesTheItemProtobuf] |
| V3 | `UpdateSimpleDynelIIR` | `0x30536411` | `UpdateSimpleDynelCodec` | [V3: WorldLootTakeTests.BagConsumedVisual_MatchesCapture] |
| V2 | `AreaFormulaIIR` | `0xCE30BE4E` | `AreaFormulaCodec` |  |
| V2 | `ChainKillIIR` | `0x0EED7068` | `ChainKillCodec` |  |
| V2 | `CharInPlayIIR` | `0x864CFEF8` | `CharInPlayCodec` |  |
| V2 | `CharacterActionIIR` | `0x6B6FD368` | `CharacterActionCodec` |  |
| V2 | `CrimeIIR` | `0xC7954504` | `CrimeCodec` |  |
| V2 | `DeleteItemIIR` | `0x72491DE9` | `DeleteItemCodec` |  |
| V2 | `FanaticCommandIIR` | `0xEE4AA85C` | `FanaticCommandCodec` |  |
| V2 | `FeedbackIIR` | `0xBA1FD222` | `FeedbackCodec` |  |
| V2 | `KnubotFinishTradeIIR` | `0x7446F3FA` | `KnubotFinishTradeCodec` |  |
| V2 | `LockedToChangedIIR` | `0x6B0AEC58` | `LockedToChangedCodec` |  |
| V2 | `MoveInventoryItemIIR` | `0x2A8A1B49` | `MoveInventoryItemCodec` |  |
| V2 | `RegenIIR` | `0x28D0AC99` | `RegenCodec` |  |
| V2 | `SetMorphValueIIR` | `0xBADF5A4B` | `SetMorphValueCodec` |  |
| V2 | `SetShadowCoverageIIR` | `0xD6C1F0CC` | `SetShadowCoverageCodec` |  |
| V2 | `SpellBookIIR` | `0x6BA10936` | `SpellBookCodec` |  |
| V2 | `TemplateActionIIR` | `0x6C300937` | `TemplateActionCodec` |  |

**V2** rows are built but have no byte-exact golden yet; **V1** rows are genuinely undecoded (see
[`gaps.md`](../gaps.md)). A V3 row with an empty golden column is byte-exact via a codec whose test the
scan could not name to a single method (rare); its codec still has a golden per `coverage.md`.
