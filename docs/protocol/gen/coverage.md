# Coverage matrix (generated)

> **GENERATED — do not edit.** Derived from the codecs themselves: a codec covers an IIR type when it
> **declares** it (`HandledTypes`, or a `const uint *Opcode` = `(uint)GameServer0X2000Opcodes.<Type>`);
> a codec has a byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

## Headline

- **821** RPC method opcodes — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).
- **77** IIR carrier types: **73 V3** (byte-exact builder), **3 V2** (built, no golden), **1 V1** (no builder found).
- **59** codec classes, **56** with a byte-exact test.
- **44** Python dissectors (`Captures/scripts/decode_*.py`).

### The distinction this table exists to make

Every opcode is named (V1). That is NOT the same as its message body being decoded. A type is:
- **V3** — a builder reproduces captured bytes (a golden test pins it). Provably correct.
- **V2** — a codec declares it, or an inline listener builder names its opcode member, but no
  byte-exact golden guards it yet.
- **V1** — nothing builds or parses it. Genuinely undecoded.

V1 now means what it says. A codec cannot emit an IIR without declaring it: `CodecConventionTests`
forbids a bare opcode hex literal anywhere in `Codecs/`, so every emitter names its
`GameServer0X2000Opcodes.<Type>` member and is attributed here. (Before #317 the scan fell back on a
`<Name>Iir` identifier regex, and ~9 built types showed a false V1.)

**Limitation (honest):** RPC method opcodes are dispatched inline in the listeners with no explicit
opcode→handler map, so their per-body decode status is not auto-correlated here — only their
existence (V1). The IIR-type rows below ARE code-correlated. The byte-exact-test signal is a
heuristic (codec class referenced by a test that asserts on bytes); it can over- or under-count at
the margins, but never invents a golden that isn't there.

## IIR carrier types

| Level | IIR type | Built by |
|-------|----------|----------|
| V3 | `AreaFormulaIIR` | `AreaFormulaCodec` |
| V3 | `BankIIR` | `BankIirCodec` |
| V3 | `BehaviorControlCenterIIR` | `BehaviorAckBody`, `BehaviorAnimationCodec`, `BehaviorHeartbeatCodec`, `CombatPacketCodec`, `WorldDeath` |
| V3 | `ChainKillIIR` | `ChainKillCodec` |
| V3 | `CharDCMoveIIR` | `CharDCMoveCodec` |
| V3 | `CharInPlayIIR` | `CreateEditorRecordBuilder` |
| V3 | `CharacterActionIIR` | `CreateEditorRecordBuilder` |
| V3 | `ChatTextIIR` | `ChatTextCodec` |
| V3 | `ClimbingDynelFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `CombatReactiveIIR` | `SpellweaveCodec` |
| V3 | `CombatShieldIIR` | `CombatShieldCodec` |
| V3 | `ComboBookIIR` | `ComboBookIirCodec` |
| V3 | `CoreClaimIIR` | `CoreClaimListCodec` |
| V3 | `CrimeIIR` | `CrimeCodec` |
| V3 | `DamageResultIIR` | `CombatEffectBuilder`, `CombatPacketCodec` |
| V3 | `DestructibleFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `DoorFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `EnergyIIR` | `CombatPacketCodec` |
| V3 | `FeatIIR` | `FeatIirCodec` |
| V3 | `FeedbackIIR` | `FeedbackIirCodec` |
| V3 | `FormatFeedbackIIR` | `FormatFeedbackCodec` |
| V3 | `FullPlayerCharacterIIR` | `FullPlayerCharacterBuilder` |
| V3 | `GameActionListIIR` | `GameActionListCodec` |
| V3 | `GameTimeIIR` | `GameTimeCodec` |
| V3 | `GuildUpdateIIR` | `GuildTagBody` |
| V3 | `HateListUpdateIIR` | `HateListCodec` |
| V3 | `IntendedTeleportIIR` | `TeleportCodec` |
| V3 | `KnubotAnswerIIR` | `KnubotCodec` |
| V3 | `KnubotAnswerListIIR` | `ConversationScreenDecoder`, `KnubotCodec` |
| V3 | `KnubotAppendTextIIR` | `ConversationScreenDecoder`, `KnubotCodec` |
| V3 | `KnubotCloseChatWindowIIR` | `ConversationScreenDecoder`, `KnubotCodec` |
| V3 | `KnubotFinishTradeIIR` | `KnubotFinishTradeCodec` |
| V3 | `KnubotOpenChatWindowIIR` | `ConversationScreenDecoder`, `KnubotCodec` |
| V3 | `KnubotStartTradeIIR` | `KnubotStartTradeCodec` |
| V3 | `LockedToChangedIIR` | `LockedToChangedCodec` |
| V3 | `LookAtIIR` | `ShopOpenBuilder` |
| V3 | `LootBagFullUpdateIIR` | `LootBagMessageCodec`, `LootContainerTemplate` |
| V3 | `LootRemoveItemsIIR` | `WorldLoot` |
| V3 | `MagicBuffIIR` | `NpcSpellCastBuilder`, `WorldDeath` |
| V3 | `MoveTaskIIR` | `MoveTaskCodec`, `MoveTaskComposite`, `WorldDeath` |
| V3 | `N3TeleportIIR` | `TeleportCodec` |
| V3 | `N3ToClientFadeIIR` | `WorldLoot` |
| V3 | `N3ToClientQuitIIR` | `WorldDespawn` |
| V3 | `NewAllowedLooterIIR` | `WorldLoot` |
| V3 | `NewCurrentLooterIIR` | `WorldLoot` |
| V3 | `NewLevelIIR` | `NewLevelIirCodec` |
| V3 | `PetIIR` | `PetIirCodec` |
| V3 | `QuestFullUpdateIIR` | `WorldQuest` |
| V3 | `QuestGoalIIR` | `WorldQuest` |
| V3 | `QuestIIR` | `WorldQuest` |
| V3 | `QuestJournalIIR` | `WorldQuest` |
| V3 | `QuestLogListIIR` | `WorldQuest` |
| V3 | `RegenIIR` | `RegenCodec` |
| V3 | `ScryScriptIIR` | `ScryScript` |
| V3 | `SetMorphValueIIR` | `CreateEditorRecordBuilder` |
| V3 | `SetNameIIR` | `CreateEditorRecordBuilder` |
| V3 | `SetPosIIR` | `SetPosCodec` |
| V3 | `SimpleCharFullUpdateIIR` | `PlayerSpawnScaffoldCodec` |
| V3 | `SimpleCharKilledByUpdateIIR` | `WorldDeath` |
| V3 | `SimpleDynelFullUpdateIIR` | `SimpleDynelMessageCodec` |
| V3 | `SimpleDynelVisualChangeIIR` | `SimpleDynelMessageCodec`, `WorldLoot` |
| V3 | `SpellBookIIR` | `SpellBookCodec` |
| V3 | `StatDamageIIR` | `CombatEffectBuilder`, `SpellweaveCodec`, `StatDamageCodec` |
| V3 | `StatIIR` | `SpellweaveCodec` |
| V3 | `StatUpdateIIR` | `StatUpdateCodec` |
| V3 | `StopIIR` | `StopCodec` |
| V3 | `TemplateActionIIR` | `TemplateActionCodec` |
| V3 | `ThreatLevelIIR` | `ThreatLevelCodec` |
| V3 | `TombStoneFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `TradeskillReplyIIR` | `TradeskillRecipeCodec` |
| V3 | `TradeskillRequestIIR` | `TradeskillRecipeCodec` |
| V3 | `UpdateInventoryIIR` | `UpdateInventoryCodec` |
| V3 | `UpdateSimpleDynelIIR` | `WorldLoot` |
| V2 | `DeleteItemIIR` | `InventoryDeleteCodec` |
| V2 | `MoveInventoryItemIIR` | `InventoryMoveCodec` |
| V2 | `PlayfieldFIIR` | `MapChangeCodec` |
| V1 | `SetShadowCoverageIIR` | — |
