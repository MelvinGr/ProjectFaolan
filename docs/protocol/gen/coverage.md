# Coverage matrix (generated)

> **GENERATED — do not edit.** Derived from the codecs themselves: a codec covers an IIR type when it
> **declares** it (`HandledTypes`, or a `const uint *Opcode` = `(uint)GameServer0X2000Opcodes.<Type>`);
> a codec has a byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

## Headline

- **826** RPC method opcodes — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).
- **80** IIR carrier types: **62 V3** (byte-exact builder), **18 V2** (built, no golden), **0 V1** (no builder found).
- **67** codec classes, **50** with a byte-exact test.
- **8** Python dissectors (`tools/capture/decode_*.py`).

### The distinction this table exists to make

Every opcode is named (V1). That is NOT the same as its message body being decoded. A type is:
- **V3** — a builder reproduces captured bytes (a golden test pins it). Provably correct.
- **V2** — a codec declares it, or an inline listener builder names its opcode member, but no
  byte-exact golden guards it yet.
- **V1** — nothing builds or parses it. Genuinely undecoded.

⚠️ **A V2 is not automatically a to-do.** V3 is defined as *a BUILDER reproducing captured bytes*, so a
message we only ever RECEIVE can never reach it — there is nothing to build. The parse-only c2s types
sit at V2 permanently and correctly; read the remaining V2 count as "types with no golden", not as work
outstanding, and check the direction before treating one as a gap.

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
| V3 | `AddToGroupIIR` | `GroupMembershipCodec` |
| V3 | `BankIIR` | `BankCodec` |
| V3 | `BehaviorControlCenterIIR` | `BehaviorControlCenterCodec` |
| V3 | `CharDCMoveIIR` | `CharDCMoveCodec` |
| V3 | `ChatTextIIR` | `ChatTextCodec` |
| V3 | `ClimbingDynelFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `CombatReactiveIIR` | `CombatReactiveCodec` |
| V3 | `CombatShieldIIR` | `CombatShieldCodec` |
| V3 | `ComboBookIIR` | `ComboBookCodec` |
| V3 | `CoreClaimIIR` | `CoreClaimCodec` |
| V3 | `DamageResultIIR` | `DamageResultCodec` |
| V3 | `DestructibleFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `DoorFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `EnergyIIR` | `EnergyCodec` |
| V3 | `FeatIIR` | `FeatCodec` |
| V3 | `FormatFeedbackIIR` | `FormatFeedbackCodec` |
| V3 | `FullPlayerCharacterIIR` | `FullPlayerCharacterCodec` |
| V3 | `GameActionListIIR` | `GameActionListCodec` |
| V3 | `GameTimeIIR` | `GameTimeCodec` |
| V3 | `GuildUpdateIIR` | `GuildUpdateCodec` |
| V3 | `HateListUpdateIIR` | `HateListUpdateCodec` |
| V3 | `IntendedTeleportIIR` | `TeleportCodec` |
| V3 | `KnubotAnswerIIR` | `KnubotCodec` |
| V3 | `KnubotAnswerListIIR` | `KnubotCodec` |
| V3 | `KnubotAppendTextIIR` | `KnubotCodec` |
| V3 | `KnubotCloseChatWindowIIR` | `KnubotCodec` |
| V3 | `KnubotOpenChatWindowIIR` | `KnubotCodec` |
| V3 | `KnubotStartTradeIIR` | `KnubotStartTradeCodec` |
| V3 | `LookAtIIR` | `LookAtCodec` |
| V3 | `LootBagFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `LootRemoveItemsIIR` | `LootRemoveItemsCodec` |
| V3 | `MagicBuffIIR` | `MagicBuffCodec` |
| V3 | `MainQuestUpdateIIR` | `MainQuestUpdateCodec` |
| V3 | `MoveTaskIIR` | `MoveTaskCodec` |
| V3 | `N3TeleportIIR` | `TeleportCodec` |
| V3 | `N3ToClientFadeIIR` | `N3ToClientFadeCodec` |
| V3 | `N3ToClientQuitIIR` | `N3ToClientQuitCodec` |
| V3 | `NewAllowedLooterIIR` | `NewLooterCodec` |
| V3 | `NewCurrentLooterIIR` | `NewLooterCodec` |
| V3 | `NewLevelIIR` | `NewLevelCodec` |
| V3 | `PetIIR` | `PetCodec` |
| V3 | `PlayfieldFIIR` | `PlayfieldFCodec` |
| V3 | `QuestFullUpdateIIR` | `QuestFullUpdateCodec` |
| V3 | `QuestGoalIIR` | `QuestGoalCodec` |
| V3 | `QuestIIR` | `QuestCodec` |
| V3 | `QuestJournalIIR` | `QuestJournalCodec` |
| V3 | `RemoveFromGroupIIR` | `GroupMembershipCodec` |
| V3 | `ScryScriptIIR` | `ScryScriptCodec` |
| V3 | `SetNameIIR` | `SetNameCodec` |
| V3 | `SetPosIIR` | `SetPosCodec` |
| V3 | `SimpleCharFullUpdateIIR` | `SimpleCharFullUpdateCodec` |
| V3 | `SimpleCharKilledByUpdateIIR` | `SimpleCharKilledByUpdateCodec` |
| V3 | `SimpleDynelFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `SimpleDynelVisualChangeIIR` | `SimpleDynelVisualChangeCodec` |
| V3 | `StatDamageIIR` | `StatDamageCodec` |
| V3 | `StatUpdateIIR` | `StatUpdateCodec` |
| V3 | `StopIIR` | `StopCodec` |
| V3 | `ThreatLevelIIR` | `ThreatLevelCodec` |
| V3 | `TombStoneFullUpdateIIR` | `WorldObjectCodec` |
| V3 | `TradeskillReplyIIR` | `TradeskillReplyCodec` |
| V3 | `UpdateInventoryIIR` | `UpdateInventoryCodec` |
| V3 | `UpdateSimpleDynelIIR` | `UpdateSimpleDynelCodec` |
| V2 | `AreaFormulaIIR` | `AreaFormulaCodec` |
| V2 | `ChainKillIIR` | `ChainKillCodec` |
| V2 | `CharInPlayIIR` | `CharInPlayCodec` |
| V2 | `CharacterActionIIR` | `CharacterActionCodec` |
| V2 | `CrimeIIR` | `CrimeCodec` |
| V2 | `DeleteItemIIR` | `DeleteItemCodec` |
| V2 | `FanaticCommandIIR` | `FanaticCommandCodec` |
| V2 | `FeedbackIIR` | `FeedbackCodec` |
| V2 | `KnubotFinishTradeIIR` | `KnubotFinishTradeCodec` |
| V2 | `LockedToChangedIIR` | `LockedToChangedCodec` |
| V2 | `MoveInventoryItemIIR` | `MoveInventoryItemCodec` |
| V2 | `RegenIIR` | `RegenCodec` |
| V2 | `SetMorphValueIIR` | `SetMorphValueCodec` |
| V2 | `SetShadowCoverageIIR` | `SetShadowCoverageCodec` |
| V2 | `SpellBookIIR` | `SpellBookCodec` |
| V2 | `StatIIR` | `StatCodec` |
| V2 | `TemplateActionIIR` | `TemplateActionCodec` |
| V2 | `TradeskillRequestIIR` | `GameServerListener.Inbound` *(inline)* |
