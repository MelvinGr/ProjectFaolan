# AoC wire-id decode (BKDR-31 name hash) - all servers

Stage 3 of `docs/archive/opcode-re-plan.md`. The client id hash is **BKDR-31** (`h = h*31 + c`, Java's
`String.hashCode`), cracked in Stage 2 (18/18 known anchors). Ids below are resolved by hashing every
identifier in `AgeOfConan.exe` and matching. `[?]` = no exe string hashes to it (name not in binary, or
a collision picked wrong); 16-bit RpcHeader opcodes are method indices, not hashes — **RESOLVED 2026-06-12**
(see Stage 3b below).

**Update 2026-06-14 (#192):** `0x33A56FB0 = DoorFullUpdateIIR_c` (the last unidentified beach-burst type —
the map's doors); `0x00000000` is a null/terminator, not a type. The remaining GameServer `[?]` rows below
in the `0x013xxxxx` family plus `0x02165EFF` / `0x08006F31` / `0x0802E5D4` are **NOT IIR type hashes** — they
are per-character **instance ids** (ProtoEndpoint.instance), which the analyzer surfaces as a "sub-opcode"
because in-world the client streams 0x2000 packets keyed by its own instance (e.g. `0x01316E71` = hoxxling,
`0x02165EFF` = Strie). No name hashes to them because they aren't names.


## ProtoEndpoint.type ids (object-class / interface names)


| id | count | resolved name | other candidates |
|---|---|---|---|
| 0x6C67C147 | 72435 | GameInterface |  |
| 0xB171CE13 | 72435 | GameAgent |  |
| 0xA9ECB95D | 18069 | GameCharAgent |  |
| 0x8B87F791 | 18042 | GameCharInterface |  |
| 0x28377648 | 17431 | GameCharInterfaceProject |  |
| 0x5B9756FC | 17431 | GameCharAgentProject |  |
| 0x7D155738 | 503 | PlayerInterface |  |
| 0xBBD560C8 | 497 | CSPlayerInterface |  |
| 0x6DD55614 | 497 | CSPlayerAgent |  |
| 0x82F20484 | 411 | PlayerAgent |  |
| 0x0C384054 | 115 | UniverseInterface |  |
| 0x604DDBA0 | 115 | UniverseAgent |  |
| 0x1D359116 | 92 | [?] |  |
| 0xF2C2D5B5 | 56 | TradepostInterface |  |
| 0x3D744981 | 29 | TradepostAgent |  |

## 32-bit sub-opcodes inside 0x2000 (IIR / message type names)

> **Live on the wire (2026-07-08):** the Wireshark plugin dissects the IIR id out of
> GameAgent/GameInterface `0x2000` PassBlob bodies (`faolan.pkt.iir`, COL_INFO
> `DispatchPassBlob:MoveTaskIIR`), names generated from the `GameServer0X2000Opcodes` catalog in
> `Opcodes.cs`. Regenerated `*.tshark.json` dumps carry the id as an additive `Iir` key — decode
> scripts no longer need to re-parse `BodyHex` for it.


| sub-opcode | count | resolved name | servers |
|---|---|---|---|
| 0x642CD3D6 | 22474 | BehaviorControlCenterIIR_c | GameServer |
| 0x4517AA81 | 11005 | CharDCMoveIIR_t | GameServer |
| 0xF98E10B3 | 8167 | StatIIR_t | GameServer |
| 0x96C46740 | 5564 | MoveTaskIIR_c | GameServer |
| 0xFA015807 | 3407 | SimpleCharFullUpdateIIR_t | GameServer |
| 0x04B82B2D | 3249 | n3ToClientQuitIIR_t | GameServer |
| 0x08006F31 | 3020 | [?] | GameServer |
| 0xD751EBB4 | 2097 | StopIIR_c | GameServer |
| 0x01316E71 | 2065 | [?] | GameServer |
| 0x96B8DC59 | 1595 | StatUpdateIIR_c | GameServer |
| 0x66AEDD50 | 1575 | SimpleDynelFullUpdateIIR_c | GameServer |
| 0xFEC4158A | 1134 | DamageResultIIR_c | GameServer |
| 0x40749B8A | 1039 | EnergyIIR | GameServer |
| 0x0D155695 | 812 | LookAtIIR_t | GameServer |
| 0x6B87E316 | 728 | MagicBuffIIR_c | GameServer |
| 0x110099BD | 674 | [?] | GameServer |
| 0x02165EFF | 601 | [?] | GameServer |
| 0x28D0AC99 | 554 | RegenIIR_c | GameServer |
| 0xE7F4F8F8 | 548 | SimpleDynelVisualChangeIIR_c | GameServer |
| 0xE9F24184 | 450 | StatDamageIIR_t | GameServer |
| 0xA36D3B74 | 390 | ScryScriptIIR_c | GameServer |
| 0xF508F4C1 | 366 | GameActionListIIR_t | GameServer |
| 0x01316D8A | 328 | [?] | GameServer |
| 0x0802E5D4 | 272 | [?] | GameServer |
| 0x4A3D899A | 217 | KnubotAppendTextIIR_c | GameServer |
| 0x01307874 | 205 | [?] | GameServer |
| 0x4D7D9123 | 194 | KnubotAnswerIIR_c | GameServer |
| 0x6BD56736 | 189 | DestructibleFullUpdateIIR_c | GameServer |
| 0xF164A4A5 | 177 | KnubotAnswerListIIR_c | GameServer |
| 0x1745EF6C | 162 | ThreatLevelIIR_c | GameServer |
| 0x01316C57 | 156 | [?] | GameServer |
| 0x6B6FD368 | 136 | CharacterActionIIR_t | GameServer |
| 0x01316D2B | 133 | [?] | GameServer |
| 0xF096251E | 102 | LootBagFullUpdateIIR_c | GameServer |
| 0xBADF5A4B | 101 | SetMorphValueIIR_c | GameServer |
| 0x0004DEB9 | 97 | [?] | GameServer |
| 0x33A56FB0 | 89 | DoorFullUpdateIIR_c | GameServer |
| 0x66180B20 | 87 | n3ToClientFadeIIR_t | GameServer |
| 0x1FD6412B | 87 | ClimbingDynelFullUpdateIIR_c | GameServer |
| 0xCFFD0519 | 79 | CombatShieldIIR_c | GameServer |
| 0x864CFEF8 | 74 | CharInPlayIIR_t | GameServer |
| 0x00042164 | 72 | [?] | GameServer |
| 0x8D5A8B6B | 68 | FormatFeedbackIIR_t | GameServer |
| 0x00031635 | 66 | [?] | GameServer |
| 0x0004DEB1 | 66 | [?] | GameServer |
| 0xA8BBEB7F | 65 | ComboBookIIR_c | GameServer |
| 0x8A01E5A1 | 61 | HateListUpdateIIR | GameServer |
| 0xD06568C8 | 60 | NewCurrentLooterIIR_c | GameServer |
| 0x1E2E0139 | 59 | NewAllowedLooterIIR_c | GameServer |
| 0x6B0AEC58 | 58 | LockedToChangedIIR_t | GameServer |
| 0x000421AB | 55 | [?] | GameServer |
| 0x00043849 | 55 | [?] | GameServer |
| 0x021662C8 | 55 | [?] | GameServer |
| 0x0004384D | 53 | [?] | GameServer |
| 0x0004DEB2 | 51 | [?] | GameServer |
| 0x00017E2F | 50 | [?] | GameServer |
| 0x000421A3 | 50 | [?] | GameServer |
| 0x00017E32 | 49 | [?] | GameServer |
| 0x00043943 | 49 | [?] | GameServer |
| 0x0004384E | 48 | [?] | GameServer |
| 0x0004383E | 47 | [?] | GameServer |
| 0x0004384F | 47 | [?] | GameServer |
| 0x0003181A | 47 | [?] | GameServer |
| 0x00031817 | 46 | [?] | GameServer |
| 0x0004DEB6 | 46 | [?] | GameServer |
| 0xC4AF39EB | 44 | KnubotCloseChatWindowIIR_c | GameServer |
| 0x0004DEB5 | 44 | [?] | GameServer |
| 0x045B0B0F | 43 | KnubotOpenChatWindowIIR_c | GameServer |
| 0xCE30BE4E | 42 | AreaFormulaIIR_t | GameServer |
| 0x70010162 | 42 | LootRemoveItemsIIR_c | GameServer |
| 0x00043941 | 42 | [?] | GameServer |
| 0x000195AA | 40 | [?] | GameServer |
| 0x00042196 | 40 | [?] | GameServer |
| 0x000421A4 | 38 | [?] | GameServer |
| 0x000438A3 | 38 | [?] | GameServer |
| 0x00043905 | 38 | [?] | GameServer |
| 0x000421A0 | 37 | [?] | GameServer |
| 0x0004392B | 37 | [?] | GameServer |
| 0x000438A8 | 37 | [?] | GameServer |
| 0x00017E15 | 36 | d4e | GameServer |
| 0x0004217E | 36 | [?] | GameServer |
| 0x0004385E | 36 | [?] | GameServer |
| 0x000421BA | 35 | [?] | GameServer |
| 0x0004385F | 35 | [?] | GameServer |
| 0x97E02314 | 34 | QuestIIR_c | GameServer |
| 0x5D85BFC7 | 33 | PlayfieldFIIR_t | GameServer |
| 0x737A6DF9 | 33 | FullPlayerCharacterIIR | GameServer |
| 0x4F57DC08 | 33 | GameTimeIIR_t | GameServer |
| 0x0004385D | 33 | [?] | GameServer |
| 0x00042195 | 32 | [?] | GameServer |
| 0x000438A9 | 32 | [?] | GameServer |
| 0x000439AE | 32 | [?] | GameServer |
| 0x000438EC | 31 | [?] | GameServer |
| 0x000438EB | 31 | [?] | GameServer |
| 0x30536411 | 30 | UpdateSimpleDynelIIR_c | GameServer |
| 0x00017E30 | 30 | [?] | GameServer |
| 0xA74D2814 | 30 | UpdateInventoryIIR_t | GameServer |
| 0x00043888 | 30 | [?] | GameServer |
| 0x0004DEB3 | 30 | [?] | GameServer |
| 0x00017E2E | 29 | [?] | GameServer |
| 0x00017E0C | 29 | [?] | GameServer |
| 0x04B80B24 | 29 | SetPosIIR_c | GameServer |
| 0x000439AC | 29 | [?] | GameServer |
| 0x000317F8 | 29 | [?] | GameServer |
| 0x000317F9 | 29 | [?] | GameServer |
| 0x63268258 | 28 | QuestFullUpdateIIR | GameServer |
| 0x00017E2A | 28 | [?] | GameServer |
| 0x0EED7068 | 28 | ChainKillIIR_t | GameServer |
| 0x0004392A | 28 | [?] | GameServer |
| 0x5A32F0D7 | 27 | n3TeleportIIR_t | GameServer |
| 0x0774C8C1 | 27 | QuestGoalIIR_c | GameServer |
| 0x000438A5 | 27 | [?] | GameServer |
| 0x000438A2 | 27 | [?] | GameServer |
| 0x00031665 | 27 | [?] | GameServer |
| 0x000316AF | 27 | [?] | GameServer |
| 0x00031818 | 26 | [?] | GameServer |
| 0x0003181B | 26 | [?] | GameServer |
| 0x00031816 | 26 | [?] | GameServer |
| 0x00031819 | 26 | [?] | GameServer |
| 0x00043906 | 26 | [?] | GameServer |
| 0x00042188 | 25 | [?] | GameServer |
| 0x00042187 | 25 | [?] | GameServer |
| 0x00042392 | 25 | [?] | GameServer |
| 0x000439AD | 25 | [?] | GameServer |
| 0x00017E2B | 24 | [?] | GameServer |
| 0x000438F0 | 24 | [?] | GameServer |
| 0x000818A6 | 24 | [?] | GameServer |
| 0x00042174 | 23 | [?] | GameServer |
| 0x00017E17 | 22 | [?] | GameServer |
| 0x000195AB | 22 | [?] | GameServer |
| 0x000421A5 | 22 | [?] | GameServer |
| 0x000423CC | 22 | [?] | GameServer |
| 0x000438A4 | 22 | [?] | GameServer |
| 0x00017E03 | 21 | [?] | GameServer |
| 0x00017E08 | 21 | [?] | GameServer |
| 0x00042399 | 21 | [?] | GameServer |
| 0x00017E0F | 20 | [?] | GameServer |
| 0x00017E14 | 20 | [?] | GameServer |
| 0x00042419 | 20 | [?] | GameServer |
| 0x000423FA | 20 | [?] | GameServer |
| 0x0004393F | 20 | [?] | GameServer |
| 0x00042189 | 19 | [?] | GameServer |
| 0x000423FE | 19 | [?] | GameServer |
| 0x0004388F | 19 | [?] | GameServer |
| 0x000438ED | 19 | [?] | GameServer |
| 0x00043842 | 19 | [?] | GameServer |
| 0x00017ED0 | 18 | [?] | GameServer |
| 0x000423A9 | 18 | [?] | GameServer |
| 0x000423A8 | 18 | [?] | GameServer |
| 0x00043852 | 18 | [?] | GameServer |
| 0x0004070F | 18 | [?] | GameServer |
| 0x0004DEE9 | 18 | [?] | GameServer |
| 0x00030D5A | 18 | [?] | GameServer |
| 0x00026522 | 18 | [?] | GameServer |
| 0x79F8C7A1 | 17 | QuestJournalIIR_c | GameServer |
| 0x00017E36 | 17 | [?] | GameServer |
| 0x000421AA | 17 | [?] | GameServer |
| 0x54C02089 | 17 | CombatReactiveIIR_c | GameServer |
| 0x00031815 | 17 | [?] | GameServer |
| 0x0004384C | 17 | [?] | GameServer |
| 0x0004DEC5 | 17 | [?] | GameServer |
| 0x0004DEC7 | 17 | [?] | GameServer |
| 0x2A8A1B49 | 16 | MoveInventoryItemIIR_t | GameServer |
| 0x00017E41 | 16 | [?] | GameServer |
| 0x000195A5 | 16 | [?] | GameServer |
| 0x000423C9 | 16 | [?] | GameServer |
| 0x0004235C | 16 | [?] | GameServer |
| 0x00043848 | 16 | [?] | GameServer |
| 0x000438D6 | 16 | [?] | GameServer |
| 0x00043930 | 16 | [?] | GameServer |
| 0x00040722 | 16 | [?] | GameServer |
| 0x10D27BC0 | 15 | IntendedTeleportIIR_c | GameServer |
| 0x00017E2D | 15 | [?] | GameServer |
| 0x00017E38 | 15 | [?] | GameServer |
| 0x00017E18 | 15 | [?] | GameServer |
| 0x00017E42 | 15 | [?] | GameServer |
| 0x0004237F | 15 | [?] | GameServer |
| 0x0004239C | 15 | [?] | GameServer |
| 0x0004237C | 15 | [?] | GameServer |
| 0x0004237D | 15 | [?] | GameServer |
| 0x0004382F | 15 | [?] | GameServer |
| 0x0004392F | 15 | [?] | GameServer |
| 0x000438F3 | 15 | [?] | GameServer |
| 0x00043860 | 15 | [?] | GameServer |
| 0x000438FA | 15 | [?] | GameServer |
| 0x00030D5B | 15 | [?] | GameServer |
| 0x00017F80 | 14 | [?] | GameServer |
| 0x00017E3F | 14 | [?] | GameServer |
| 0x00017E39 | 14 | [?] | GameServer |
| 0x00017E3A | 14 | [?] | GameServer |
| 0x00017E09 | 14 | [?] | GameServer |
| 0x0004217C | 14 | [?] | GameServer |
| 0x00043890 | 14 | [?] | GameServer |
| 0x000438AB | 14 | [?] | GameServer |
| 0x000438A7 | 14 | [?] | GameServer |
| 0x000316AA | 14 | [?] | GameServer |
| 0x000316DC | 14 | [?] | GameServer |
| 0x0004071A | 14 | [?] | GameServer |
| 0x00017E35 | 13 | [?] | GameServer |
| 0x000195A8 | 13 | [?] | GameServer |
| 0x000423B5 | 13 | [?] | GameServer |
| 0x000438AD | 13 | [?] | GameServer |
| 0x0004387B | 13 | [?] | GameServer |
| 0x00043929 | 13 | [?] | GameServer |
| 0x0004DEC4 | 13 | [?] | GameServer |
| 0x00039235 | 13 | [?] | GameServer |
| 0x00030D59 | 13 | [?] | GameServer |
| 0x00030D58 | 13 | [?] | GameServer |
| 0x00030D5C | 13 | [?] | GameServer |
| 0x000195A6 | 12 | [?] | GameServer |
| 0x000423B4 | 12 | [?] | GameServer |
| 0x00042382 | 12 | [?] | GameServer |
| 0x000438AE | 12 | [?] | GameServer |
| 0x000438AA | 12 | [?] | GameServer |
| 0x00043927 | 12 | [?] | GameServer |
| 0x00039234 | 12 | [?] | GameServer |
| 0x00039236 | 12 | [?] | GameServer |
| 0x000264D8 | 12 | [?] | GameServer |
| 0x00017E34 | 11 | [?] | GameServer |
| 0x00017E3B | 11 | [?] | GameServer |
| 0x000195A9 | 11 | [?] | GameServer |
| 0x0004390F | 11 | [?] | GameServer |
| 0x00043895 | 11 | [?] | GameServer |
| 0x00043857 | 11 | [?] | GameServer |
| 0x000439B5 | 11 | [?] | GameServer |
| 0x00043963 | 11 | [?] | GameServer |
| 0x00043843 | 11 | [?] | GameServer |
| 0x0009FF93 | 11 | [?] | GameServer |
| 0x0009FF94 | 11 | [?] | GameServer |
| 0x0009FF92 | 11 | [?] | GameServer |
| 0x0009FF95 | 11 | [?] | GameServer |
| 0x0009FF96 | 11 | [?] | GameServer |
| 0x00039233 | 11 | [?] | GameServer |
| 0x00039232 | 11 | [?] | GameServer |
| 0x00026526 | 11 | [?] | GameServer |
| 0x00026525 | 11 | [?] | GameServer |
| 0x00017E29 | 10 | [?] | GameServer |
| 0x00017E37 | 10 | [?] | GameServer |
| 0x00017E3C | 10 | [?] | GameServer |
| 0x00017E0E | 10 | [?] | GameServer |
| 0x0003FC08 | 10 | [?] | GameServer |
| 0x000421B0 | 10 | [?] | GameServer |
| 0x0004388C | 10 | [?] | GameServer |
| 0x0004388B | 10 | [?] | GameServer |
| 0x00043892 | 10 | [?] | GameServer |
| 0x00043891 | 10 | [?] | GameServer |
| 0x00043893 | 10 | [?] | GameServer |
| 0x00043846 | 10 | [?] | GameServer |
| 0x000438B8 | 10 | [?] | GameServer |
| 0x00043851 | 10 | [?] | GameServer |
| 0x000438FB | 10 | [?] | GameServer |
| 0x00031784 | 10 | [?] | GameServer |
| 0x000316B1 | 10 | [?] | GameServer |
| 0x00040650 | 10 | [?] | GameServer |
| 0x00040703 | 10 | [?] | GameServer |
| 0x0004DE8A | 10 | [?] | GameServer |
| 0x0004DEEA | 10 | [?] | GameServer |
| 0x0002324D | 10 | [?] | GameServer |
| 0x0002324E | 10 | [?] | GameServer |
| 0x00026521 | 10 | [?] | GameServer |
| 0x00026529 | 10 | [?] | GameServer |
| 0x00017E40 | 9 | bu3 | GameServer |
| 0x0003FBE7 | 9 | [?] | GameServer |
| 0x000195A7 | 9 | [?] | GameServer |
| 0x0004217F | 9 | [?] | GameServer |
| 0x0004239B | 9 | [?] | GameServer |
| 0x000423CF | 9 | [?] | GameServer |
| 0x000423D0 | 9 | [?] | GameServer |
| 0x00043940 | 9 | [?] | GameServer |
| 0x0004390D | 9 | [?] | GameServer |
| 0x000316A9 | 9 | [?] | GameServer |
| 0x000316A8 | 9 | [?] | GameServer |
| 0x000316AB | 9 | [?] | GameServer |
| 0x000316AE | 9 | [?] | GameServer |
| 0x00040620 | 9 | [?] | GameServer |
| 0x0004064F | 9 | [?] | GameServer |
| 0x0004067C | 9 | [?] | GameServer |
| 0x00040686 | 9 | [?] | GameServer |
| 0x00040689 | 9 | [?] | GameServer |
| 0x000406FD | 9 | [?] | GameServer |
| 0x000535A0 | 9 | [?] | GameServer |
| 0x0002324C | 9 | [?] | GameServer |
| 0x0002324B | 9 | [?] | GameServer |
| 0x0002324F | 9 | [?] | GameServer |
| 0x000264D6 | 9 | [?] | GameServer |
| 0x000264D7 | 9 | [?] | GameServer |
| 0x000264D5 | 9 | [?] | GameServer |
| 0x000264D9 | 9 | [?] | GameServer |
| 0x00026523 | 9 | [?] | GameServer |
| 0x06EC1255 | 8 | CoreClaimIIR | GameServer |
| 0x0003FC1E | 8 | [?] | GameServer |
| 0x000195C6 | 8 | [?] | GameServer |
| 0x000421AC | 8 | [?] | GameServer |
| 0x000423DA | 8 | [?] | GameServer |
| 0x000423CE | 8 | [?] | GameServer |
| 0x000423D1 | 8 | [?] | GameServer |
| 0x000423B3 | 8 | [?] | GameServer |
| 0x000439B0 | 8 | [?] | GameServer |
| 0x00043901 | 8 | [?] | GameServer |
| 0x000439B3 | 8 | [?] | GameServer |
| 0x0004388E | 8 | [?] | GameServer |
| 0x0004388D | 8 | [?] | GameServer |
| 0x00031663 | 8 | [?] | GameServer |
| 0x00031664 | 8 | [?] | GameServer |
| 0x00031644 | 8 | [?] | GameServer |
| 0x00031722 | 8 | [?] | GameServer |
| 0x00040655 | 8 | [?] | GameServer |
| 0x0004067B | 8 | [?] | GameServer |
| 0x00040665 | 8 | [?] | GameServer |
| 0x00040688 | 8 | [?] | GameServer |
| 0x00040687 | 8 | [?] | GameServer |
| 0x000818A3 | 8 | [?] | GameServer |
| 0x000818A4 | 8 | [?] | GameServer |
| 0x000818A2 | 8 | [?] | GameServer |
| 0x000818A5 | 8 | [?] | GameServer |
| 0x00026550 | 8 | [?] | GameServer |
| 0x00017E31 | 7 | [?] | GameServer |
| 0x6C300937 | 7 | TemplateActionIIR_t | GameServer |
| 0x0003FC22 | 7 | [?] | GameServer |
| 0x0003FC1B | 7 | [?] | GameServer |
| 0x0003FC1C | 7 | [?] | GameServer |
| 0x0003FC1F | 7 | [?] | GameServer |
| 0x0003FC21 | 7 | [?] | GameServer |
| 0x0003FC1D | 7 | [?] | GameServer |
| 0x00042365 | 7 | [?] | GameServer |
| 0x00042336 | 7 | [?] | GameServer |
| 0x00042402 | 7 | [?] | GameServer |
| 0x000423FB | 7 | [?] | GameServer |
| 0x000423FC | 7 | [?] | GameServer |
| 0x00042403 | 7 | [?] | GameServer |
| 0x0004239A | 7 | [?] | GameServer |
| 0x000423CD | 7 | [?] | GameServer |
| 0x00043928 | 7 | [?] | GameServer |
| 0x000438BE | 7 | [?] | GameServer |
| 0x00031721 | 7 | [?] | GameServer |
| 0x00031666 | 7 | [?] | GameServer |
| 0x0003167F | 7 | [?] | GameServer |
| 0x00031706 | 7 | [?] | GameServer |
| 0x000406FC | 7 | [?] | GameServer |
| 0x00040683 | 7 | [?] | GameServer |
| 0x000406F9 | 7 | [?] | GameServer |
| 0x00040608 | 7 | [?] | GameServer |
| 0x00022722 | 7 | [?] | GameServer |
| 0x00018025 | 6 | [?] | GameServer |
| 0x00017E0A | 6 | [?] | GameServer |
| 0x00017E1A | 6 | [?] | GameServer |
| 0x0D80E7C9 | 6 | TombStoneFullUpdateIIR_c | GameServer |
| 0x0003FC20 | 6 | [?] | GameServer |
| 0x0001958F | 6 | [?] | GameServer |
| 0x000195AC | 6 | [?] | GameServer |
| 0x1F4CB0F7 | 6 | PetIIR_c | GameServer |
| 0x000423CA | 6 | [?] | GameServer |
| 0x0004234E | 6 | [?] | GameServer |
| 0x000423B6 | 6 | [?] | GameServer |
| 0x00043847 | 6 | [?] | GameServer |
| 0x00043878 | 6 | [?] | GameServer |
| 0x0004387A | 6 | [?] | GameServer |
| 0x0003164D | 6 | [?] | GameServer |
| 0x00031704 | 6 | [?] | GameServer |
| 0x000316F6 | 6 | [?] | GameServer |
| 0x00031738 | 6 | [?] | GameServer |
| 0x00031653 | 6 | [?] | GameServer |
| 0x00031652 | 6 | [?] | GameServer |
| 0x0003168D | 6 | [?] | GameServer |
| 0x0003164C | 6 | [?] | GameServer |
| 0x000316AD | 6 | [?] | GameServer |
| 0x000316B0 | 6 | [?] | GameServer |
| 0x0003164B | 6 | [?] | GameServer |
| 0x000316B3 | 6 | [?] | GameServer |
| 0x00031696 | 6 | [?] | GameServer |
| 0x00031695 | 6 | [?] | GameServer |
| 0x0003164A | 6 | [?] | GameServer |
| 0x00031698 | 6 | [?] | GameServer |
| 0x000317B6 | 6 | [?] | GameServer |
| 0x00031697 | 6 | [?] | GameServer |
| 0x00031691 | 6 | [?] | GameServer |
| 0x00031693 | 6 | [?] | GameServer |
| 0x00040742 | 6 | [?] | GameServer |
| 0x00040629 | 6 | [?] | GameServer |
| 0x000406FB | 6 | [?] | GameServer |
| 0x0004067A | 6 | [?] | GameServer |
| 0x00040682 | 6 | [?] | GameServer |
| 0x00040684 | 6 | [?] | GameServer |
| 0x00040702 | 6 | [?] | GameServer |
| 0x00040679 | 6 | [?] | GameServer |
| 0x0004067D | 6 | [?] | GameServer |
| 0x00040633 | 6 | [?] | GameServer |
| 0x000406FE | 6 | [?] | GameServer |
| 0x000406AD | 6 | [?] | GameServer |
| 0x0004DEC6 | 6 | [?] | GameServer |
| 0xBA1FD222 | 5 | FeedbackIIR_t | GameServer |
| 0x00017E16 | 5 | [?] | GameServer |
| 0x0003FBEB | 5 | [?] | GameServer |
| 0x000421A9 | 5 | [?] | GameServer |
| 0x000423CB | 5 | [?] | GameServer |
| 0x0004390E | 5 | [?] | GameServer |
| 0x000438A6 | 5 | [?] | GameServer |
| 0x000316FB | 5 | [?] | GameServer |
| 0x000316F2 | 5 | [?] | GameServer |
| 0x000317B3 | 5 | [?] | GameServer |
| 0x000317B4 | 5 | [?] | GameServer |
| 0x00031654 | 5 | [?] | GameServer |
| 0x000317B2 | 5 | [?] | GameServer |
| 0x000316FC | 5 | [?] | GameServer |
| 0x000316F3 | 5 | [?] | GameServer |
| 0x000316F1 | 5 | [?] | GameServer |
| 0x000316F9 | 5 | [?] | GameServer |
| 0x00031726 | 5 | [?] | GameServer |
| 0x0003171E | 5 | [?] | GameServer |
| 0x000316C3 | 5 | [?] | GameServer |
| 0x000316C0 | 5 | [?] | GameServer |
| 0x000316C1 | 5 | [?] | GameServer |
| 0x000316BF | 5 | [?] | GameServer |
| 0x00031657 | 5 | [?] | GameServer |
| 0x00031658 | 5 | [?] | GameServer |
| 0x0003164F | 5 | [?] | GameServer |
| 0x0003175A | 5 | [?] | GameServer |
| 0x00031680 | 5 | [?] | GameServer |
| 0x0003167E | 5 | [?] | GameServer |
| 0x000316B2 | 5 | [?] | GameServer |
| 0x0003165D | 5 | [?] | GameServer |
| 0x0003178B | 5 | [?] | GameServer |
| 0x00031699 | 5 | [?] | GameServer |
| 0x000317B9 | 5 | [?] | GameServer |
| 0x00031692 | 5 | [?] | GameServer |
| 0x000316BD | 5 | [?] | GameServer |
| 0x0004062F | 5 | [?] | GameServer |
| 0x000406A4 | 5 | [?] | GameServer |
| 0x000406A3 | 5 | [?] | GameServer |
| 0x000406A0 | 5 | [?] | GameServer |
| 0x000406A2 | 5 | [?] | GameServer |
| 0x000406A1 | 5 | [?] | GameServer |
| 0x0004074D | 5 | [?] | GameServer |
| 0x00040743 | 5 | [?] | GameServer |
| 0x00040681 | 5 | [?] | GameServer |
| 0x00040764 | 5 | [?] | GameServer |
| 0x000406FF | 5 | [?] | GameServer |
| 0x000406FA | 5 | [?] | GameServer |
| 0x00040708 | 5 | [?] | GameServer |
| 0x0004070B | 5 | [?] | GameServer |
| 0x00040709 | 5 | [?] | GameServer |
| 0x000406D5 | 5 | [?] | GameServer |
| 0x000406CE | 5 | [?] | GameServer |
| 0x000406CF | 5 | [?] | GameServer |
| 0x000406D6 | 5 | [?] | GameServer |
| 0x00026567 | 5 | [?] | GameServer |
| 0xADCE0CDA | 4 | SetNameIIR_t | GameServer |
| 0x00017E19 | 4 | [?] | GameServer |
| 0x00043850 | 4 | [?] | GameServer |
| 0x00031720 | 4 | [?] | GameServer |
| 0x00031648 | 4 | [?] | GameServer |
| 0x0003171F | 4 | [?] | GameServer |
| 0x00031785 | 4 | [?] | GameServer |
| 0x000316AC | 4 | [?] | GameServer |
| 0x00031656 | 4 | [?] | GameServer |
| 0x000317BB | 4 | [?] | GameServer |
| 0x00031737 | 4 | [?] | GameServer |
| 0x00031694 | 4 | [?] | GameServer |
| 0x00040707 | 4 | [?] | GameServer |
| 0x0004071E | 4 | [?] | GameServer |
| 0x0004070A | 4 | [?] | GameServer |
| 0x00040719 | 4 | [?] | GameServer |
| 0x0004070D | 4 | [?] | GameServer |
| 0x0004070C | 4 | [?] | GameServer |
| 0x0004075B | 4 | [?] | GameServer |
| 0x0004071D | 4 | [?] | GameServer |
| 0x0004071C | 4 | [?] | GameServer |
| 0x000406DE | 4 | [?] | GameServer |
| 0x000406E9 | 4 | [?] | GameServer |
| 0x0004075A | 4 | [?] | GameServer |
| 0x0004070E | 4 | [?] | GameServer |
| 0x0004062C | 4 | [?] | GameServer |
| 0x00040638 | 4 | [?] | GameServer |
| 0x000406D1 | 4 | [?] | GameServer |
| 0x2550C99A | 3 | GuildUpdateIIR_c | GameServer |
| 0x0003FBEA | 3 | [?] | GameServer |
| 0xC7954504 | 3 | CrimeIIR_c | GameServer |
| 0x0BA1C1CB | 3 | BankIIR_t | GameServer |
| 0x42E62E00 | 3 | [?] | GameServer |
| 0x00040710 | 3 | [?] | GameServer |
| 0xB066AFC2 | 2 | SimpleCharKilledByUpdateIIR_t | GameServer |
| 0x4B2A47BF | 2 | KnubotStartTradeIIR_c | GameServer |
| 0x7446F3FA | 2 | KnubotFinishTradeIIR_c | GameServer |
| 0x1261A629 | 2 | [?] | CsPlayerAgent |
| 0x268ADDA9 | 1 | [?] | CsPlayerAgent |
| 0x6BA10936 | 1 | SpellBookIIR_t | GameServer |
| 0x3384CC8F | 1 | [?] | CsPlayerAgent |
| 0x780FA30F | 1 | [?] | CsPlayerAgent |
| 0x5A4C3297 | 1 | [?] | CsPlayerAgent |
| 0xE0B51102 | 1 | ChatTextIIR_t | GameServer |
| 0x47A1B099 | 1 | [?] | CsPlayerAgent |
| 0x7BE4B3E9 | 1 | [?] | CsPlayerAgent |
| 0x5CCDEA29 | 1 | [?] | CsPlayerAgent |
| 0x786BBA93 | 1 | [?] | CsPlayerAgent |
| 0x1F550B43 | 1 | [?] | CsPlayerAgent |
| 0x0857313D | 1 | [?] | CsPlayerAgent |
| 0x3391A2B5 | 1 | [?] | CsPlayerAgent |
| 0x037D6B6B | 1 | [?] | CsPlayerAgent |
| 0x36CF73BD | 1 | [?] | CsPlayerAgent |
| 0x4002CDE3 | 1 | [?] | CsPlayerAgent |
| 0x319E7477 | 1 | [?] | CsPlayerAgent |
| 0x7120B9A7 | 1 | [?] | CsPlayerAgent |
| 0x35DA4A91 | 1 | [?] | CsPlayerAgent |
| 0x575BA201 | 1 | [?] | CsPlayerAgent |
| 0x351A6F73 | 1 | [?] | CsPlayerAgent |
| 0x6505EEC3 | 1 | [?] | CsPlayerAgent |

## 16-bit RpcHeader opcodes per server (method indices - Stage 3b, NOT hashes)

**RESOLVED (2026-06-12, guild work):** the opcode is `0x2000 + the method's position` in the recovered
client→server RPC table (`docs/re/aoc-client-rpc-methods.json`) — registration order = table order. Anchored on
CSPlayerAgent `0x201C` = `AcceptGuildInvitation` (fired exactly once on the guild-accept click) and bulk-verified
by tabulating `(RpcHeader caller class, opcode)` across all capture JSONs (independent semantic hits:
`SplitInventoryItem`, `ToggleVanityArmorFilter`, `AddShortcut`, `CloseLootBag`, `GameCharAgent.Ping`, the
tradepost methods). On the multiplexed GameServer connection the dispatch key is `(class id, opcode)`; the
small class tables top out early (TradepostAgent `0x2013`, project `0x200C`, GameAgent `0x200A`), so any value
`>= 0x2014` is unambiguously GameCharAgent. Full per-class catalogs: `Faolan.Core/Network/Packets/Opcodes.cs`.

**s2c tables RESOLVED (2026-06-13, Ghidra):** the server→client "drift" is now fully explained and the true
tables recovered from `AgeOfConan.exe`. The base is `0x2000` for every interface **except PlayerAgent/
PlayerInterface** (login; observed c2s wire `0x2083..0x20F5`, base ≠ 0x2000 — those opcodes stay
behavior-derived). For all others, `wire = 0x2000 + registration index`. c2s registration order is *identical*
to `aoc-client-rpc-methods.json` (proven: the proxy constructors register methods in that exact order). The s2c
drift was caused by the live build inserting extra dispatch methods — mostly **no-arg** methods that emit no
`stream bad` string, so the string-extracted catalog missed them and every later method shifted. Recovered by
decompiling each `<Class>InterfaceDispatch_t::DispatchMethod` (a `switch(index)`; case N → handler N, named by
the handler's own `Dispatch_t::<Method>` string). True s2c sizes vs catalog: UniverseInterface 6 (=), GameInterface
16 (+3), GameCharInterfaceProject 16 (+1), TradepostInterface 8 (+1), GameCharInterface **181 (+24)**,
CSPlayerInterface **211 (+33)**. Full tables: **`docs/re/aoc-rpc-method-opcodes.md`**; recovery scripts:
`Captures/ghidra/`. In-client-verified anchors all match: s2c GameCharInterface DispatchUpdateBindLocation=0x2002,
**DispatchUpdateCash=0x2005**, DispatchShopData=0x2033, DispatchItemSoldToShop=0x2035, DispatchAddSpecialAbility
=0x2052, DispatchPong=0x2082. NOTE corrections to old content-guesses: GameServer s2c `0x2009`=DispatchChangeItem
StackSize (not "Pong" — real per-char Pong is `0x2082`), `0x2008`=DispatchSetQuestMarker, and `0x2020`=
**DispatchAddItem** exists. PlayerInterface s2c is still open (its dispatcher isn't a flat switch). Methodology:
README "Client reverse-engineering"; verification corpus: `Captures/scripts/decode_guild.py` + `Captures/ghidra/`.

The raw per-server counts below are kept as the original observation record:


**CsPlayerAgent**: 0x201E(76) 0x2077(57) 0x20A5(56) 0x2030(48) 0x2000(47) 0x2040(34) 0x2041(34) 0x2038(33) 0x20D0(23) 0x20C5(23) 0x202F(18) 0x2043(18) 0x203A(10) 0x2042(8) 0x203F(4) 0x2039(3) 0x202E(1) 0x203D(1) 0x202A(1) 0x201C(1) 0x20AB(1)

**GameServer**: 0x2000(82527) 0x2020(5341) 0x2008(5076) 0x2005(2354) 0x2085(2324) 0x200C(2129) 0x2082(2115) 0x2009(2107) 0x205A(1029) 0x201C(914) 0x200B(412) 0x2052(292) 0x200D(184) 0x200E(125) 0x2003(114) 0x2002(112) 0x205F(108) 0x2015(83) 0x204E(59) 0x206E(58) 0x2044(46) 0x200A(41) 0x205E(37) 0x2001(36) 0x2024(36) 0x207E(35) 0x2037(23) 0x2045(23) 0x2043(23) 0x2038(23) 0x206D(22) 0x2076(20) 0x201B(15) 0x2012(15) 0x2033(13) 0x205D(12) 0x2032(12) 0x206A(12) 0x201F(9) 0x2034(8) 0x2035(8) 0x2016(6) 0x2004(5) 0x2011(4) 0x206F(3) 0x2071(2) 0x201D(2) 0x2010(2) 0x2007(2) 0x20B3(1) 0x200F(1) 0x201E(1) 0x2006(1) 0x203B(1) 0x207A(1)

**PlayerAgent**: 0x1A07(92) 0x20E5(48) 0x20D6(36) 0x20CC(34) 0x2091(26) 0x20EC(26) 0x20EF(25) 0x20A6(25) 0x209C(25) 0x2083(23) 0x20C1(23) 0x20A5(23) 0x208E(23) 0x208C(23) 0x20B9(23) 0x20B6(16) 0x20DE(7) 0x209D(2) 0x20F5(2) 0x2084(1)

**UniverseAgent**: 0x2000(46) 0x2001(46) 0x2005(23)
