# IIR carrier type sub-opcodes (generated)

> **GENERATED — do not edit.** Source: the compiled `GameServer0X2000Opcodes` enum in
> `Faolan.Core/Enums/Opcodes.cs`, read by reflection.
> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.

These are the 32-bit type ids carried inside the GameServer `0x2000` (`DispatchPassBlob`) carrier —
the sub-opcode after the RPC method opcode that says which IIR record type the blob holds.

## Verification [V1]

Each type id is the **BKDR-31 hash** (`h = h*31 + c`, Java `String.hashCode`) of the client's IIR
type name. The **recovered name** column is the exact string whose BKDR-31 equals the id, found by
recomputing the hash over candidates derived from our member name (± `_c`/`_t`, lowercase `n3`) —
so a filled cell is a self-contained proof: `BKDR-31(name) == id`. This holds without parsing any
external doc; the enum + the hash function are the whole source.

- **76** ids: client type name recovered + BKDR-31-verified.
- **1** ids: 32-bit hash (V1-recovered from the client) whose exact source string our name doesn't reproduce.

| Type id | Recovered client name | Our enum name | Note |
|---------|-----------------------|---------------|------|
| `0x045B0B0F` | `KnubotOpenChatWindowIIR_c` ✓ | `KnubotOpenChatWindowIIR` | KnubotOpenChatWindowIIR_c  (x43) |
| `0x04B80B24` | `SetPosIIR_c` ✓ | `SetPosIIR` | SetPosIIR_c  (x29) |
| `0x04B82B2D` | `n3ToClientQuitIIR_t` ✓ | `N3ToClientQuitIIR` | n3ToClientQuitIIR_t  (x3249) |
| `0x06B9CD94` | `TradeskillRequestIIR_c` ✓ | `TradeskillRequestIIR` | TradeskillRequestIIR_c - recipe-book REQUEST (client opened Crafting->Recipes); reply = TradeskillReplyIIR |
| `0x06EC1255` | `CoreClaimIIR` ✓ | `CoreClaimIIR` | CoreClaimIIR  (x8) - "InventoryClaimsButton" |
| `0x0774C8C1` | `QuestGoalIIR_c` ✓ | `QuestGoalIIR` | QuestGoalIIR_c  (x27) |
| `0x0BA1C1CB` | `BankIIR_t` ✓ | `BankIIR` | BankIIR_t  (x3) |
| `0x0D155695` | `LookAtIIR_t` ✓ | `LookAtIIR` | LookAtIIR_t  (x812) - "SelectDeselect" |
| `0x0D80E7C9` | `TombStoneFullUpdateIIR_c` ✓ | `TombStoneFullUpdateIIR` | TombStoneFullUpdateIIR_c  (x6) |
| `0x0EED7068` | `ChainKillIIR_t` ✓ | `ChainKillIIR` | ChainKillIIR_t  (x28) |
| `0x10D27BC0` | `IntendedTeleportIIR_c` ✓ | `IntendedTeleportIIR` | IntendedTeleportIIR_c  (x15) |
| `0x1745EF6C` | `ThreatLevelIIR_c` ✓ | `ThreatLevelIIR` | ThreatLevelIIR_c  (x162) |
| `0x1E2E0139` | `NewAllowedLooterIIR_c` ✓ | `NewAllowedLooterIIR` | NewAllowedLooterIIR_c  (x59) |
| `0x1F4CB0F7` | `PetIIR_c` ✓ | `PetIIR` | PetIIR_c  (x6) |
| `0x1FD6412B` | `ClimbingDynelFullUpdateIIR_c` ✓ | `ClimbingDynelFullUpdateIIR` | ClimbingDynelFullUpdateIIR_c  (x87) |
| `0x2550C99A` | `GuildUpdateIIR_c` ✓ | `GuildUpdateIIR` | GuildUpdateIIR_c  (x3) |
| `0x28D0AC99` | `RegenIIR_c` ✓ | `RegenIIR` | RegenIIR_c  (x554) |
| `0x28DA68A0` | `FeatIIR` ✓ | `FeatIIR` | FeatIIR (BKDR-31 of "FeatIIR") - feat train/unlock: 0x3F1-framed feat-id list; c2s state 0x04 request, s2c 0x05 ack (#251) |
| `0x2A8A1B49` | `MoveInventoryItemIIR_t` ✓ | `MoveInventoryItemIIR` | MoveInventoryItemIIR_t  (x16) - "MoveItemInInventory" |
| `0x30536411` | `UpdateSimpleDynelIIR_c` ✓ | `UpdateSimpleDynelIIR` | UpdateSimpleDynelIIR_c  (x30) |
| `0x33A56FB0` | `DoorFullUpdateIIR_c` ✓ | `DoorFullUpdateIIR` | DoorFullUpdateIIR_c  (x89) - world door/object full update |
| `0x40749B8A` | `EnergyIIR` ✓ | `EnergyIIR` | EnergyIIR  (x1039) |
| `0x4517AA81` | `CharDCMoveIIR_t` ✓ | `CharDCMoveIIR` | CharDCMoveIIR_t  (x11005) - entity move ("Move"); raw-body floats are big-endian |
| `0x4A3D899A` | `KnubotAppendTextIIR_c` ✓ | `KnubotAppendTextIIR` | KnubotAppendTextIIR_c  (x217) |
| `0x4B2A47BF` | `KnubotStartTradeIIR_c` ✓ | `KnubotStartTradeIIR` | KnubotStartTradeIIR_c  (x2) |
| `0x4D7D9123` | `KnubotAnswerIIR_c` ✓ | `KnubotAnswerIIR` | KnubotAnswerIIR_c  (x194) - NPC dialogue ("Knubot" = the NPC dialogue system) |
| `0x4F57DC08` | `GameTimeIIR_t` ✓ | `GameTimeIIR` | GameTimeIIR_t  (x33) - "SetDayTime" |
| `0x504B7924` | — *(source string not reproduced)* | `QuestLogListIIR` | the active-quest LIST that populates the J-panel (WorldQuest.BuildQuestLogList) |
| `0x54C02089` | `CombatReactiveIIR_c` ✓ | `CombatReactiveIIR` | CombatReactiveIIR_c  (x17) |
| `0x5A32F0D7` | `n3TeleportIIR_t` ✓ | `N3TeleportIIR` | n3TeleportIIR_t  (x27) - teleport |
| `0x5D85BFC7` | `PlayfieldFIIR_t` ✓ | `PlayfieldFIIR` | PlayfieldFIIR_t  (x33) - "MapChange" |
| `0x63268258` | `QuestFullUpdateIIR` ✓ | `QuestFullUpdateIIR` | QuestFullUpdateIIR  (x28) |
| `0x642CD3D6` | `BehaviorControlCenterIIR_c` ✓ | `BehaviorControlCenterIIR` | BehaviorControlCenterIIR_c  (x22474) - the generic action/behavior channel ("Interact") |
| `0x66180B20` | `n3ToClientFadeIIR_t` ✓ | `N3ToClientFadeIIR` | n3ToClientFadeIIR_t  (x87) - screen fade |
| `0x66AEDD50` | `SimpleDynelFullUpdateIIR_c` ✓ | `SimpleDynelFullUpdateIIR` | SimpleDynelFullUpdateIIR_c  (x1575) - world object spawn ("SpawnObject") |
| `0x6B0AEC58` | `LockedToChangedIIR_t` ✓ | `LockedToChangedIIR` | LockedToChangedIIR_t  (x58) |
| `0x6B6FD368` | `CharacterActionIIR_t` ✓ | `CharacterActionIIR` | CharacterActionIIR_t  (x136) - "CloseGame" |
| `0x6B87E316` | `MagicBuffIIR_c` ✓ | `MagicBuffIIR` | MagicBuffIIR_c  (x728) - a magic buff/effect ("ApplySpell") |
| `0x6BA10936` | `SpellBookIIR_t` ✓ | `SpellBookIIR` | SpellBookIIR_t  (x1) |
| `0x6BD56736` | `DestructibleFullUpdateIIR_c` ✓ | `DestructibleFullUpdateIIR` | DestructibleFullUpdateIIR_c  (x189) |
| `0x6C300937` | `TemplateActionIIR_t` ✓ | `TemplateActionIIR` | TemplateActionIIR_t  (x7) |
| `0x6D314743` | `NewLevelIIR_t` ✓ | `NewLevelIIR` | NewLevelIIR_t - level-up notification (built by NewLevelIirCodec) |
| `0x70010162` | `LootRemoveItemsIIR_c` ✓ | `LootRemoveItemsIIR` | LootRemoveItemsIIR_c  (x42) |
| `0x72491DE9` | `DeleteItemIIR_t` ✓ | `DeleteItemIIR` | DeleteItemIIR_t - "DropItem"; body = ProtoDeleteItemIIR (NOT the sibling move message - they alias on the wire; see InventoryDeleteCodec) |
| `0x737A6DF9` | `FullPlayerCharacterIIR` ✓ | `FullPlayerCharacterIIR` | FullPlayerCharacterIIR  (x33) - the whole player character (inc. inventory); see carve-out below |
| `0x7446F3FA` | `KnubotFinishTradeIIR_c` ✓ | `KnubotFinishTradeIIR` | KnubotFinishTradeIIR_c  (x2) |
| `0x79F8C7A1` | `QuestJournalIIR_c` ✓ | `QuestJournalIIR` | QuestJournalIIR_c  (x17) |
| `0x864CFEF8` | `CharInPlayIIR_t` ✓ | `CharInPlayIIR` | CharInPlayIIR_t  (x74) - "ReadyForPlayScreen" |
| `0x8A01E5A1` | `HateListUpdateIIR` ✓ | `HateListUpdateIIR` | HateListUpdateIIR  (x61) |
| `0x8D5A8B6B` | `FormatFeedbackIIR_t` ✓ | `FormatFeedbackIIR` | FormatFeedbackIIR_t  (x68) |
| `0x9062D559` | `TradeskillReplyIIR_c` ✓ | `TradeskillReplyIIR` | TradeskillReplyIIR_c - the recipe-book list (reply to TradeskillRequestIIR 0x06B9CD94) |
| `0x96B8DC59` | `StatUpdateIIR_c` ✓ | `StatUpdateIIR` | StatUpdateIIR_c  (x1595) - entity vitals/stat update (combat HP/Stamina/Mana); see docs/re/quests-and-combat.md |
| `0x96C46740` | `MoveTaskIIR_c` ✓ | `MoveTaskIIR` | MoveTaskIIR_c  (x5564) - movement task (the float-heavy state block) |
| `0x97E02314` | `QuestIIR_c` ✓ | `QuestIIR` | QuestIIR_c  (x34) - "DeleteQuest" |
| `0xA36D3B74` | `ScryScriptIIR_c` ✓ | `ScryScriptIIR` | ScryScriptIIR_c  (x390) - cutscene/scry scripts + char-editor callbacks (IntroDone, TheNameIs, ...) |
| `0xA74D2814` | `UpdateInventoryIIR_t` ✓ | `UpdateInventoryIIR` | UpdateInventoryIIR_t  (x30) |
| `0xA8BBEB7F` | `ComboBookIIR_c` ✓ | `ComboBookIIR` | ComboBookIIR_c  (x65) |
| `0xADCE0CDA` | `SetNameIIR_t` ✓ | `SetNameIIR` | SetNameIIR_t  (x4) - name commit in the create flow |
| `0xB066AFC2` | `SimpleCharKilledByUpdateIIR_t` ✓ | `SimpleCharKilledByUpdateIIR` | SimpleCharKilledByUpdateIIR_t  (x2) - kill/death |
| `0xBA1FD222` | `FeedbackIIR_t` ✓ | `FeedbackIIR` | FeedbackIIR_t  (x5) |
| `0xBADF5A4B` | `SetMorphValueIIR_c` ✓ | `SetMorphValueIIR` | SetMorphValueIIR_c  (x101) - character-creator morph ack |
| `0xC4AF39EB` | `KnubotCloseChatWindowIIR_c` ✓ | `KnubotCloseChatWindowIIR` | KnubotCloseChatWindowIIR_c  (x44) |
| `0xC7954504` | `CrimeIIR_c` ✓ | `CrimeIIR` | CrimeIIR_c  (x3) |
| `0xCE30BE4E` | `AreaFormulaIIR_t` ✓ | `AreaFormulaIIR` | AreaFormulaIIR_t  (x42) |
| `0xCFFD0519` | `CombatShieldIIR_c` ✓ | `CombatShieldIIR` | CombatShieldIIR_c  (x79) |
| `0xD06568C8` | `NewCurrentLooterIIR_c` ✓ | `NewCurrentLooterIIR` | NewCurrentLooterIIR_c  (x60) |
| `0xD6C1F0CC` | `SetShadowCoverageIIR_c` ✓ | `SetShadowCoverageIIR` | SetShadowCoverageIIR_c (BKDR-31 of "SetShadowCoverageIIR_c") - environment/lighting update |
| `0xD751EBB4` | `StopIIR_c` ✓ | `StopIIR` | StopIIR_c  (x2097) - stop moving |
| `0xE0B51102` | `ChatTextIIR_t` ✓ | `ChatTextIIR` | ChatTextIIR_t  (x1) |
| `0xE7F4F8F8` | `SimpleDynelVisualChangeIIR_c` ✓ | `SimpleDynelVisualChangeIIR` | SimpleDynelVisualChangeIIR_c  (x548) |
| `0xE9F24184` | `StatDamageIIR_t` ✓ | `StatDamageIIR` | StatDamageIIR_t  (x450) |
| `0xF096251E` | `LootBagFullUpdateIIR_c` ✓ | `LootBagFullUpdateIIR` | LootBagFullUpdateIIR_c  (x102) - loot bag (NOT the player bag) |
| `0xF164A4A5` | `KnubotAnswerListIIR_c` ✓ | `KnubotAnswerListIIR` | KnubotAnswerListIIR_c  (x177) |
| `0xF508F4C1` | `GameActionListIIR_t` ✓ | `GameActionListIIR` | GameActionListIIR_t  (x366) |
| `0xF98E10B3` | `StatIIR_t` ✓ | `StatIIR` | StatIIR_t  (x8167) |
| `0xFA015807` | `SimpleCharFullUpdateIIR_t` ✓ | `SimpleCharFullUpdateIIR` | SimpleCharFullUpdateIIR_t  (x3407) - entity spawn ("Spawn") |
| `0xFEC4158A` | `DamageResultIIR_c` ✓ | `DamageResultIIR` | DamageResultIIR_c  (x1134) - combat damage result |
