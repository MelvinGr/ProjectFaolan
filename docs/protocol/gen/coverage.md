# Coverage matrix (generated)

> **GENERATED — do not edit.** Derived from real code references in the repo:
> a codec covers an IIR type when it references `GameServer0X2000Opcodes.<Type>`; a codec has a
> byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

## Headline

- **819** RPC method opcodes — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).
- **74** IIR carrier types: **43 V3** (byte-exact builder), **13 V2** (built, no golden), **18 V1** (no builder found).
- **103** codec classes, **85** with a byte-exact test.
- **36** Python dissectors (`Captures/scripts/decode_*.py`).

### The distinction this table exists to make

Every opcode is named (V1). That is NOT the same as its message body being decoded. A type is:
- **V3** — a builder reproduces captured bytes (a golden test pins it). Provably correct.
- **V2** — a codec OR an inline listener builder constructs/parses it (it references the opcode member
  or the `<Name>Iir` identifier somewhere in the server), but no byte-exact golden guards it yet.
- **V1** — no server code references it by opcode member or `<Name>Iir` identifier. Either genuinely
  undecoded, OR built by an inconsistently-named builder the scan can't attribute (the builder-naming
  cleanup that would collapse this ambiguity is Gitea #317).

**Limitation (honest):** RPC method opcodes are dispatched inline in the listeners with no explicit
opcode→handler map, so their per-body decode status is not auto-correlated here — only their
existence (V1). The IIR-type rows below ARE code-correlated. The byte-exact-test signal is a
heuristic (codec class referenced by a test that asserts on bytes); it can over- or under-count at
the margins, but never invents a golden that isn't there.

## IIR carrier types

| Level | IIR type | Built by |
|-------|----------|----------|
| V3 | `BehaviorControlCenterIIR` | `BehaviorAckBody`, `BehaviorAnimationCodec`, `CombatPacketCodec`, `CreateEditorRecordBuilder` |
| V3 | `CharDCMoveIIR` | `CharDCMove`, `CharDCMoveCodec`, `SetPosCodec`, `StopCodec` |
| V3 | `CharInPlayIIR` | `CreateEditorRecordBuilder` |
| V3 | `CharacterActionIIR` | `CreateEditorRecordBuilder` |
| V3 | `CombatReactiveIIR` | `SpellweaveCodec` |
| V3 | `CombatShieldIIR` | `CombatShieldBuilder`, `CombatShieldCodec`, `HateListCodec`, `StatDamageCodec`, `ThreatLevelCodec` |
| V3 | `ComboBookIIR` | `ComboBookIirCodec`, `CreateEditorRecordBuilder` |
| V3 | `DamageResultIIR` | `CombatEffectBuilder`, `CombatPacketCodec` |
| V3 | `EnergyIIR` | `CombatPacketCodec`, `CreateEditorRecordBuilder` |
| V3 | `FeedbackIIR` | `CreateEditorRecordBuilder` |
| V3 | `GameActionListIIR` | `GameActionListCodec` |
| V3 | `GameTimeIIR` | `CreateEditorRecordBuilder` |
| V3 | `HateListUpdateIIR` | `CombatShieldCodec`, `HateListCodec`, `StatDamageCodec`, `ThreatLevelCodec` |
| V3 | `IntendedTeleportIIR` | `TeleportCodec` |
| V3 | `KnubotAnswerListIIR` | `KnubotCodec` |
| V3 | `KnubotAppendTextIIR` | `KnubotCodec` |
| V3 | `KnubotCloseChatWindowIIR` | `KnubotCodec` |
| V3 | `KnubotOpenChatWindowIIR` | `KnubotCodec` |
| V3 | `LootRemoveItemsIIR` | `WorldLoot` |
| V3 | `MagicBuffIIR` | `NpcSpellCastBuilder` |
| V3 | `MoveTaskIIR` | `MoveTaskBuilder`, `MoveTaskCodec`, `MoveTaskComposite`, `MoveTaskNode` |
| V3 | `N3TeleportIIR` | `TeleportCodec` |
| V3 | `N3ToClientFadeIIR` | `WorldLoot` |
| V3 | `NewAllowedLooterIIR` | `WorldLoot` |
| V3 | `NewCurrentLooterIIR` | `WorldLoot` |
| V3 | `PetIIR` | `BehaviorAnimationCodec`, `PetIirCodec`, `SpellweaveCodec` |
| V3 | `QuestFullUpdateIIR` | `QuestProgress`, `WorldQuest` |
| V3 | `QuestGoalIIR` | `QuestProgress`, `WorldQuest` |
| V3 | `QuestIIR` | `QuestProgress`, `WorldQuest` |
| V3 | `QuestJournalIIR` | `QuestProgress`, `WorldQuest` |
| V3 | `QuestLogListIIR` | `QuestProgress`, `WorldQuest` |
| V3 | `RegenIIR` | `CreateEditorRecordBuilder` |
| V3 | `ScryScriptIIR` | `ScryScript` |
| V3 | `SetMorphValueIIR` | `CreateEditorRecordBuilder` |
| V3 | `SetPosIIR` | `CharDCMove`, `CharDCMoveCodec`, `SetPosCodec`, `StopCodec` |
| V3 | `SimpleCharFullUpdateIIR` | `PlayerSpawnScaffoldCodec` |
| V3 | `SimpleDynelVisualChangeIIR` | `WorldLoot` |
| V3 | `StatDamageIIR` | `CombatEffectBuilder`, `CombatShieldCodec`, `HateListCodec`, `SpellweaveCodec`, `StatDamageCodec`, `ThreatLevelCodec` |
| V3 | `StatIIR` | `CreateEditorRecordBuilder`, `SpellweaveCodec` |
| V3 | `StatUpdateIIR` | `CreateEditorRecordBuilder` |
| V3 | `StopIIR` | `CharDCMove`, `CharDCMoveCodec`, `SetPosCodec`, `StopCodec` |
| V3 | `ThreatLevelIIR` | `CombatShieldCodec`, `HateListCodec`, `StatDamageCodec`, `ThreatLevelCodec` |
| V3 | `UpdateSimpleDynelIIR` | `WorldLoot` |
| V2 | `BankIIR` | `BankService` *(inline)* |
| V2 | `CoreClaimIIR` | `GameServerListener.PostReadyBurst` *(inline)* |
| V2 | `DeleteItemIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `GuildUpdateIIR` | `GuildTagBody` *(inline)* |
| V2 | `KnubotAnswerIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `LookAtIIR` | `ShopOpenBuilder` |
| V2 | `MoveInventoryItemIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `N3ToClientQuitIIR` | `WorldDespawn` |
| V2 | `PlayfieldFIIR` | `CreateFlowService` *(inline)* |
| V2 | `SetNameIIR` | `CreateFlowService` *(inline)* |
| V2 | `SimpleDynelFullUpdateIIR` | `CreateFlowService` *(inline)* |
| V2 | `TradeskillReplyIIR` | `GameServerListener.Outbound` *(inline)* |
| V2 | `TradeskillRequestIIR` | `GameServerListener.Inbound` *(inline)* |
| V1 | `AreaFormulaIIR` | — |
| V1 | `ChainKillIIR` | — |
| V1 | `ChatTextIIR` | — |
| V1 | `ClimbingDynelFullUpdateIIR` | — |
| V1 | `CrimeIIR` | — |
| V1 | `DestructibleFullUpdateIIR` | — |
| V1 | `DoorFullUpdateIIR` | — |
| V1 | `FormatFeedbackIIR` | — |
| V1 | `FullPlayerCharacterIIR` | — |
| V1 | `KnubotFinishTradeIIR` | — |
| V1 | `KnubotStartTradeIIR` | — |
| V1 | `LockedToChangedIIR` | — |
| V1 | `LootBagFullUpdateIIR` | — |
| V1 | `SimpleCharKilledByUpdateIIR` | — |
| V1 | `SpellBookIIR` | — |
| V1 | `TemplateActionIIR` | — |
| V1 | `TombStoneFullUpdateIIR` | — |
| V1 | `UpdateInventoryIIR` | — |
