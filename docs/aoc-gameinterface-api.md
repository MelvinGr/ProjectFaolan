# AoC client GUI API catalog (`GameInterface.*`) — Rosetta stone

Stage 5 #7 of `docs/opcode-re-plan.md`. The client's UI is Lua, bound to C++ GUI modules whose names are RTTI/
registration strings in `AgeOfConan.exe`. This catalogs those modules so a server packet can be tied to "what UI
the client drives with it" — a verification aid when building/validating packets.

**Scope note (what's here vs. not):** the exe gives the **module/class catalog** below (the C++ side). The full
method-level "server event → UI action" map lives in the **RDB UI Lua scripts** (`com.GameInterface.*` script
files), not the binary — extracting that is the deeper, RDB-side layer (see [[rdb_extraction]]). 42 namespaces /
50+ classes extracted 2026-06-09 via `strings AgeOfConan.exe | grep -oE 'GameInterface\.[A-Za-z0-9_.]+'`.

## Gameplay modules (the ones our server packets feed)

| `GameInterface.*` module / class | Driven by (our server side) |
|---|---|
| `Game.Dynel`, `Game.DynelBase` | entity spawns (`FA015807` / `SimpleCharFullUpdateIIR`) — the world objects/NPCs |
| `Game.CharacterBase` | the player/character snapshot (`0x737A6DF9`) |
| `Game.TargetingInterface` | target selection / target frame |
| `Game.TeamInterface`, `Game.GroupElement` | team/group (CSPlayerAgent team packets) |
| `Game.BuffData` | buffs/abilities (`MagicBuffIIR` / ApplySpell `0x6B87E316`, ability grant `0x2052`) |
| `Game.Camera` | cutscene camera (`DispatchSetDialogueCamera`, the create-world scry — TODO #7) |
| `ProgressBars.ProgressBarsInterface` | vitals/cast bars (`StatUpdateIIR` `0x96B8DC59`, see [[protocol_stats_and_combat]]) |
| `Factions.Faction(Interface)` | faction standing |
| `Guild.Guild*` (Member/Rank/Renown/Permission/Feat) | guild data |
| `Inventory`, `InventoryItem` | inventory snapshot (`FullPlayerCharacterIIR`, see [[inventory_db_items_work]]) |
| `FeatWindows.AAFeatTree`, `MultiSpecsFeatsInterface` | feats / alternate-advancement (the gametweaks `AAPointsPerLevel`/`AlternateAdvancement_*`) |
| `Fifo.FifoInterface` | the client FIFO / chat scroll (`DispatchShowClientFIFO(remoteFormat)`) |
| `ScryWidgets` | scry/cutscene widgets (TODO #7) |
| `DialogIF` | NPC conversation (see [[protocol_conversation_routing]]) |
| `ItemShop`, `ItemShopBrowser`, `ItemShopGameItem` | the cash/item shop |
| `GroupFinder*` (Activity/Category/Prerequisite) | the group/raid finder (the `category=100` `GroupFinder*` remoteformats) |

## Front-end / non-gameplay modules

`AlternateLoginBrowser`, `DailyLoginBrowser`, `RegistrationBrowser`, `CharacterSelection`, `Credits`,
`PetitionBrowser`, `PvPMinigame`, `KillChain`, `Lore`/`LoreBase`, `SocialNetwork`, `Tooltip`, `MousePointer`,
`EscapeStack`/`EscapeStackNodeCPP` (the Esc-key UI stack), `PlayfieldAccessPopup`/`PlayfieldAccessWindow`,
`ClientServerPerfTracker`, `MathLib.Vector3`, `Utils`/`UtilsBase`, `LogBase`, `DistributedValueBase`,
`GUIModuleIF`/`GUIModuleIFBase` (`OnModuleStatusChanged` = module load/unload).

## Use

Cross-check a packet against the module it should drive: e.g. if a target frame doesn't update, the relevant UI
is `Game.TargetingInterface`; vitals bars are `ProgressBars.ProgressBarsInterface`. The method-level event names
(what each module listens for) require the RDB Lua scripts — pull those when a specific UI module needs tracing.
