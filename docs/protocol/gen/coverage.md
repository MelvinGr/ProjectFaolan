# Coverage matrix (generated)

> **GENERATED — do not edit.** Derived from real code references in the repo:
> a codec covers an IIR type when it references `GameServer0X2000Opcodes.<Type>`; a codec has a
> byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

## Headline

- **819** RPC method opcodes — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).
- **75** IIR carrier types: **52 V3** (byte-exact builder), **14 V2** (built, no golden), **9 V1** (no builder found).
- **102** codec classes, **83** with a byte-exact test.
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
| V3 | `BehaviorControlCenterIIR` | `BehaviorAckBody`, `BehaviorAnimationCodec`, `BehaviorHeartbeatCodec`, `CombatPacketCodec`, `CreateEditorRecordBuilder`, `WorldDeath` |
| V3 | `CharDCMoveIIR` | `CharDCMove`, `CharDCMoveCodec` |
| V3 | `CharInPlayIIR` | `CreateEditorRecordBuilder` |
| V3 | `CharacterActionIIR` | `CreateEditorRecordBuilder` |
| V3 | `ChatTextIIR` | `ChatTextCodec` |
| V3 | `ClimbingDynelFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `CombatReactiveIIR` | `SpellweaveCodec` |
| V3 | `CombatShieldIIR` | `CombatShieldBuilder`, `CombatShieldCodec` |
| V3 | `ComboBookIIR` | `ComboBookIirCodec`, `CreateEditorRecordBuilder` |
| V3 | `DamageResultIIR` | `CombatEffectBuilder`, `CombatPacketCodec` |
| V3 | `DestructibleFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `DoorFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `EnergyIIR` | `CombatPacketCodec`, `CreateEditorRecordBuilder` |
| V3 | `FeedbackIIR` | `CreateEditorRecordBuilder` |
| V3 | `FormatFeedbackIIR` | `FormatFeedbackCodec` |
| V3 | `GameActionListIIR` | `GameActionListCodec` |
| V3 | `GameTimeIIR` | `CreateEditorRecordBuilder` |
| V3 | `HateListUpdateIIR` | `HateListCodec` |
| V3 | `IntendedTeleportIIR` | `TeleportCodec` |
| V3 | `KnubotAnswerListIIR` | `KnubotCodec` |
| V3 | `KnubotAppendTextIIR` | `KnubotCodec` |
| V3 | `KnubotCloseChatWindowIIR` | `KnubotCodec` |
| V3 | `KnubotOpenChatWindowIIR` | `KnubotCodec` |
| V3 | `LootBagFullUpdateIIR` | `LootBagMessageCodec`, `LootBagTemplate` |
| V3 | `LootRemoveItemsIIR` | `WorldLoot` |
| V3 | `MagicBuffIIR` | `NpcSpellCastBuilder`, `WorldDeath` |
| V3 | `MoveTaskIIR` | `MoveTaskBuilder`, `MoveTaskCodec`, `MoveTaskComposite`, `WorldDeath` |
| V3 | `N3TeleportIIR` | `TeleportCodec` |
| V3 | `N3ToClientFadeIIR` | `WorldLoot` |
| V3 | `NewAllowedLooterIIR` | `WorldLoot` |
| V3 | `NewCurrentLooterIIR` | `WorldLoot` |
| V3 | `NewLevelIIR` | `NewLevelIirCodec` |
| V3 | `PetIIR` | `BehaviorAnimationCodec`, `PetIirCodec`, `SpellweaveCodec` |
| V3 | `QuestFullUpdateIIR` | `WorldQuest` |
| V3 | `QuestGoalIIR` | `WorldQuest` |
| V3 | `QuestIIR` | `WorldQuest` |
| V3 | `QuestJournalIIR` | `WorldQuest` |
| V3 | `QuestLogListIIR` | `WorldQuest` |
| V3 | `RegenIIR` | `CreateEditorRecordBuilder` |
| V3 | `ScryScriptIIR` | `ScryScript` |
| V3 | `SetMorphValueIIR` | `CreateEditorRecordBuilder` |
| V3 | `SetPosIIR` | `SetPosCodec` |
| V3 | `SimpleCharFullUpdateIIR` | `PlayerSpawnScaffoldCodec` |
| V3 | `SimpleCharKilledByUpdateIIR` | `WorldDeath` |
| V3 | `SimpleDynelFullUpdateIIR` | `SimpleDynelMessageCodec` |
| V3 | `SimpleDynelVisualChangeIIR` | `SimpleDynelMessageCodec`, `WorldLoot` |
| V3 | `StatDamageIIR` | `CombatEffectBuilder`, `SpellweaveCodec`, `StatDamageCodec` |
| V3 | `StatIIR` | `CreateEditorRecordBuilder`, `SpellweaveCodec` |
| V3 | `StatUpdateIIR` | `CreateEditorRecordBuilder`, `StatUpdateCodec` |
| V3 | `StopIIR` | `StopCodec` |
| V3 | `TombStoneFullUpdateIIR` | `DynelFullUpdateCodec` |
| V3 | `UpdateSimpleDynelIIR` | `WorldLoot` |
| V2 | `BankIIR` | `BankService` *(inline)* |
| V2 | `CoreClaimIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `DeleteItemIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `FullPlayerCharacterIIR` | `GameServerListener.Outbound` *(inline)* |
| V2 | `GuildUpdateIIR` | `GuildTagBody` *(inline)* |
| V2 | `KnubotAnswerIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `LookAtIIR` | `ShopOpenBuilder` |
| V2 | `MoveInventoryItemIIR` | `GameServerListener.Inbound` *(inline)* |
| V2 | `N3ToClientQuitIIR` | `WorldDespawn` |
| V2 | `PlayfieldFIIR` | `GameServerListener.Outbound` *(inline)* |
| V2 | `SetNameIIR` | `CreateFlowService` *(inline)* |
| V2 | `ThreatLevelIIR` | `ThreatLevelCodec` *(inline)* |
| V2 | `TradeskillReplyIIR` | `GameServerListener.Outbound` *(inline)* |
| V2 | `TradeskillRequestIIR` | `GameServerListener.Inbound` *(inline)* |
| V1 | `AreaFormulaIIR` | — |
| V1 | `ChainKillIIR` | — |
| V1 | `CrimeIIR` | — |
| V1 | `KnubotFinishTradeIIR` | — |
| V1 | `KnubotStartTradeIIR` | — |
| V1 | `LockedToChangedIIR` | — |
| V1 | `SpellBookIIR` | — |
| V1 | `TemplateActionIIR` | — |
| V1 | `UpdateInventoryIIR` | — |
