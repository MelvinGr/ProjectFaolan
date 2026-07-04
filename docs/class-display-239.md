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

## What it IS (Ghidra: `AgeOfConan.exe`, project `/home/melvin/Development/Ghidra/Aoc`)

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
contains a space and breaks Ghidra's source-bundle loader) — copy to `/home/melvin/Development/Ghidra/Aoc/scripts`.
Run e.g. `FNS=01506030 analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis
-scriptPath /home/melvin/Development/Ghidra/Aoc/scripts -postScript DecompileFns.java`.

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

## VALIDATED against a retail properly-classed char (2026-06-19)

Read a live retail **Cimmerian Conqueror lvl 2** in-world (winedbg froze at getPlayer, then a direct
/proc/<pid>/mem read; the player's StatHolder is the *richest* array — 218 stats with distinct hp/mana,
vs ~144 for NPC entities). Its stats confirm the mechanism AND the id space:

- `stat 0x36 (level)` = 2, `stat 0x1b (HP)` = 22900, `stat 0x1f8 (mana)` = 21700, `stat 0x1fa (stam)` = 0.
- **`stat 0x43 (CLASS)` = 22 = our `Class.Conqueror` (0x16).** With Necromancer=41 also matching, the
  stat-0x43 class id space **equals our `Class` enum** — so the fix (`stat 0x43 = Character.Class`) is
  correct for every class, not just Necro. (A nearby level-80 NPC had class 24 — NPCs share the same
  StatHolder layout, so identify the player by the richest array / distinct hp≠mana.)
- ⚠️ SUSPECT (do NOT act on): the read labelled `0x1f8=217` as MANA / `0x1fa=0` as stamina. This CONTRADICTS the
  authoritative captured `CharacterMaxStats` (Conqueror = stamina only, mana 0); the label was almost certainly a
  stat-id misread (0x1f8 is stamina). The captures win — don't make Conqueror a mana user on this line.
  StatHolder layout (`StatHolder_t::GetStat` = FUN_006faa30): sorted `[u32 id][u32 value]` at holder+0x28,
  count +0x2c — reusable for stat decoding. (rdump.py over /tmp/frida-239/ reads any guest address.)

## RETAIL SOURCE FOUND (2026-06-19, dynamic backtrace on retail Conqueror)

Set a conditional winedbg breakpoint on `SimpleChar_t::SetStat` (FUN_010fd220) with `key==0x43` and relogged
a retail char — it fired exactly once during char-load. Backtrace:

```
0  FUN_010fd220   SimpleChar_t::SetStat(this, 0x43, classValue)
1  FUN_005bffe0   (stat-apply dispatcher/visitor)
2  FUN_0083ae30   GameCodeDynelFullUpdateIIR::Activate   <-- the player FULL-UPDATE spawn handler
3  FUN_01121560 -> FUN_0083ae30
...
```

(Frame 2 contains the literal string `GameCodeDynel\GameCodeDynelFullUpdateIIR.cpp`.)

**So retail sets the class from the player's FULL-UPDATE SPAWN**: when the client Activates the
GameCodeDynelFullUpdate (the player spawn), it applies the entity's stat data, including
`SetStat(0x43 = class)`. The class is spawn-embedded, NOT a separate packet and NOT computed from the feat
tree. Our FA015807 self-spawn omits it (its CoreCharacter has no class field/stat — CharacterStats f4 carries
race/sex/vitals only, and we send no generic_stats), so the client never sets stat 0x43 → Barbarian.

**Implication for the fix:** the interim `PostReadyFollowUp` `SetStat(0x43)` is *functionally* correct (same
stat, right value) but happens post-ready rather than during spawn-Activate. The fully retail-accurate fix is
to embed stat 0x43 in the player spawn's stat data so Activate sets it (exact carrier field — generic_stats
vs an extra stat block — is the remaining decode). Until then the interim is fine and validated.

## LIVE-RETAIL CONFIRMED: class = stat 0x43, but it is NOT a wire stat (2026-07-05)

Read a **live retail** client (necro lvl80, `/proc/<pid>/mem`, base 0x400000) — the player StatHolder is at
`player+0x134` (array) / `+0x138` (count): **240 stats, `stat 0x43 = 41 = Necromancer`**, `0x36`(level)=80,
`0x1b`(HP)=634100, `0x1f`=4 (the fallback is NOT the class). Chain: `holder=*0x0297d374 → player=*(holder+0xa4)
→ statArray=*(player+0x134)`, sorted `[u32 id][u32 value]` pairs (`live_stats.txt`). The mechanism is now
**live-confirmed**, not just inferred.

**The class is NOT delivered as a wire stat.** The player stat block on the wire (the `0x737A6DF9` FullChar
snapshot) is `[u32 id][u32 value]` BE pairs (found via the HP=634100 fingerprint in
`faolan-...-necro-lvl80` frame 81: `(0x1b,634100)(0x36,80)(0x34,…)…`), but it **does not contain id `0x43`
at all**. Exhaustive search of both lvl80 necrol80 captures + all 8 fresh per-class login captures finds **no
`(0x43, class)` pair in any byte order, in any record**. So the client SETS stat `0x43` during the player-spawn
`GameCodeDynelFullUpdateIIR::Activate` (the retail backtrace) from a **non-stat source** — a dedicated class
field in the spawn, or client-side derivation from cached char-select data (the char-LIST *does* carry the
correct Class; retail likely caches it and applies it as stat 0x43 on Activate). Pinning the exact source needs
GUI-Ghidra decompilation of `Activate`/the visitor `FUN_005bffe0` (the headless decompiler returns empty bodies
for these).

**This supersedes the "generic_stats values-only ordered" note below** — that was WRONG: `g_Int32StatsOrder`
(`0x02d25da4`) reads **empty (0/0) on retail too** (live), so it is not the class carrier and the assert passes
trivially. The generic_stats decode is kept below only for the record.

**Bottom line for #304:** the class is not a simple wire stat we can add to our spawn — retail derives it during
Activate. The post-ready `SetStat(0x43)=Class` force-feed remains the correct, validated delivery; retiring it
requires identifying and replicating Activate's derivation source (open, GUI-Ghidra).

### CONFIRMED CONCLUSION: the class is client-derived from char-select, NOT on the GameServer wire (2026-07-05)

Two independent proofs settle it:

1. **Two-class live confirmation.** Live `/proc/mem` reads of two retail chars: Necromancer `stat 0x43 = 41`
   (`0x29`), Demonologist `stat 0x43 = 44` (`0x2C`) — both exactly our `Class` enum. Mechanism + id-space done.
2. **Necro-vs-Demo capture differential (decisive).** Across the two lvl80 login captures, there is **no
   `(iirType, offset)` — from record start OR end, in ANY `0x2000` record — where the necro stream holds `41`
   and the demo stream holds `44`.** So no GameServer download record carries the class as a field. (The small
   20-byte `[handle][index]` records that briefly looked like a hit are just a `0..47` slot enumeration; the
   `0x737A6DF9` snapshot stat block is `[id][value]` pairs but omits `0x43`.)

Therefore the in-world class is **derived client-side from the char-select `Class`** (delivered earlier on the
**PlayerAgent char-list**, which every capture/our server sends correctly) — the client caches it and applies it
as `stat 0x43` when it Activates the player spawn. The GameServer login stream has no class at all. This is why
"retail computes it client-side" (the original 2026-06-19 hypothesis) was right, and pins the source to the
char-select cache. **Our force-feed is the functional equivalent** of that client-side application; a
"retail-accurate" version would mean getting the client to run its own char-select→`0x43` caching on our
world-entry path (handshake-dependent, likely not worth it). Recommendation: **keep the interim, close the #304
sub-item as won't-fix-by-design.**

### (superseded) generic_stats decode — kept for reference

The CoreCharacter's `generic_stats` vector is read by `CoreCharacterFullUpdateIIR::ReadSubClass`
(`FUN_0145ac80`):

- The reader ends with `assert(g_Int32StatsOrder.size() == m_CoreCharacterMessage.generic_stats_size())`
  (`CoreCharacterFullUpdateIIR.cpp:0x435`; disasm `0145b279: MOV EDX,[0x02d25da8]; SUB EDX,[0x02d25da4]; SAR
  EDX,2`). So `g_Int32StatsOrder` (a `std::vector<u32>` at `0x02d25da4`=begin / `..da8`=end) is the fixed
  **key order**, and the wire `generic_stats` is a **values-only** list — the stat *ids* (incl. `0x43`) are
  **implicit by position**, NOT on the wire.
- Proven by exhaustive search: across all 8 per-class retail login captures (server `37.18.x`, so real retail
  traffic), there is **no `(0x43, class)` pair in any byte order** (`scan43`/`pb22`/full-stream probe = 0 hits).
  The `00 00 00 43` bytes in the stream are float noise (`43 A1 40 00` = 322.5 position arrays), not stat keys.
  → The class value is one entry in a bare `[count][count×u32 value]` list, at the index where
  `g_Int32StatsOrder == 0x43`.
- **`g_Int32StatsOrder` is EMPTY on our Faolan client** (live `/proc/<pid>/mem` read of `0x02d25da4/da8` = 0/0,
  base confirmed 0x400000, player loaded). It is zero-initialised by `FUN_01e14b00` and only ever read (not
  filled) by our login path — which is exactly why our no-`generic_stats` spawn passes the assert (`0 == 0`)
  and does NOT crash, and why the class defaults to Barbarian. On retail the table is populated (source not yet
  found — `FUN_01458d40`, adjacent to the reader, is the accessor; no code takes the vector's address as an
  immediate, so it's filled through a pointer alias / from a data source).

**Consequence for retiring the interim:** you cannot embed *just* the class — the count-match assert means the
player spawn must carry the **entire ordered `generic_stats` value vector** (`size() == g_Int32StatsOrder.size()`,
correct value for every stat), or the client crashes on load. That is the full player-stat-vector construction,
i.e. the **blocked `SpawnPacketBuilder.BuildSpawn` from-scratch** item (capture-bloat-blocked). The post-ready
single-stat `SetStat(0x43)` force-feed is the correct decoupling and should stay until that larger decode lands.
Remaining RE to unblock: recover `g_Int32StatsOrder`'s ordered keys (find `FUN_01458d40`'s data source, or read
it live from a **retail-connected** client — ours never populates it).
