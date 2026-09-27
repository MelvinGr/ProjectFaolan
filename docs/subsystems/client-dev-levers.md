# The retail client's developer / GM levers

What the client actually exposes, and — more usefully — what it does not.

## `gm_level` on the wire is not the switch

The login `PlayerStats.gm_level` (protobuf member 5) is byte-exact on the wire in the player-spawn packet, and the
snapshot GM Level stat `0x0D7` can be forced too. The retail client shows no GM behaviour from either. `/gm`
is refused by the chat dispatcher (`FUN_00657D40`) through a branch selected by `FUN_00A7EBE0`, and that function
reads one thing: the config key **`IsDeveloperUniverse`**. (`/aa` by contrast reaches the exe's *"No chat command
or script named"* path, so `gm` really is a known verb — it is refused, not missing.)

## The bool config keys are enumerable, and there are exactly 15

The getter is `FUN_00A7E1F0(default)`, and a call site builds its key immediately before it with
`FUN_0040EC10(<literal>, <len>)`. Walking every caller back to that literal enumerates the set exhaustively —
**21 call sites, 15 distinct keys, none unrecovered**. The full list with its call sites is
[client-config-keys.tsv](../re/client-config-keys.tsv).

They are settable with **no patch**: the loader `FUN_00A7CF90` merges `SelfConfig.xml`, `UniversalConfig.xml` and
`LocalConfig.xml` (only the last ships in a retail install), self-context `Universe/Client/`, values `True`/`False`.

Three of the fifteen are worth knowing about beyond `IsDeveloperUniverse`, because they name systems this server
does not implement: **`EnableAlternateAdvancement`**, **`EnableOfflineAaTraining`** and **`EnableOfflineLeveling`**
(all read by one function, `FUN_00412990`).

## The config-key-shaped debug strings are not config keys

Every name a config-key-shaped string scan turns up has been xref'd. All eight exist in the binary and all
eight are referenced — by three *different* mechanisms, none of them the config getter:

| string | what it actually is | evidence |
|---|---|---|
| `DebugPvPMatchForceStartMatch` `…ForceEnterAll` `…ForcePlayerIntoCurrentMatch` | **c2s RPC methods** | referenced from `FUN_01358BA0`, a table of RPC method names (`AcceptGuildInvitation`, `AcceptSummon`, `AddPvpPoints`, `Authenticate`, …) |
| `DebugToggleFlyingMode` | an **input action** (keybindable) | referenced from `FUN_00458DF0`, a table of input actions (`Action_Attack`, `Action_Sneak`, `Assist`, `AutoLoot`, `CampGameToLogin`, …) |
| `GMCommandLogging` | a **log tag** | referenced from `FUN_0064CFB0`, which formats `"Command["` strings |
| `FreeFeatRespecs`, `GmExcludeFromDataMining` | neither — both referenced from one table in `FUN_01DB2740` | not the config getter, and absent from the 15 |
| `DebugRenderScripts`, `EnablePostProcessDebugging`, `DebugLinesViewConfig` | render-side debug | referenced from render code (`FUN_00FB7FF0`, `FUN_015E59D0`) |

**The PvP forcers are the useful find, and they are in our catalog**: `docs/re/aoc-client-rpc-methods.json`
holds **12** `Debug*` methods, nine of them the minigame/matchmaking dev controls —
`DebugPvPMatchForceStartMatch`, `DebugPvPMatchForceEnterAll`, `DebugPvPMatchForcePlayerIntoCurrentMatch`,
`DebugPvPMatchMakingDoMatchMaking`, `DebugPvPMatchMakingRestart`, `DebugPvPMatchMakingSetNewTimeLimit`,
`DebugPvPMatchMakingShowDebugInfo`, `DebugPvPMatchMakingStopAllMatches`, `DebugPvPMatchMakingStopCurrentMatch`.
They are server methods the client can call, so they are a minigames lead (Gitea #146), not a client config one.

`DebugToggleFlyingMode` being an input action rather than a lever is consistent with what fly mode is: the `/luke`
command, not a config flag.

## What needs the client

Whether `IsDeveloperUniverse=True` actually unlocks `/gm`, and what verb surface appears if it does, can only be
answered by launching the retail client with the key set. Open work tracked in Gitea #448.
