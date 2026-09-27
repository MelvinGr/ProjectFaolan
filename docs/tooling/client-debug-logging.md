# Client debug logging

The client's verbose debug channels work on this build. The knob is the logger's **per-channel level threshold**,
and the client takes it from a plain config FILE. Enable it with `Faolan.ClientLauncher --debug-channels`, which
writes `dbDebug.conf` (`ClientDebugChannels`) — no exe change, no process memory.

Dropping the default threshold produces **117,044 log lines in seconds**, including the
`RECEIVED IIR: class MoveTaskIIR_c` trace.

## `dbDebug.conf` — the client's logger config

The client reads `dbDebug.conf` from its install dir during startup (the path is built at `0x004130C7`, the parser
is `FUN_004AB950`), and it ships with one:

```
DebugLevel=3      CallTrace=false     PrintFlush=true       PrefixLevel=3
TypeMode=select   AssertMode=abort    PrintType=true        PrefixColors=false
                  PrintModule=false   DateStamp=true        Log4cxxFormat=true
                                      LogFile=AgeOfConan.log
```

The parser skips blank and `#` lines, then full-line-matches each line against a fixed table of SETTINGS keys at
`0x01F847C4` — `DebugLevel=%7s`, `TypeMode=inclusive|exclusive|select`, `PrefixLevel=%d`, `LogFile=%s`,
`PrintFlush=`, `DateStamp=`, `PrefixColors=`, `PrintType=`, `Log4cxxFormat=true`, `ShowImportant=false`,
`UseCallback=false`, `Win32Debug=` — and **every line matching none of them is registered as a CHANNEL**:

```c
name  = tok(line);                    // delimiters " =\n\r"
value = tok();
FUN_004B2A40(name, value != NULL ? atoi(value) : defaultLevel);   // the add-channel routine
```

That is why the shipped file yields exactly three live channels: `CallTrace`, `AssertMode` and `PrintModule` are
not settings keys, so they are added at `atoi("false") == atoi("abort") == 0`. **So a `GameCode/Stats=0` line opens
that channel** — the whole mechanism is a text file.

`TypeMode=select` is what selects per-channel thresholds (it is the call to the mode-2 setter `FUN_004B2C90`);
`inclusive`/`exclusive` are a name whitelist/blacklist with no thresholds, where per-channel entries do nothing.
It ships as `select`, and `ClientDebugChannels` enforces both it and `PrintFlush=true`.

**Where those lines land at runtime** (client is PE32 with no ASLR, so Ghidra addresses are runtime addresses):

| address | meaning | ships as |
|---|---|---|
| `0x02984AC0` | default level threshold (`DebugLevel`) | **3** |
| `0x02984ADC` | output mode (`TypeMode`) | **2** = per-channel thresholds |
| `0x02984AE0` | 0x80 channel names, 48 B each, NUL-padded | `CallTrace`, `AssertMode`, `PrintModule` |
| `0x029862E0` | parallel `i32` threshold per name | 0, 0, 0 |

A line is emitted when `threshold <= level`. The debug sites log at level **0–2** and ERROR is 4, so the shipped
default of 3 is exactly what hides them — and a channel absent from the table falls back to that default. A name at
threshold 0 opens **that channel and nothing else**.

Do not simply drop `DebugLevel` to 0: `GameCode.Movement` alone logs per entity per frame (99k lines of the
117k). Per-channel is the usable form. The table holds `0x80` names of at most 47 characters, and a channel name
cannot contain a space or `=` (the parser's tokenizer would split it). A name that is not a real channel opens
nothing at all, silently — the sink prints channels with a DOT (`GameCode.Stats`) while the call sites and the
table use a SLASH, so **install the slash form** and check it exists:
`strings -n 3 AgeOfConan.exe | grep -xF 'N3/IIR'` (`N3.IIR` logs nothing; only `N3/IIR` exists). Pass `-n 3`: `strings` prints runs of **4+** printable characters by default, so a
three-character channel name is invisible to it — `BCC` is a real channel (6 sites) that a plain `strings` search
reports as missing. The call-site scan below has no such blind spot.

Output goes to `AgeOfConan.log`; `PrintFlush=true` (→ `0x029864E0`) flushes per line, so `--console` shows it live.

Configuring the file rather than the live process is what makes this work on Windows as well as Linux, needs no
ptrace, and has the channels open from the client's **first** log line — measured, `GameCode/ItemData` output
appears 3 s after launch, at log line 82, long before world entry.

**What it gives us** — the client stating what it computed, e.g.

```
INFO1: GameCode.Stats - Character 134407636 got 6 stats from server
INFO0: GameCode.Stats - Character 134407636 got new stats: 507=291000
INFO2: GameCode.Stats - 2:134407636 stat 19 old 0 new 0
INFO2: BassiNet.Dispatcher - Dispatcher_t 'GameInterface:134407636:0' calling 'PassBlob' from 'GameAgent:1000002:0'
INFO2: N3.IIR - n3infoitemremote::Construct. RECEIVED IIR: class MoveTaskIIR_c
INFO1: ClientInterface - ClientInterface_t::CMsg_StatChange()..(18 23085 old:23080)
```

The full channel list is `strings AgeOfConan.exe | grep '^GameCode/'` (~90). Note the sink prints them with a DOT
(`GameCode.Stats`) while the table and the call sites use a SLASH (`GameCode/Stats`) — install the slash form. Not
every channel is named `GameCode/*`: **`ClientInterface`** is the most useful one outside that prefix.

### `ClientInterface` — every stat the client applies, with its previous value

`ClientInterface_t::CMsg_StatChange()..(<statId> <new> old:<old>)` (`FUN_006dbc30`, level 1). This is strictly more
than `GameCode/Stats`, which only reports server-driven sets: it also catches everything the client computes or
derives locally, which is what makes the attribute pipeline readable. A world entry shows:

1. the client seeding the five attributes to a hardcoded **10** (`(804 10 old:0)` …),
2. then one write per contributing item template, so — read alongside `GameCode/ItemData`, which names the template
   being loaded — **each delta is attributed to an item id**,
3. and `278`/`279`/`280` (Non Combat Health/Stamina/Mana Regen) seeded to `2000`/`1000`/`2000`.

Step 2 makes a per-item comparison against our own item-stat parse possible. An item where the client applies a full
list while our side is empty is a parse miss, not a missing scaling formula: item-template stat regions are mostly
unaligned, so a parser that scans only 4-byte-aligned offsets misses them.

## The channel catalog, and which ones pay

There are **339** channel names in the exe, recovered from the call sites rather than from `strings`: every site
compiles to `push <channelPtr>; push <level>; call FUN_004A8B90`, so scanning `.text` for `68 <ptr> 6A <imm8>` where
the pointer resolves to a printable name yields each channel with its site count and the levels it logs at. (A few
hits are assert filenames — `*.pb.cc`, `*.cpp` — not channels.) The loudest are `BassiNet/Proxy` (768 sites),
`Scaleform` (531), `GameCode/Cars` (176), `GameCode/Criteria` (141), `GameCode/Movement` (140), `Lua` (117).

Measured in-world (L24 character, Tortage, world entry + idle), 34 candidates at threshold 0:

| channel | lines | what it says |
|---|---|---|
| `GameCode/Criteria` | 4337 | `50000:102 Criteria result: 0`, `stat Sex equal success 2 == 2!` — per-NPC criteria evaluation, i.e. quest/dialogue gating. Keeps firing while idle. |
| `GameCode/GameActions` | 880 | `Streaming in spell list with 12 elements` |
| `GameCode/Feats` | 195 | `FeatPointsTable_c have information up to level 80`, `New FeatPoints: 1 TSFeatPoints: 0 at level: 10` — the per-level feat-point table (load only) |
| `GameCode/QuestMarker` | 180 | `CoreCharacterFullUpdateIIR NPC( [Identity 50000:102] )` — which NPCs the client evaluates as quest givers |
| `Control/Data/Movementset` | 106 | `BCTAction with movementset: Group:begging, Action:41237/beghungrylook, Set:137475` — BCT action → animation ids (load only) |
| `GameCode/Skills` | 88 | `CoreSkillHolder::AddSkill 2:285324533(Hakotep) 1037 from 0 to 500` |
| `GameCode/Inplay` | 86 | `FlowControlModule_t::ClientCharacterReadyToSendInPlay()`, `TeleportEndedMessage()`, `Got inplay from client character <name>` |
| `GameCode/GameAction/Modify` | 18 | `GetOnCasterModifyPercent: 0`, `Value after percent modification : 10000` |
| `GameCode/Combat/DPS` | 10 | `CalcBaseDPS: Base DPS:2960.00` → `DPS after abilitybonus:3868.45 (stat:804)` → `after genericdamagemodifier:4086.76` |
| `GameActionData/GetStat` | 2 | `Tried to GetStringStat for GameActionStat_e 1241 but it did not exist` — a stat the client wanted and we never sent |

The remaining candidates registered but stayed silent, because nothing triggered their code path: `Control/IIR`,
`Control/Data/Fatality`, `GameCode/BCC`, `BCC`, `GameCode/Quest`, `GameCode/Quests`,
`GameCode/Quests/QuestJournal`, `CharacterTag`, `GameCode/FeatData`, `GameCode/Magic/Recast`,
`GameCode/Magic/Metaline`, `GameCode/Combat`, `GameCode/Death`, `GameCode/SwapAlternateEquipment`, `Tradepost`,
`Mail`, `GameCode/Shops`, `GameCode/Pets`, `Knubot`, `GameCode/Scry`, `EffectTimeline`. Enable those with the action
that drives them (a cast, a fatality, opening the auction house, talking to an NPC, dying) — same rule that made
`GameCode/ArmorMitigation` look dead until someone opened the character sheet.

Notable per-task channels beyond the default set: `GameCode/Magic/Metaline` for spellweaving,
`GameCode/Magic/Recast` for recast timers, `Tradepost`/`Mail`/`GuildBank`/`GameCode/Shops` for the economy
subsystems, `Knubot` + `GameCode/Criteria` + `CharacterTag` for dialogue gating, `GameCode/Pets` for pets, and
`BassiNet/Proxy` (every RPC through the proxy, 768 sites) when the question is which call the client made.

**`Control/*` and `Control/Data/*` are the client's DATA-LOADING layer, not runtime traces.**
`Control/Data/Movementset` emits all 106 of its lines during startup and never again, and `Control/Data/Fatality`
(9 sites, level 1) stayed silent through a live retail session that included a kill which DID enter the fatality
path — so its emit sites are edge/error branches in fatality data parsing rather than a per-fatality trace. Do not
read the name as "a channel that reports fatalities"; measured against a live session it reports nothing about one.

## The log gate and sink (why memory patching is the wrong knob)

Every channel site compiles to the same shape (here `FUN_0113ba00`, the armour-mitigation computation):

```c
if (DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || FUN_004b2cc0(&DAT_02984ac0, 2, "GameCode/ArmorMitigation")))
    ... build the message ...
    FUN_004a8b90(2, "GameCode/ArmorMitigation", "%s", msg);
```

`FUN_004a8c50(level, channel)` is the per-line predicate (`file_off = VA − 0x400C00`, i.e. `0x004A8C50 → 0xA8050`):
master enable `DAT_021a2c29`, `level < 0` → false, then `DAT_029864f8 == 0` → true for everything, else the
per-channel table (`FUN_004a8070`). The sink `FUN_004a8b90(level, channel, fmt, ...)` is:

```c
if (level >= 0) {
    if (DAT_029864f8 == 0)
        vfprintf((FILE *)(__iob_func() + 0x40), fmt, args);   // <-- STDERR
    else {
        FUN_004a8910(level, fmt, args);                       // <-- the AgeOfConan.log writer
        if (DAT_029864e0 != 0) fflush(NULL);
    }
}
```

`__iob_func() + 0x40` is `_iob[2]` = **stderr**. So `DAT_029864f8` is both a filter bypass and the **output route**:
zero sends every line to stderr, non-zero to the log file. Measured live (`/proc/<pid>/mem` reads):
`DAT_021a2c29` is already `1` in every run, and the client zeroes `DAT_029864f8` itself by world entry — so forcing
either global and watching `AgeOfConan.log` shows nothing, because the lines go to a stream nothing captures.

With `DAT_029864f8` non-zero, `FUN_004b2cc0(&DAT_02984ac0, level, channel)` decides: `[0]` = minimum level,
`[7]` = output mode, `[8]…` = the channel-name table (up to 0x80 entries, 0xC words apart), and mode 2 reads
per-channel thresholds at `[idx + 0x608]`. That is exactly the table `dbDebug.conf` fills at startup, which is why
the config file is the knob: nothing has to be timed against world entry and no memory is written.

- A **static** patch of those tables is impossible: they are in the BSS tail of `.data` (raw bytes end at VA
  `0x02238200`, the tables are at `0x02984AC0`), so there is nothing on disk to edit, and the client memsets and
  fills them at startup regardless.
- A PE-subsystem GUI→CUI flip does not help: the stderr branch of the sink only runs while the logger is
  uninitialised, and it never is in-world.
- The lines `AgeOfConan.log` carries with the stock config (`ERROR` / `WARNING` / `IMPORTANT`, RDB-miss,
  `Playfield.Loading` timings) are what `Faolan.ClientLauncher --console` tails.

Before reaching for memory or exe patching, look for a config file the client already parses: `dbDebug.conf` sits
in the install dir, and its `DebugLevel=3` is the default threshold.
