# Client debug logging — SOLVED (the channels work; the 2026-06-15 "dead end" was the wrong knob)

**Status:** SOLVED 2026-07-26, verified live. The 2026-06-15 conclusion below ("the verbose sites don't fire on
this build") is **wrong** — they fire fine. The knob is not the log gate at all: it is the logger's **per-channel
level threshold**, and the client takes it from a plain config FILE. Enable it with
`Faolan.ClientLauncher --debug-channels`, which writes `dbDebug.conf` (`ClientDebugChannels`) — no exe change, no
process memory.

Measured proof: dropping the default threshold produced **117,044 log lines in seconds**, including the
`RECEIVED IIR: class MoveTaskIIR_c` trace this doc previously recorded as never firing.

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

⚠️ Do NOT simply drop `DebugLevel` to 0: `GameCode.Movement` alone logs per entity per frame (99k lines of the
117k). Per-channel is the usable form. ⚠️ The table holds `0x80` names of at most 47 characters, and a channel name
cannot contain a space or `=` (the parser's tokenizer would split it). ⚠️ A name that is not a real channel opens
nothing at all, silently — the sink prints channels with a DOT (`GameCode.Stats`) while the call sites and the
table use a SLASH, so **install the slash form** and check it exists:
`strings AgeOfConan.exe | grep -xF 'N3/IIR'`. (`N3.IIR` shipped in the default set for one commit and logged
nothing; only `N3/IIR` exists.)

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
derives locally, which is what makes the attribute pipeline readable (#442/#444). A world entry shows:

1. the client seeding the five attributes to a hardcoded **10** (`(804 10 old:0)` …),
2. then one write per contributing item template, so — read alongside `GameCode/ItemData`, which names the template
   being loaded — **each delta is attributed to an item id**,
3. and `278`/`279`/`280` (Non Combat Health/Stamina/Mana Regen) seeded to `2000`/`1000`/`2000`.

Step 2 is what **solved #441**. Comparing the client's per-item stats against ours showed a clean split — items whose
region our parser found agreed exactly, items where it found nothing were empty on our side while the client applied a
full list. That looked like a missing scaling formula, and it was not: the stats were in the template all along, and
the parser could not see them because it scanned only 4-byte-aligned offsets while most regions are unaligned. The
channel is what made the discrepancy measurable per item, which is how the real cause was found.

**A PE-subsystem GUI→CUI flip is NOT needed.** It was implemented, tried and removed: the channels stayed silent,
because the stderr branch of the sink only runs when the logger is UNinitialised, and it never is in-world.

Everything below is the original 2026-06-15 write-up, kept because its measurements are sound and only its
conclusion was wrong.

## What we tried

The hook DLL's mechanism #1 (and a short-lived `Faolan.ClientLauncher --debug-log` flag that did the same via a
live `/proc/<pid>/mem` write) set two globals to open the client's internal
`LogBase` trace:

- `DAT_021a2c29 = 1` (master log-enable, 1 byte)
- `DAT_029864f8 = 0` (per-channel filter bypass, 4 bytes)

The premise (from the hook README) was the gate `DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || perChannel(channel))`
— so forcing the second operand true would make every channel log its full debug trace
(`got quest [id]`, `Wrong IIR_t version`, dispatch/inventory/combat, …) to `AgeOfConan.log`.

## What the predicate actually is

`FUN_004a8c50(level, channel)` is the per-line "should output" predicate (disassembled from the exe;
`file_off = VA − 0x400C00`, i.e. `0x004A8C50 → 0xA8050`):

```asm
push ebp ; mov ebp,esp
cmp byte [0x21a2c29], 0     ; master enable
je  ret_false
mov eax,[ebp+8]            ; level
test eax,eax / js ret_false ; level < 0 -> false
cmp dword [0x29864f8], 0    ; the filter bypass
jne per_channel_lookup      ; != 0 -> consult per-channel table (FUN_004a8070)
mov al,1 / pop ebp / ret    ; == 0 -> return TRUE for everything
per_channel_lookup: ...     ; cdecl, returns the table result
ret_false: xor al,al / pop ebp / ret
```

So the README formula was **correct**: `DAT_029864f8 == 0` does force the predicate true. The mechanism wasn't
wrong about the gate. It's wrong about the *outcome*.

## Why it produces nothing — three measured facts

Verified live against a running client (`ptrace_scope=0`, reads/writes via `/proc/<pid>/mem`):

1. **`DAT_021a2c29` is already `1` by default**, in every run, patched or not. Writing `1` is a no-op.
2. **The client zeroes `DAT_029864f8` itself by world-entry.** Read at early startup = `1`; read in-world on an
   **unpatched** run = `0`. So `FUN_004a8c50` already returns true for everything once you're in the world —
   without any patch.
3. **With the predicate wide open, the log does not flood.** In-world idle: `+0 lines / 15 s`, `0` `RECEIVED IIR`.
   The saved patched run that reached playfield-load (571 lines) contained `0` `RECEIVED IIR` / `DEBUG:` /
   `VERBOSE:` / `TRACE:` lines. The verbose `LogBase` sites simply don't fire on this build — they're release-
   compiled out or guarded further — so opening the gate changes nothing.

The lines `AgeOfConan.log` *does* carry (`ERROR` / `WARNING` / `IMPORTANT`, RDB-miss, `Playfield.Loading`
timings) are emitted by the stock client regardless of the gate. That's the same content
`Faolan.ClientLauncher --console` already tails into the terminal.

## What the sink actually does (2026-07-26 — this is the correction)

Decompiling the log CALL and the SINK, rather than only the predicate, shows why the log file stayed quiet. Every
channel site looks like this (here `FUN_0113ba00`, the armour-mitigation computation):

```c
if (DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || FUN_004b2cc0(&DAT_02984ac0, 2, "GameCode/ArmorMitigation")))
    ... build the message ...
    FUN_004a8b90(2, "GameCode/ArmorMitigation", "%s", msg);
```

and the sink `FUN_004a8b90(level, channel, fmt, ...)` is:

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

`__iob_func() + 0x40` is `_iob[2]` = **stderr**. So `DAT_029864f8` is not just a filter bypass, it is also the
**output ROUTE**: zero sends every line to stderr, non-zero sends it to the log file. The 2026-06-15 patch forced
it to zero and then measured `AgeOfConan.log` — which is exactly the combination that shows nothing. Fact 2 above
(the client zeroes it itself by world-entry) means the stock in-world client is *already* logging these channels,
to a stream nothing captures.

### The configuration that should actually work

1. `DAT_029864f8` **non-zero** in-world (the opposite of the old patch), so the sink takes the file-writer branch.
   The client zeroes it at world entry, so it has to be re-written *after* that — periodically, or by hooking the
   write.
2. With it non-zero the per-channel predicate `FUN_004b2cc0(&DAT_02984ac0, level, channel)` now decides. That
   object is `[0]` = minimum level, `[7]` = output mode, `[8]…` = a channel-name table (up to 0x80 entries, 0xC
   words apart), and mode 2 additionally reads per-channel thresholds at `[idx + 0x608]`:
   - **mode 0 = whitelist** — logs a channel only if its name is IN the table. The low-noise choice: put
     `GameCode/ArmorMitigation`, `GameCode/Stats`, `GameCode/ItemData` in it and get only those.
   - **mode 1 = blacklist** — logs unless the name is in the table, so an all-zero table logs everything.
   - Either way `[0]` must be `<= level`, and these sites log at level 2.
3. `DAT_029864e0` non-zero additionally forces an `fflush` per line, which is what makes tailing usable.

The alternative route — flipping the PE subsystem GUI→CUI so Wine gives the process a real console and stderr
becomes valid — also works in principle and needs no live memory writes, but it changes the exe (and so the
`-clienthash` md5, which `Faolan.ClientLauncher` already recomputes after patching).

Both of those were superseded: point 2's table is written from `dbDebug.conf` at startup, per the top of this doc,
so nothing has to be timed against world entry and no memory is written at all. A note on why a *static* patch of
those tables is impossible, should anyone try: they are in the BSS tail of `.data` (raw bytes end at VA
`0x02238200`, the tables are at `0x02984AC0`), so there is nothing on disk to edit, and the client memsets and
fills them at startup regardless.

## Takeaways

- The gate formula in the original README was right, the measurement was right, and the CONCLUSION was wrong:
  don't cite this doc as "client logging is impossible". Cite it as "`DAT_029864f8 = 0` sends it to stderr".
- What these channels would give us is real ground truth: `GameCode/ArmorMitigation` prints
  `TotalArmorValue`, `ApprenticeLevel Adjusted ArmorValue`, `Mitigation Modification`, `MinMitigation`/
  `MaxMitigation` and `Final Mitigation` per hit — which is exactly what #401 needs, and the same trick applies
  to `GameCode/Stats` (#3) and `GameCode/ItemData` (#441).
- For in-client ground truth the `Faolan.ClientHook` project's other mechanisms were used — the **dispatcher
  hooks** (per-interface server→client packet feed) and **field hooks** (e.g. `QuestFullUpdateIIR::Activate`),
  logging to `faolan_hook.log` via inline detours — but that project is no longer in the repo; Frida-under-Wine
  ([[frida_under_wine_client_re]]) is now the live-RE tool.
- The patch *mechanism* (find the client pid, write+verify `/proc/<pid>/mem`) is sound and still lives in
  `ClientExePatcher` for the DH-key patch; it was only the debug-log *target* that was useless.
- Before reaching for memory or exe patching, look for a config file the client already parses. `dbDebug.conf`
  had been sitting in the install dir the whole time, and its `DebugLevel=3` is literally the default threshold
  the first working patch wrote by hand.
