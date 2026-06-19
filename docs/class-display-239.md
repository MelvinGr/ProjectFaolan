# #239 — in-world character sheet shows the wrong CLASS (Barbarian)

A freshly-created character's in-world sheet shows **Barbarian** regardless of the class chosen, even
though the **character-select screen shows the correct class**. (Note: *class* ≠ *profession* — profession
is the tradeskill; this bug is the class.)

## What it is NOT (ruled out 2026-06-19, captures + proto + Ghidra)

- **Not a protobuf field.** There is no class field anywhere in the character protobuf
  (`AocProtocol` / the recovered client schema). The only `profession_*` fields are `profession_name`
  (ProtoMember 11) / `profession_type` (12) inside **`npc_stats`** — the tradeskill profession, NPC-only,
  not the class.
- **Not the feat tree.** The earlier theory (profession feat = class × 100, fixed via
  `PlayerSpawnScaffoldRows.FreshSkillIdsFor`) is **disproven**: a Necromancer spawn that carries the
  correct profession feat `4100` (verified on the wire) STILL shows Barbarian, and Ghidra shows the class
  getter never reads the spawn feat IdList. Feat trees / AA are not decoded as a subsystem.
- **Not the char-list.** `PlayerAgent/Packets.cs` `SendCharacterList` (0x20EF) sends `character.Class`
  correctly (= 41 for Necro; verified byte-for-byte against a retail Necro char-list). The select screen
  proves the client receives the right class. Only the **in-world** sheet diverges.
- **Not the snapshot / stat block / vitals / StatIIR.** `Captures/scripts/decode_statblock.py` confirms no
  class enum in the FullChar stat block; none of our stat messages carry it.

## What it IS (Ghidra: `AgeOfConan.exe`, project `/tmp/ghidra-aoc/proj`)

The in-world class getter is **`GetClassName` = `FUN_01506030`** (registered in the AA-feat-planner script
API `FUN_01504bf0`, alongside `GetAAFeatTreeDescription`/`HasFeat`/`TrainAAFeat`/…). It reads the **local
player object**:

```
player = (*DAT_0297d374)->vtbl[+0x10]()        // the local player
value  = player->[+0x10c]->vtbl[+8](0x130, 0)  // read attribute key 0x130 (304) off the attribute container
display = FUN_0060f190(0xfa, value, 0)          // generic display-string formatter
```

So the displayed class is a **player-object attribute, key ~`0x130` (304)**, set by some server message.
In our world entry nothing populates `0x130`, so it defaults → Barbarian. (`RDBPLAYERCLASSDATA` loads fine;
no `ClassView_c ... unable to find defined class` error — the value simply defaults.)

## Remaining unknown + plan

Find **which server message sets player attribute `0x130`**. It is written via virtual dispatch on the
`+0x10c` attribute container, so static tracing is slow/uncertain. Efficient next step is **dynamic
analysis**: run the real client under a debugger, breakpoint the `+0x10c` container's setter (vtable slot
at +8 with key `0x130`), and watch what populates it during a real (retail-style) login vs our login.

Also found: seeding the real Necro starting spells into `CharacterSpells` (the wired `DispatchLearnSpell`
0x200A path) **crashes** the client on relog — the spell-grant path (flagged needs-testing) is itself
broken; that's a separate sub-bug.

Ghidra recipe: scripts in `Captures/ghidra/` but they must be run from a **space-free** path (the repo path
contains a space and breaks Ghidra's source-bundle loader) — copy to `/tmp/ghidra-scripts`. Run e.g.
`FNS=01506030 analyzeHeadless /tmp/ghidra-aoc/proj aoc -process AgeOfConan.exe -noanalysis -scriptPath
/tmp/ghidra-scripts -postScript DecompileFns.java`.

## Dynamic-analysis progress (2026-06-19, winedbg on Proton WoW64)

Frida **cannot** hook this client — it runs under Proton Experimental (wine-11.0) in WoW64 (32-bit guest
emulated in a 64-bit process via `wow64cpu.dll`); the Frida injection crashed the client.

**winedbg works** (Proton's `lib/wine/i386-windows/winedbg.exe`, run in the game's own environment via
`/tmp/frida-239/winedbg_env.sh <linux_pid>`, after stripping the inherited `WINESERVERSOCKET` fd; needs
`kernel.yama.ptrace_scope=0`). `attach 0x<winpid>` + `break *0x1506030` sets a real software breakpoint
(verified: `/proc/<pid>/mem` byte at `0x1506030` becomes `0xCC`). AgeOfConan.exe maps at base `0x400000`,
`.text` = `0x401000-0x1e1a000`.

**Player → class-attribute pointer chain** (read live via `/proc/<pid>/mem`; Ghidra-confirmed):
- `holder = *(uint*)0x0297d374`            (DAT_0297d374; live e.g. 0x15f8b078)
- `getPlayer = holder->vtbl[+0x10]`         = `FUN_00596150` = `return *(this+0xa4)`
- `player = *(holder + 0xa4)`
- `attrContainer = *(player + 0x10c)`
- class value = `attrContainer->vtbl[+8](0x130, 0)`  (the GetClassName getter, FUN_01506030)

**Gotchas hit:**
- `GetClassName` (FUN_01506030) is the **AA-feat-planner** class getter; opening the basic character sheet
  does NOT call it (breakpoint never fired). Trigger needs the AA/feat-tree window, or hook a different
  getter, or read the attribute directly.
- A *live* (running-game) read of the container gave an inconsistent/garbage vtable — need the game
  **stopped** for a clean read.
- winedbg launched under `setsid` (no tty) can't be broken in with SIGINT, and **killing winedbg kills the
  ptrace'd game**. Must detach cleanly (`detach`) — or run winedbg under a PTY so Ctrl-C breaks in.

**Plan to finish:** run winedbg under a PTY; set `break *0x596150` (getPlayer, hot — stops within a frame
for a clean read) BEFORE `cont`; read player→container; locate the storage of attr `0x130`; set a
hardware **write watchpoint** there; delete the getPlayer bp; `cont`; then **relog** — the watchpoint
fires when the class is written during world-entry → `bt` gives the setter and the server message behind
it. Detach cleanly to avoid killing the client.

## RESOLVED mechanism (2026-06-19, winedbg memdump + Ghidra): class = player stat 0x43

Walked the live player object (frozen at a `getPlayer` breakpoint, then a full 2.2 GB memdump of the
32-bit guest for offline analysis — `/tmp/frida-239/dump/`, reader `rdump.py`):

- `holder = *0x0297d374` → `player = *(holder+0xa4)` → `attrContainer = *(player+0x10c)`.
- The container's accessor is a functor: `getAttr = *(attrContainer+8)` = `FUN_010fcaf0(this, key, mode)`.
  `attrContainer[0]` is an **adjustor thunk** (`sub ecx,0x10c; jmp …`) that converts `this` back to the
  player and calls the player's base stat getter.
- **`getAttr(0x130)` (the CLASS) decompiles to:**
  ```c
  if (key == 0x130) {            // class
      v = playerStat(0x43);       // primary: stat 0x43 (67)
      if (v != 0) return v;
      return playerStat(0x1f);    // fallback: stat 0x1f (31)
  }
  ```

**So the in-world displayed class = the player's stat `0x43` (67), fallback `0x1f` (31)** — looked up in
`RDBPlayerClassData` by that id (same id space as the char-list `character.Class`, e.g. Necromancer=41). It
is NOT a protobuf field, NOT the feat tree, NOT the char-list. Our server never sends stat `0x43`/`0x1f` for
the player, so the client reads 0 → defaults to Barbarian. (decode_statblock already noted "no class enum in
the snapshot stat block" — correct; the class stat is delivered some other way on retail, TBD.)

**Fix direction:** populate player stat `0x43` (67) = `character.Class` in the stats the client receives
(vitals `0x96B8DC59` and/or a StatIIR / the stat block). Quickest confirm+fix: add stat `0x43`=class to the
Faolan vitals/stat data and verify the sheet in-client. The memdump (`/tmp/frida-239/dump/`) holds the retail
char's actual stat `0x43` value for cross-check.

## Fix attempt 1 (stat 0x43 via vitals) — CRASHED, reverted

Tried delivering it as `vitals (0x96B8DC59) stat 0x43 = character.Class` (VitalsUpdateBuilder.BuildVitals).
In-client result: **crash on load** — the same class-data fragility seen with the spell grant and the wrong
#250 closer (the client rejects class/stat data injected at the wrong point mid-load). Reverted.

Note: retail's snapshot stat block does NOT carry stat 0x43 either (decode_statblock), so retail sets it via
some other path/timing we haven't observed. **The remaining work is purely the DELIVERY**: a write-watchpoint
on the player's stat-0x43 storage during a *retail* relog (using the now-proven winedbg + memdump workflow,
`/tmp/frida-239/`) will show exactly which message/timing sets it — then mirror that on Faolan. The
*mechanism* (class = player stat 0x43, fallback 0x1f) is settled; only the wire delivery is open.

## RESOLVED (interim) — 2026-06-19, commit 399fa15

Fix attempt 2 WORKS: send `stat 0x43 = Character.Class` in `PostReadyFollowUp` (after the 0x2024
ReadyForPlayScreen), NOT in the mid-load vitals. Verified in-client: a fresh Necromancer now shows
**Necromancer** (was Barbarian), no crash. The timing is the whole trick — mid-load stat 0x43 crashes the
loader; post-ready is fine (matches the manual `.setstat 43 <class>` that flipped the sheet live).

Marked INTERIM / NOT RETAIL-ACCURATE in the code: retail does not send stat 0x43 at all (the client computes
it from char data we haven't decoded). When that computed source is found, replace the force-fed stat with
the real data. Until then this gives the correct class display for all classes (id space = char-list Class).
