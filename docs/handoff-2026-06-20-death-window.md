# Handoff 2026-06-20 — Death/release window (#214)

## ★ CURRENT STATE — READ THIS FIRST (FINAL, retail-confirmed 2026-06-20) ★

**The in-session death flow works end-to-end** (release window, movement lock, camera pan, dead pose, title,
server-side revive) — see the session-1/2/3 sections far below. **The one open item is the WAVE-vs-INSTANT
respawn:** on the resurrect click our client takes the wave-respawn path (15 s countdown via
`SignUpForWaveRespawn 0x2025`) instead of retail's instant `RespawnMe 0x2024`.

**ROOT CAUSE (proven by side-by-side live gdb on OUR server vs RETAIL Funcom, both dead on the beach):**
the death/`0x2024`/stat/BccIIR layers are all byte-correct and IDENTICAL to retail — they are NOT the problem.
The sole difference is the client flag **`ResurrectView+0x476`**: retail `0` (→ instant), ours `1` (→ wave).
That flag is set in the window ctor from a **spatial zone lookup** (`FUN_00592aa0` over the global index
`DAT_029c7a8c`, a position→entity/zone tree): retail's lookup at the death spot returns a **zone-region marker**
(`+0x14 = 0x4d8`); **ours has no zone marker there, so it falls through to the nearest ENTITY — the player's own
SimpleChar** (`+0x14` = the player, `+0xc=0xC350`/`+0x10=0x02000000` = its class+handle) → the resurrect logic
misfires → `+0x476=1` → wave. ⇒ **Our world-entry is missing the beach's playfield zone-region data in the
client's spatial index.** This is the "no real playfield model" world-model gap, not a death-packet/stat issue.

**Comparison table (live gdb):**

| field | retail (instant) | ours (wave) | verdict |
|---|---|---|---|
| `window+0x476` | **0** | **1** | ← the ONLY discriminator |
| `window+0x477` | 0 | 0 | same |
| `GetStat(0x546)` | 15 | 15 | red herring (same on both) |
| `GetStat(0x545)` | 0 | 0 | same |
| `sc+0x1e8` (is-proxy) | 0 | 0 | red herring (0 is normal for a local player) |
| `0x2024` body / spot const `0xC7A0` | identical | identical | red herring |

**Red herrings ruled out (do NOT re-chase):** stat `0x546` (=15 on BOTH), `sc+0x1e8` (=0 on BOTH, normal for a
local player), the `0xC7A3` wave-zone constant (our `destObj+0x74=0xC79C`, the normal beach region), and the
self death-effect `ApplySpell(0x0009F000)` (absent from retail beach anyway — we removed it; correct but not the
cause).

**RELATED (Melvin): hostile NPCs render GREEN, not RED.** Name colour = faction/disposition → our NPC spawns
lack hostile-faction data. Same root *area* (incomplete world-entry data: zone-regions + faction), not literally
the same field. The faction thread may be the easier one to pull and could reveal what else world-entry omits.

**State of the code / tree (uncommitted):**
- `Faolan/GameServer/World/WorldSimulation.cs` — removed the extraneous death-effect `ApplySpell` from
  `SendDeathScreen` (matches the retail beach capture; not the wave cause). 14/14 death tests pass.
- This handoff — the full trail + the retail-confirmed conclusion.

**Next steps (pick one):**
1. **True fix (deep):** decode what populates `DAT_029c7a8c` with zone-region markers (Ghidra: it's a member
   structure — find the inserter via `this`, not the global; the readers are `FUN_00592aa0`/`005937f0`/
   `00590e40`/`00568a20`), find which world-entry packet/IIR carries the playfield zone-regions on retail, and
   send it (or stop our over-dense burst from masking it). Same area as `npc_model_streaming` (~4× density).
2. **Faction/green-names thread:** find how the client computes NPC name colour and the faction field it reads
   from the spawn; fixing it may clarify the broader world-entry data gap.
3. **Fallback B (small, contained):** handle `SignUpForWaveRespawn (0x2025)` server-side → fire the instant
   revive immediately so the button works regardless of `+0x476`. Ships the death feature now.

**Reproduce (live gdb; client has NO ASLR, Ghidra addr == runtime):** PE pid = the `pgrep -f AgeOfConan.exe`
whose `/proc/<pid>/maps` has `00400000 ... AgeOfConan.exe`. `sc = *(*(uint*)0x0297d374 + 0xA4)`. ResurrectView
window: scan rw mappings for vtable `0x020589a4`, read `+0x476`/`+0x477`. The `+0x476` decision is
`FUN_012673b0` `0x1267bd4`–`0x1267c20` (breakpoint `0x1267bf2` to read the region lookup result). Stat getter
`FUN_006f18e0`=`0x6f18e0` (`call (long double)(...)(statId,0)` with `$ecx=sc`, `set unwindonsignal on` — inferior
calls are flaky on Wine; prefer raw reads). Retail testing: throwaway Funcom acct, no anti-cheat
(`gdb_live_client_debugging`).

---

## UPDATE 2026-06-20 (session 10, RETAIL live gdb) — ROOT CAUSE: a GameCharInterface RPC drives the hostile name colour; our server doesn't send it

Poked a retail mob's idx to 0 → retail IMMEDIATELY recomputed it to 1 (ours leaves a poke untouched). So
retail recomputes the colour index; ours never does. The 0→1 write backtrace:
- write at `0x011c01b0` `MOV [EDI+0x30],ESI` (EDI+0x30 = iVar3+0x1c = the index; ESI = value) in `FUN_011c0150`.
- value arrives via a SIGNAL/observer cascade: `FUN_00547da0` (signal-emit, iterates a slot list calling each
  observer vtable+8 = SlotFull/SignalSignatureObj/AggressionIndicator pattern) → `FUN_011ca740` (vtable forward
  +0x20) → `FUN_011c0150` (stores idx).
- the signal fires from **`FUN_0148dad0` = the GameCharInterface RPC dispatch** (strings
  `"GameCharInterface_t::SendMessageToEndpoint"` / `"GameCharInterface"`), under the dispatch loop (0x0046xxxx).

**ROOT CAUSE:** a GameCharInterface disposition/aggression RPC (server→client) makes the client compute the
hostile colour index; **retail sends it, our server doesn't** → index stays at the friendly default 0 → green
names. The NpcStats.community we send is correct but irrelevant to the name colour. Tied to the AggressionIndicator
system (AggressionIndicator_c / AI_HostileActionPerformed / agro_overhead_indicator.tga). NEXT: identify the exact
GameCharInterface method/opcode (diff GameCharInterface RPCs retail-vs-ours, or re-trace one frame above
FUN_0148dad0 to read the dispatched method), then implement it server-side.

## UPDATE 2026-06-20 (session 9, LIVE gdb on our client) — CONFIRMED in-client: iVar3[0x1c] is the sole colour driver; our server never sets it hostile

- Mob idx=0 (green) while the player is ALIVE too → NOT a death-state issue (retail = idx 1 red in both states).
- Hardware watchpoint on the mob's `iVar3+0x1c`: **zero writes even after attacking** → the index is never
  recomputed on ours; it sits at the cache-creation default 0.
- **Poke test (decisive): `set *(int*)(iVar3+0x1c)=1` → the mob name turns RED in-client and stays.** So
  `iVar3[0x1c]` is the SOLE name-colour driver and our server never causes the client to compute hostile (1);
  it stays at the default 0=friendly. disp (param_10)=6=hostile is correct on both; the finer index is the bug.
- `iVar3` = cache node from `FUN_011c74c0` (callers 011c3510/3b90/42f0/2c00); creation zeroes fields (idx=0), so a
  separate disposition computation sets idx=1 on retail and never runs / yields 0 on ours.
- **Remaining (one experiment):** on RETAIL, break the FUN_011c74c0 cache-miss (creation) path, grab the new
  node, watch node+0x30 (=payload+0x1c), continue → the 0→1 write fires → backtrace = the writer + the
  server-supplied input it reads = the data gap. Recipe + breakpoints in agent memory
  `npc_green_names_not_missing_faction_data`.

## UPDATE 2026-06-20 (session 8, LIVE gdb retail-vs-ours) — green-names is a CACHED nameplate colour-index bug, NOT the registry/wave root (corrects session 7)

Ran the nameplate colour path live on the client (our server then RETAIL, no ASLR). Decisive:
- **Registry lookup WORKS** — the hostile NPC resolves to its OWN registry node (key matches), not the player's.
  So green-names is NOT a `DAT_029c7a8c` registry miss and does NOT share a root with the wave bug. **Session 7's
  "shared registry root" is SUPERSEDED.**
- disposition (`FUN_011c28c0` param_10/EBX) = **6 = hostile_npc on BOTH** retail and ours (correct). disp 6 ∈ [6,9]
  ⇒ colour via `FUN_011bf370(idx)` (0=friendly +0xab4 / 1=hostile +0xabc / 2=neutral +0xab8), not `FUN_011bf1b0`.
  Palette correct on ours (friendly 0x5b933d / neutral 0xffff00 / hostile 0xb41d1d).
- **RETAIL: idx=1 (hostile red) in ALL states (alive AND dead). OURS: idx=0 (friendly green).** Death-state is NOT
  the variable. Bug is real, server-data-driven.
- **Ruled out by the live diff:** registry miss; faction data we send (NpcStats.community matches); player handle
  (= char id on BOTH — our ids start at 0x02000000 by design, `DatabaseRepository.FirstCharacterId`; Testie's
  handle 0x02000000 is correct, NOT degenerate); death state; the param_2 buffer (matches; its +0x08 is a stray
  stack return-addr into FUN_011c0940 = noise).
- **Localized:** the colour index = `iVar3[0x1c]` (param_11 to FUN_011c28c0, pushed first at
  `FUN_011c3b90:0x011c3e6e` from `[ESI+0x1c]`, ESI=[ESP+0xc]=iVar3). iVar3 = `FUN_011c74c0(*(np+0xa18))`, itself a
  lower_bound CACHE lookup returning node+0x14 (a cached per-nameplate state object). iVar3[0x1c] = 0 (friendly)
  on ours, 1 (hostile) on retail.
- **NEXT STEP:** hardware watchpoint on `iVar3+0x1c` (break bf370 call `0x011c2b51`, recover iVar3, watch +0x1c)
  to catch the writer + the divergent input it reads — that input is the data gap. Gdb recipe + the bf370/disp
  breakpoints + the broken/correct reference buffers are in agent memory `npc_green_names_not_missing_faction_data`.

## UPDATE 2026-06-20 (session 7) — green-names and wave-respawn share ONE root (client code, decisive) + DAT_029c7a8c re-characterized

Followed the disposition path in the client all the way down. **The nameplate-colour path and the wave-respawn
path read the SAME global** — so #256 (green names) and the #214 wave bug are the same world-model gap; one fix
covers both.

**Name-colour chain (Ghidra):** palette loaded by `FUN_011bd980` into the overhead obj (friendly_npc +0xab4,
neutral_npc +0xab8, hostile_npc +0xabc, hostile_minion +0xac0, low_lvl +0xac4). Colour selector
`FUN_011bf1b0(disposition)` maps a **disposition enum**: 5→friendly_npc, 6→hostile_npc, 7/8/9→hostile_minion,
10→low_lvl (also 1 self / 2 team / 3 raid / 4 guild / 0xb friendly_player / 0xc hostile_player). The nameplate
updater `FUN_011c3b90`→`FUN_011c28c0`→`FUN_011bf1b0` resolves the entity via `FUN_00592aa0`/`FUN_005937f0`
over `DAT_029c7a8c` (`payload = node+0x14`) and reads `method_0x60` (= `SimpleChar+0x1e8`) on the result —
**identical to the wave `+0x476` decision** (`FUN_012673b0`).

**DAT_029c7a8c re-characterized (corrects the CURRENT-STATE block's "position→zone spatial index"):**
`DAT_029c7a8c` is the red-black-tree **NIL SENTINEL** (single writer = its init `FUN_01de8890`; every other ref
is a `== sentinel` miss-check). The real registry is an **ordered map keyed by a 2-word (class, handle) key**
(node `+0xc`=class, `+0x10`=handle, `+0x14`=payload). `FUN_005937f0` = raw lower_bound walk; `FUN_00592aa0` =
lower_bound with an at-or-before guard. So the lookups are **(class,handle) registry queries, NOT position→zone
spatial queries** — the "missing beach zone-region spatial data" framing is imprecise. The real symptom (both
bugs): the lower_bound resolves to the **player's own node** (`+0xc=0xC350`, `+0x10=0x02000000`) because the
entry retail has at-or-before the search key is **absent from our registry**. What retail registers there that
we don't is the open question (likely tied to how/whether our local player + world entities are entered into
this registry — same area as the controlled-player / over-dense interest set).

**Decisive next step (same for BOTH #214 and #256, live gdb):** at a nameplate update (bp `FUN_011c3b90`) and at
the resurrect-window ctor (`FUN_012673b0`), on retail vs ours, read the lower_bound search key (the 2-word key
in EAX/EDI), and dump the registry node it resolves to (`+0xc`/`+0x10`/`+0x14`) — find the entry retail has that
ours lacks. Then register it at world-entry. Tooling added this session: `Captures/ghidra/FindFieldAccess.java`
(find `[reg+OFF]` accesses).

---

## UPDATE 2026-06-20 (session 6) — FACTION/GREEN-NAMES thread (Melvin picked this over the wave deep-fix) → Gitea #256

Pulled the related "hostile NPCs render GREEN not RED" thread. **The handoff's premise — "our NPC spawns lack
hostile-faction data" — is WRONG.** Full investigation is on **Gitea #256**; key facts:

- **We send NPC faction data byte-identically to retail.** The hostile discriminator is `NpcStats.community`
  (ProtoCoreCharacterIIR f5/f6): retail hostiles carry community 59/60/317/374, friendlies have it absent. Our
  DB `NpcSpawns.Data` blobs carry the SAME values (verified Scavenger Tuut=60, Crocodile=374, …), and
  `NpcSpawnBuilder.TryBuild` only zeroes `SpawnAnimation` — it preserves the whole `NpcStats`/IIR proto. The
  beach (map 4010) DB has every hostile with the right community. `NpcStats.faction_id` (f13) is ABSENT in BOTH
  retail and ours. The PLAYER self-spawn carries NO `NpcStats` at all (true for retail too). Retail sends NO
  `DispatchApplyFactionRank`(0x204F)/`Points`(0x2050) at login (12 captures) — so basic mob colour is not from
  server-sent reputation points either.
- **Client mechanism (Ghidra, no ASLR):** palette from `Gui/Default/Views/PortraitGUI/OverheadConfig.xml`
  loaded by `FUN_011bd980` → overhead-nameplate obj: `color_friendly_npc`+0xab4 / `color_neutral_npc`(#FFFF00)
  +0xab8 / `color_hostile_npc`(#B41D1D)+0xabc / `color_hostile_minion_npc`+0xac0 / `color_low_lvl_npc`+0xac4.
  Disposition computed from `data/FactionRelations.xml` (static client data, present on BOTH). Relevant fns:
  `TargetIsFriendly` `FUN_01141480` (accumulates a disposition rank into obj+0xb4 = 2/10/0x12/0x13; uses
  `GetTeamHolder`/faction); strings `IsFriendlyToFaction`, `CoreCharacter::GetTeamHolder() - called by npc.`
- **Conclusion:** every NPC-side input matches retail, so the divergence is on the **local-player side** — the
  player's faction/team (`GetTeamHolder` input), supplied via the `0x737A6DF9` snapshot OR established by the
  controlled-player setup. **Same world-model gap as the wave-respawn root cause above** (our local player is
  built via the generic FA015807 stream path; the client's spatial/team model of our local player is
  incomplete). **Decisive next step:** live gdb (retail vs ours, same hostile NPC) — read the disposition rank
  (nameplate obj+0xb4) + the local player's team-holder/faction; find which input is null/wrong on ours.

---

The chronological trail (sessions 1–5, including corrected theories) follows. **The section directly below
leads with the `+0x1e8` construction-path theory that the retail read above DISPROVED — it is SUPERSEDED;
keep it only for the reasoning trail.**

## UPDATE 2026-06-20 (session 5, live gdb) — [SUPERSEDED by the CURRENT STATE block above: `+0x1e8` is a red herring; the real discriminator is `+0x476` from a missing zone marker]

**This section's `+0x1e8` conclusion was overturned by the retail ground-truth read (see the top block).** Kept
for the reasoning trail. Confirmed by live gdb on our own client + Ghidra. The death/`0x2024` packets are
byte-correct and are NOT the problem.

### The full causal chain (each link verified)
1. **The resurrect window decides wave vs instant from the window's `+0x477`/`+0x476` flags** (ResurrectView_c
   ctor `FUN_012673b0`, button handler `FUN_01268d20`). Live on our dead client: `+0x477=0`, `+0x476=1`.
2. **`+0x477=0`** ⇒ not the `GetStat(0x360)&0x8000` term and not the `destObj+0x74==0xC7A3` term (we read
   `destObj+0x74 = 0xC79C`, the normal beach region — so the session-3 "`0xC7A3` vs our `0xC7A0`" idea was a
   red herring; those are unrelated values).
3. **`+0x476=1`** routes the gate to `GetStat(0x546)` (the wave-respawn timer). We read it live = **15** → wave
   bar with a 15 s countdown. BUT our server **never sends stat `0x546`** (confirmed: grepped our own
   loopback capture, zero `0x546` in any encoding). The client *derives* `0x546=15` because `+0x476=1`.
4. **`+0x476=1` because of a flag on the local player's own SimpleChar.** The ctor's `+0x476` block
   (disasm `0x1267bd4`–`0x1267c20`): it spatially looks up the player's grid node, takes `node+0x14` = the
   **player SimpleChar** (RTTI `.?AVSimpleChar_t@@`, class `0xC350`, handle `0x02000000`), and sets `+0x476=1`
   iff `SimpleChar->method_0x60() == 0` **OR** `FUN_006dd5b0` finds `SimpleChar+0x380` non-empty.
   Live: `SimpleChar+0x380` list is empty (both words 0), so the sole trigger is **`method_0x60()==0`**.
5. **`method_0x60` (`FUN_006ea600`) just returns the byte `SimpleChar+0x1e8`.** Live: **`SimpleChar+0x1e8 = 0`**.
   So `+0x1e8==0` ⇒ `+0x476=1` ⇒ gate reads `0x546` ⇒ client invents the 15 s wave timer ⇒ WAVE.
   For retail-beach to go INSTANT, retail's local player must have **`+0x1e8 != 0`** (and the list empty).
6. **`+0x1e8` is set ONCE, at SimpleChar construction** (only byte-writers of `+0x1e8` are the ctor and one
   scene-spawn helper). The CoreCharacter ctor `FUN_006eace0` does `*(SimpleChar+0x1e8) = (param_1 == 0)`.
   So `+0x1e8 = 1` **iff the SimpleChar is constructed with arg 0**, else `0`.
7. **Construction-path split (the actual bug surface):** the `SimpleChar_t` ctor `FUN_010fb9e0` has these
   creators:
   - **`FUN_0148c370` = the networked-entity / FA015807-stream reader** — constructs with `&local` (always
     non-null) ⇒ **`+0x1e8 = 0`** (wave).
   - **`FUN_0116e9c0` / `FUN_01121570` = the local-controlled-player factory entries** (referenced as DATA in
     a factory/vtable table at `0x020405d8` / from `FUN_0043ea20`) — these can pass arg 0 ⇒ **`+0x1e8 = 1`**
     (instant).
   ⇒ **Retail builds the local player via the controlled-player factory (`+0x1e8=1`); our world-entry makes the
   client build the local player via the generic FA015807-stream path (`+0x1e8=0`).** The remaining work is in
   the **control-handoff / entity-factory layer**: get the client to construct OUR local player as the
   controlled entity (so the FA015807 self-spawn *updates* a pre-created local player instead of *constructing*
   it). This is core world-entry protocol, not a death-path tweak.

### RETAIL GROUND TRUTH (decisive) — the SOLE discriminator is `+0x476`; `0x546` and `+0x1e8` are BOTH red herrings
Read live via gdb on the retail client (Funcom, throwaway acct), dead on the beach, vs our server:

| field | retail (instant) | ours (wave) |
|---|---|---|
| `window+0x476` | **0** | **1** |
| `window+0x477` | 0 | 0 |
| `GetStat(0x546)` | **15** | 15 |
| `GetStat(0x545)` | 0 | 0 |
| `sc+0x1e8` (is-proxy) | 0 | 0 |

⇒ `0x546=15` and `sc+0x1e8=0` are **identical on both** — neither is the bug. The **only** difference is
`window+0x476`: retail `0` (button reads `0x545=0` → instant), ours `1` (reads `0x546=15` → wave).

**Where `+0x476` diverges (breakpoint on the ctor block `0x1267bd4`–`0x1267c20`, both clients):** the block does a
spatial region lookup `FUN_00592aa0(playerPos)` (a position→region tree at global `DAT_029c7a8c`), takes
`region+0x14`, and sets `+0x476=1` iff `region+0x14`'s `method_0x60()==0` (or its `+0x380` list non-empty).
- **Retail:** lookup returns a region whose `region+0x14 = 0x4d8` (a small non-pointer field of a real zone
  node) → the test does NOT set `+0x476` → **instant**.
- **Ours:** the SAME lookup returns a node whose `region+0x14 = the player's own SimpleChar pointer`
  (`region+0xc=0xC350`, `+0x10=0x02000000` = the player's class+handle) → `method_0x60(player)==0`
  (`sc+0x1e8=0`, normal) → `+0x476=1` → **wave**.

⇒ **ROOT CAUSE (retail-confirmed): the `DAT_029c7a8c` position→entity spatial index lacks the beach's zone-region
markers on our server, so the lookup at the player's spot falls through to the nearest ENTITY (the player
itself) instead of a zone node — `region+0x14` = the player SimpleChar → `+0x476=1` → wave.** (`DAT_029c7a8c`
is a spatial entity/zone index read by `FUN_00592aa0`/`005937f0`/`00590e40`… — NOT a faction table.) Retail's
index has the zone marker (`+0x14=0x4d8`); ours doesn't. The death/`0x2024`/stat layers are all correct; this is
a world-model / playfield-zone-data gap at world-entry.

**RELATED OBSERVATION (Melvin, same session): our hostile NPCs render with GREEN names instead of RED.** Name
colour = faction/disposition (red=hostile, green=friendly/same-team), so our NPC spawns aren't carrying
hostile-faction data → default friendly. Same *root area* as the wave bug — **incomplete world-entry playfield
data** (zone-region markers + entity faction) — though not literally the same field (the spatial index isn't a
faction table). Worth tracking together as the "world-entry playfield/faction data" gap; cracking the faction
field may be the easier of the two and could illuminate what else world-entry omits. **Next step:** find what inserts into `DAT_029c7a8c`
(Ghidra: writers/inserters of that global), determine why our world-entry creates a player-keyed region node at
the beach (likely the over-dense world/region burst, cf. `npc_model_streaming` ~4× density), and stop it /
make the beach resolve to a normal zone node like retail. Everything below (incl. the `+0x1e8` construction-path
theory) is superseded by this.

### CORRECTION (same session, after the live ctor trace) — `+0x1e8` is a RED HERRING; the lever is stat `0x546`
A logging breakpoint on the `SimpleChar_t` ctor (`0x10fb9e0`) over a full fresh world-entry captured **32**
constructions (all via the FA015807 reader `0x148c370`). Exactly **one** got `+0x1e8=0` — the **first**, which a
live read confirmed IS the local player (`sc=0x2b880ed0`); the other **31** (streamed NPCs/remote) got
`+0x1e8=1`. So **`+0x1e8` is just the "is-proxy" flag: `0` = local controlled player (normal!), `1` = remote
proxy.** A local player is ALWAYS `+0x1e8=0`, so `method_0x60()==0` and `+0x476=1` are **normal for any local
player on retail too** — NOT our bug. (The session-5 "construction path" theory above is therefore wrong;
kept only as the trail.)

⇒ Since `+0x476=1` is normal, the wave/instant decision for a local player **always** comes down to
`GetStat(0x546)` (the zone wave-respawn timer): `0` ⇒ instant, `>0` ⇒ wave. **Our client reads `0x546=15`;
retail beach reads `0`.** We send NO `0x546` on the wire (re-scanned our own loopback capture in 4b/3b/2b/
protobuf forms — zero hits). So the client **derives** the 15-second timer from its **current playfield/zone
state**: our world-entry places the client in a zone it classifies as a wave-respawn (battlekeep-style) zone;
retail's beach is a normal instant-respawn zone. **The real fix is in the world-entry/playfield setup (MapChange
/ playfield-instance type), making the client's beach a normal zone — same area as the per-session-replay-bridge
playfield gap.**

**Decisive next step (ends the speculation):** retail ground-truth read (the `gdb_live_client_debugging` setup —
throwaway Funcom account, no anti-cheat). Connect the client to retail, die on the beach, and gdb-read the local
player's `GetStat(0x546)` (expect `0`), `window+0x476/+0x477`, and the current playfield object. Then find where
the client derives `0x546` from the playfield (an RDB wave-timer lookup keyed by the playfield id/type) and what
playfield state our world-entry sends differently from retail's. Until then, the construction-path/`+0x1e8`
angle is closed.

### What was tried and DID NOT fix it (don't repeat)
- Removing the self-applied death-effect `ApplySpell(0x0009F000)` from `SendDeathScreen` (it's absent from the
  retail BEACH death anyway, `Stihetfv_..._beach`). Correct to drop, but it is NOT the wave cause — the death
  effect doesn't set `0x546`; the wave is the construction flag above. (Change kept; it makes us match the
  beach capture.)
- Re-testing on a clean client session. It still waves — so the wave is NOT stale session state (overturns the
  session-3-era "wave = dirty session" note).

### Reproduce / continue (live gdb recipe — client has NO ASLR, Ghidra addr == runtime)
- Find PE pid: the `pgrep -f AgeOfConan.exe` whose `/proc/<pid>/maps` has `00400000 ... AgeOfConan.exe`.
- Local SimpleChar: `sc = *(*(uint*)0x0297d374 + 0xA4)`. Read `sc+0x1e8` (the flag), `sc+0x380` (list).
- ResurrectView window: scan rw mappings for the vtable ptr `0x020589a4`; read `window+0x476`/`+0x477`.
- `method_0x60`=`0x6ea600` (returns `+0x1e8`); ctor `0x6eace0` (`+0x1e8 = param_1==0`); `SimpleChar_t` ctor
  `0x10fb9e0`; creators `0x148c370` (stream ⇒ +0x1e8=0) / `0x116e9c0` / `0x1121570`; the `+0x476` decision is
  `FUN_012673b0` `0x1267bd4`–`0x1267c20`. Inferior `call`s into client getters are flaky on Wine
  (`set unwindonsignal on`); prefer raw memory reads.
- **Next concrete step:** breakpoint the SimpleChar ctor (`0x6eace0`) at next login, log `param_1` + the caller
  return address for the construction whose handle becomes `0x02000000` (the local player) — confirms it comes
  via `FUN_0148c370` and identifies what would route it through `FUN_0116e9c0(0)` instead. Then find the
  server message/sequence that triggers the controlled-player factory and ensure world-entry sends it.



## UPDATE 2026-06-20 (session 4, static) — our `0x2024` is BYTE-IDENTICAL to retail-instant; the wave gate is purely client-side. There is NO server-side wire fix for path A.

**Read this first — it corrects session 3's framing.** A full static pass (Ghidra decompile of the
button handler / window ctor + byte-diff of our `0x2024` against the retail BEACH death capture) proves:

1. **Our release screen is byte-for-byte retail.** The retail beach capture
   `Stihetfv_..._login-die-resurect-beach` has an **in-session death** on the SAME map (4010), SAME char,
   whose `0x2024` release screen (frame 1179) is:
   `deathPos · 0x7E2 · spotPos · 0xC7A0 · map 0xFAA(4010) · 0·0·0 · spawnPointId 0xBF76C · 0x0A · 0x00`
   — i.e. EXACTLY `WriteValidResurrectionSpotsBody` with one spot. Retail then replies pure instant
   **`RespawnMe (0x2024)` `000bf76c`** (frame 1267), **zero `0x2025`/`0x2028` wave packets.** We send the
   same single closest-known spot (`WorldSimulation.SendDeathScreen` → `[spot]`). So nothing we put on the
   wire differs from retail-instant.

2. **The handoff (session 3) conflated two different values.** It claimed the wave gate wants `0xC7A3`
   while our spot const is `0xC7A0`, implying a packet fix. WRONG — those are unrelated and merely adjacent.
   The decompiled wave gate (`FUN_012673b0`, ResurrectView_c ctor) reads a **client-side** object's field,
   NOT our packet's `0xC7A0` spot const. Decompiled chain (window = `param_1`, `piVar8` = the local
   SimpleChar = `(*DAT_0297d374)[0x10]()`):
   - `+0x476`/`+0x477` are zeroed at ctor start.
   - The wave block only runs **inside `if (iStack_e0 != 0)`**, where `iStack_e0` resolves from the
     SimpleChar's **resurrection-destination** fields `piVar8[0x1b]`(+0x6c, a handle) / `piVar8[0x1c]`(+0x70,
     the object). **If the SimpleChar has no resurrection-destination set, the whole wave gate is skipped.**
   - Inside it: `+0x477 = 1` iff `GetStat(0x360) & 0x8000` **OR** `FUN_00591c00()` (returns
     `SimpleChar->+0x70`) has `+0x74 == 0xC7A3`.
   - Button handler `FUN_01268d20`: `+0x477 != 0` ⇒ WAVE (`FUN_012696c0`); else the `+0x476`/stat-0x545/0x546
     path (all those stats absent ⇒ 0) ⇒ INSTANT (`FUN_006ede20` → RespawnMe proxy `0x4fa9f0`).

3. **Stats `0x360`/`0x545`/`0x546` are absent in BOTH retail and our snapshot** (absent ⇒ leaf returns 0),
   so they can't make US wave when retail doesn't. The ONLY discriminator left is term 2:
   `SimpleChar->resurrectionDestination(+0x70)->+0x74 == 0xC7A3` — and whether the destination
   (`piVar8[0x1b]`) is even set. `0xC7A3` is **present in retail too** (beach frames 56/58, inside NPC
   `FA015807` spawns; it's a zone/region-type value in the 0xC79x–0xC7Ax family, cf. the revive
   `IntendedTeleport`'s `0xC79C` at frame 1277) — so its mere presence isn't the discriminator either.

**Conclusion:** the packets are already correct; the wave-vs-instant decision lives entirely in the
client's local SimpleChar state (does it have a resurrection-destination whose region-type is `0xC7A3`?).
This **supports the earlier "wave = client session/playfield state, not a packet flag" finding** over
session 3's "reproduces on a fresh restart." There is no server wire change that distinguishes us from
retail-instant — so path A can only be resolved by a **live gdb read on a dead client at the button
click**, reading (with `piVar8 = (*DAT_0297d374)[0x10]()`): `[piVar8+0x6c]` (dest handle), `[piVar8+0x70]`
(dest obj), `[[piVar8+0x70]+0x74]` (== 0xC7A3?), and the window's `+0x476`/`+0x477`. Breakpoints:
window ctor `0x12673b0`, button handler CMP `[ESI+0x477]` at `0x1268e53`, wave path `0x12696c0`, instant
path `0x6ede20`. If clean retest already goes instant (very possible, given byte-identity), there's
nothing to fix. Else: either find/clear whatever sets the SimpleChar's resurrection-destination region to
`0xC7A3` on our beach, or take fallback B (handle the wave opcodes server-side).

## UPDATE 2026-06-20 (session 3, in-client) — death flow WORKS; the one remaining blocker is the WAVE-respawn path on the resurrect click

**Read this section first — it's the current state. The older sections below are the (correct) trail that led here.**

### What works in-client now (verified by Melvin)
- **Death → release window + movement lock + camera pan + dead pose.** The full Dead substate is entered.
- The window's **location title** resolves, and the **server-side respawn** (revive + move) is correct.

### The fixes that got us here (all committed)
1. **`CharacterStats.bcc_state = 29`** in the player FA015807 self-spawn = THE dead-state gate for a
   login-while-dead char (commit eec6c33). Health=0 and the forced death anim (`anim_forced_id=41877`) are
   COSMETIC (HP bar / lying pose) — they were red herrings. (PlayerSpawnMessage.ApplyCharacter.)
2. **In-session death BccIIR** (`WorldDeath.BuildDeathBcc`, behaviorId 41877) sent at the end of the death
   sequence (`SendDeathScreen`) — this is what makes an IN-SESSION death (not just login-while-dead) enter the
   Dead substate; **it restored the death CAMERA PAN in-client** (commit 767e76a). bcc_state toggles via the
   BccIIR channel (`0x642CD3D6`), NOT the FA015807 (the spawn only carries the *initial* state).
3. **Lightweight in-place revive** (`WorldDeath.BuildRevive` BccIIR bcc_state 29→1 + vitals) replaces the heavy
   `ReenterWorld` in BOTH `RespawnAtChosenPoint` and `RespawnPlayer` (commits 767e76a). Decoded from the retail
   beach death+resurrect capture (`Stihetfv_soldier-conqueror_login-die-resurect-beach`, #1382 revive / #1292
   death / #1387 IntendedTeleport). Retail does NOT world-re-enter for a near revive.
4. **`Character.IsDead` cleared before the revive** (commit 76c0800) — `RespawnAtChosenPoint` forgot to, so the
   re-entry rebuilt the dead spawn. (Now moot since we don't re-enter, but correct.)
5. **`RespawnPoint.Id` = the RDB `Guid`** (commit 4110fec) so the `0x2024` `spawnPointId` is the client id the
   release screen needs for the title + `RespawnMe` echo (beach AlwaysKnown = 784236/0xBF76C). Importer
   `RdbRespawnImporter` captures `<resurrectionpoint Guid=…>`; migration `RespawnPointClientId`; db reseeded.

### THE BLOCKER — the client takes the WAVE-respawn path on the resurrect click
On the resurrect click the client sends **`SignUpForWaveRespawn (0x2025)` + `RequestWaveRespawnTimerUpdate
(0x2028)`** (a repeating ~15s countdown) instead of instant **`RespawnMe (0x2024)`**. We don't handle the wave
opcodes → countdown + nothing happens. **Retail's beach capture uses pure instant `RespawnMe`, zero wave
packets** — so this is wrong, not a retail behavior. It is NOT stale session state (happens on a fresh client
restart AND for in-session death, not just login-while-dead).

**Client decision fully traced (Ghidra):**
- Button handler **`FUN_01268d20`**: goes WAVE (`FUN_012696c0`) if resurrect-window flag **`+0x477 != 0`**
  (primary); else if `+0x476` then stat `0x546`>0 else stat `0x545`>0 → WAVE; else INSTANT (`FUN_006ede20`,
  which calls the RespawnMe proxy `0x4fa9f0`).
- Window setup **`FUN_012673b0`** (ResurrectView_c ctor, loads `ResurrectView.xml`, has a `WaveRespawnBar`
  widget) sets the flags:
  - `+0x477 = 1` if `GetStat(0x360) & 0x8000` **OR** a resurrection-destination object `window[0x70]` (via
    `FUN_00591c00`) has field `+0x74 == 0xC7A3`.
  - `+0x476 = 1` if `SimpleChar[0x81] == 50000 (0xC350, = the entity class, ~always true)` AND current-playfield
    checks (`FUN_00592aa0`).
- Stat leaf `FUN_006f9700` is a per-character stat-map lookup → **returns 0 for an absent stat**. Stats
  `0x360`/`0x545`/`0x546` are **absent in our snapshot AND the FA015807 generic_stats** (checked) → 0.
- Our `0x2024` release-screen body is **byte-identical to retail's instant one** (spot const `0xC7A0`, not the
  `0xC7A3` the wave gate wants); the wave stats are absent. **By this static read it should be INSTANT — yet
  in-client it's WAVE.** So either one offset above is misread, or the trigger is live playfield/destination
  state not in any packet we send differently from retail. Leading suspect: the **playfield** (the `+0x476`
  playfield checks / how the client models our beach playfield instance vs a real Tortage one).

**To resolve A (true instant):** read the live window `+0x476`/`+0x477` (and `window[0x70]+0x74`) on a dead
client when the button is clicked — breakpoint `0x1268e53` (CMP `[ESI+0x477]`) in `FUN_01268d20`, ESI = the
window. ⚠️ gdb on this Wine client is FLAKY: it stops on Wine **SIGUSR1/SIGUSR2** and freezes the client even
with `handle … nostop noprint pass`; kill gdb + `kill -CONT` the client to recover. Client addrs (no ASLR):
button handler `0x1268d20`, window setup `0x12673b0`, wave path `0x12696c0`, instant path `0x6ede20`, stat leaf
`0x6f9700`, playfield getters `0x591c00`/`0x592aa0`, SimpleChar getter via `DAT_0297d374->[0x10]`.

**Fallback B (pragmatic, not yet done):** handle the wave opcodes server-side — route `SignUpForWaveRespawn
(0x2025)` (body `[u32 spawnPointId][u32 0]`) to the same lightweight revive, and answer
`RequestWaveRespawnTimerUpdate (0x2028)` (reply `DispatchReplyWaveRespawnTimerUpdate 0x207C`, format
undecoded — no wave capture) with timer 0 to kill the countdown. Makes the button work regardless of path.
(Melvin wants A — true no-wave — so B is a last resort.)

### Retail revive sequence (decoded, for completing the in-place revive properly later)
After `RespawnMe`, retail (beach #1382-1387) sends, on the player handle, with sender GameAgent 0x59:
`0x642CD3D6` revive BccIIR (bcc_state 29→1) → `0x96B8DC59` vitals (full HP) → `0x10D27BC0` IntendedTeleport to
the res point → NPC interest re-stream. Distance-based: a FAR res point gives a loading screen (world re-entry);
a NEAR one (beach) is this lightweight in-place form. We send the BccIIR + vitals; the IntendedTeleport
(`0x10D27BC0`, body has the playfield token field we'd need) is NOT yet built — we revive in place at the death
spot (fine for the adjacent beach point).

---

## UPDATE 2026-06-20 (in-client, end-to-end) — window + respawn round-trip WORK; revive-completion is the last gap

Live in-client progress (Melvin, on the local server):
- **Window + movement lock: WORKING** (bcc_state=29, commit eec6c33).
- **Location title: WORKING** — `RespawnPoint.Id` is now the RDB `Guid` (commit 4110fec); beach AlwaysKnown
  = 784236 (0xBF76C), byte-matching retail's `0x2024`.
- **Resurrect click → server revive: WORKING** — on a CLEAN relog the client sends instant
  `RespawnMe (0x2024)` `[u32 spawnPointId]`; the server resolves the spot, revives, moves to the res
  point, logs "respawned." Verified by gdb (the resurrect button at ResurrectView `FUN_01268380+0xebd`
  calls the RespawnMe proxy `0x4fa9f0`).

**The "wave respawn" (0x2025/0x2028 + repeating 15s countdown) was STALE SESSION STATE, not a packet
flag.** After an unclean relog the client was stuck in a leftover wave-respawn UI; a fresh client login
makes it use instant `RespawnMe (0x2024)` like retail. So there is nothing to fix on the wave path — just
don't carry over a dirty session. (We still don't handle 0x2025/0x2028; we don't need to for normal death.)

**THE REMAINING GAP — revive completion.** After `RespawnMe` our server calls the full
`ReenterWorld` (54-packet burst-less world entry). The client — already in-world showing the release
window — does NOT apply that as a Dead→Alive transition: the window stays open, the avatar isn't cleanly
revived (HP ticking from mobs, stray XP). **Retail does NOT re-enter the world.** Decoded from
`AssassinL23_...resurect` right after the `RespawnMe` (pkt #4807), the retail revive is a lightweight
in-place sequence on the EXISTING entity:
- **#4840 `0x642CD3D6` BehaviorControlCenterIIR (BccIIR)** on the player handle — the "resurrect confirm"
  behavior action; almost certainly the bcc_state 29→1 transition that CLOSES the window (grammar in
  `docs/behaviorcontrolcenter-grammar.md`).
- **#4845 `0x96B8DC59` vitals** — restore current health to full.
- entity despawns (`0x04B82B2D`) for the old area, then **#4904 `0x10D27BC0` IntendedTeleportIIR** —
  teleport to the res point, then re-stream nearby NPCs (interest).

**Next step (deferred — Melvin 2026-06-20: "we'll figure that out later"):** the choice is DISTANCE-based.
On retail a res point FAR from the death spot gives a loading screen (= a real world re-entry, our current
path); a NEARBY res point (our beach case: death ~<331,146,707>, res point <336,148,660>, ~47 units) does
the lightweight IN-PLACE revive (no loading screen) — BccIIR alive + vitals-to-full + IntendedTeleport +
interest refresh — which is what closes the window cleanly. So `RespawnAtChosenPoint` should pick: near ⇒
lightweight in-place revive (decode #4840 `0x642CD3D6` for the exact bcc_state 29→1 alive form, the inverse
of the dead spawn we already build); far ⇒ keep `ReenterWorld` (with the loading screen). The bcc_state
mechanism we already own is the key to the in-place form.


## UPDATE 2026-06-20 (SOLVED: the window shows) — the gate was `CharacterStats.bcc_state`, NOT health

**The release/resurrect window now paints in-client and the player is movement-locked** (verified by
Melvin). The gate was NEVER health. Walking the same-character (Stihetfv) dead-vs-alive player
FA015807 self-spawn (`SimpleCharFullUpdateIIR`) field-by-field, three things differ that matter, in
ascending order of importance:

1. `CharacterStats.health`(f7) = 0 (life/max full) — **cosmetic**: blanks the HP bar only.
2. `CharacterBcc.anim_forced_id` = 41877 (timer 1.0, blend 1) — **cosmetic**: the lying-down death
   POSE. With only 1+2 the avatar lay on the floor but **could still walk** and **no window**.
3. **`CharacterStats.bcc_state`(f22) = 29** (alive = 1) — **THE gate**. The behavior/state-controller
   state enum. Setting it locks the player into the Dead substate → movement locked → resurrect window
   paints. Across all 15 captured ALIVE player spawns `bcc_state==1`; the ONLY 29 is the dead capture.

All three are now set for a dead character in `PlayerSpawnMessage.ApplyCharacter` (commits 443213b /
4ae5b34 / eec6c33; alive path byte-unchanged; `PlayerSpawnGenerationTests` locks it). So the whole
multi-session "construction-latch from health" thread was a red herring — the dead STATE is an explicit
enum field in the spawn, not an inferred consequence of health=0.

**Two follow-ups remain (the window works, but):**
- **Empty location title.** The `0x2024` spot body carries no name string; the client resolves the
  title from the **`spawnPointId`** (a client-RDB respawn-point id). Retail sends real ids — beach
  (map 4010) = **0xBF76C (784236)**, AssassinL23's map 2010 = 0xF5C46 (1006150). Our `RespawnPoint.Id`
  must BE that client id or the title is blank (and `RespawnMe` can't resolve it).
- **Resurrect button does nothing.** The client echoes the chosen `spawnPointId` back in `RespawnMe`
  (c2s 0x2024); our handler looks it up in `RespawnPoint`. If our id ≠ the client id (above), the
  lookup fails → no revive. Fix = seed the real client respawn-point ids; then verify the
  `WorldSimulation.RespawnPlayer` revive path. (Retail 0x2024 bodies decoded above; per-spot grammar
  in `ResurrectionSpotsBuilder`.)


## UPDATE 2026-06-20 (step 3 IMPLEMENTED) — step 2 zeroed health in the WRONG packet; fixed the FA015807 self-spawn

**The construction-latch theory was right; step 2 just patched the wrong object.** The local
**SimpleChar** (whose dead-flag gates the window, `DAT_0297d374->[0x10]`) is built by
**`SimpleCharFullUpdateIIR` = the player's own FA015807 self-spawn**, NOT the `0x737A6DF9`
FullPlayerChar snapshot. Step 2 zeroed `HealthCurrent` only in the snapshot (a different object) →
the SimpleChar was still constructed full-health → flag false → avatar stood, no window. That fully
explains the step-2 in-client failure WITHOUT abandoning the latch theory.

**Decoded proof (same character alive vs dead).** The retail `login-while-dead` capture's player
self-spawn is **Stihetfv** (handle `0x021663dd`, FA015807 pkt **#50**), and we also have Stihetfv's
**alive** login (`Stihetfv_soldier-conqueror_login.tshark.json` #44) — same char, clean diff. Both
get a self-FA015807 (the local player gets BOTH a self-FA015807 and the `0x737A6DF9` snapshot). The
embedded `ProtoCoreCharacterIIR.CharacterStats` differs in exactly:
- **`health` (f7) = 0 dead / 16800 alive** ← the latch field
- **`life` (f6) stays full** (dead 19600) — only CURRENT health drops, max is untouched
- **`can_fall` (f20) = 0 dead / 1 alive** (a corpse is ragdoll, not gravity-stepped)
- (f7 bcc differs + f12/f13 offensive/defensive targets present when dead — combat residue, ignored)

**Fix (commit 443213b, needs in-client test).** `PlayerSpawnMessage.ApplyCharacter` now sets
`Stats.Health = 0` and `CanFall = false` for a dead character (life/max unchanged; alive path
byte-unchanged). This is the SOLE world-entry player-spawn path (`SendBurstlessWorldEntry` →
`BuildBurstlessPlayerSpawn`). Test `ApplyCharacter_DeadCharacter_ZeroesCurrentHealthKeepsLifeFull`
(PlayerSpawnGenerationTests) locks it. Step 2's snapshot `HealthCurrent=0` STAYS (retail-dead carries
both); they reinforce, not conflict.

**TEST (needs Melvin — visual, the launcher can't see a painted window):** rebuild + restart the
server, flag a char dead (`Character.IsDead=true`, e.g. die in-session or set it in the DB while the
server is stopped), log in → the release/resurrect window should paint at world entry. If it shows,
the latch theory is confirmed at the correct packet and the in-session death (which already sends the
death sequence) should re-latch via the same health=0 the kill record carries. If it STILL doesn't
show, the next lever is the gdb session #2 lead below (the C++ Dead substate transition) — but health
is now zeroed on the constructing packet, so the construction-latch is finally being exercised
correctly.

---

## UPDATE 2026-06-20 (live gdb session #2) — the feed path is COMPLETE; the handoff's gate theory is WRONG

A second live gdb session (client on our LOCAL server, char `Testie` flagged dead) overturned the central
claim of this handoff. **Do not chase the `0x2024`/snapshot/feed path further — it already works end to end.**

**1. `+0xA4` / `method_0x10` is NOT a dead-flag.** `DAT_0297d374` is the local-player **controller**; its
`vtable[0x10]` method (`0x596150`, just `mov eax,[ecx+0xA4]; ret`) returns the controlled local **`SimpleChar_t`**
(RTTI `.?AVSimpleChar_t@@`, vtable `0x203e93c`). That pointer is **non-null whether the player is alive or
dead** (verified: alive-and-walking, `+0xA4` still populated). So the gate `FUN_01395d20`'s
`if (method_0x10() != 0)` is just "is there a local character", not a dead-check. The earlier "the gate returns
false on local" reading was wrong.

**2. Our server drives the ENTIRE native resurrection path to completion** — proven with execution breakpoints
on our local server (fired twice, identical each login):

```
[0x4c16a0  0x2024 DispatchValidResurrectionSpots handler]  ✓ fires
[0x4c1829  vtable+0x94 window-feed call]                    ✓ reached
[0x1395d20 FUN_01395d20 gate]                               ✓ passes (local SimpleChar present)
[0x6edf40  ShowResurrectionView]                            ✓ called (stores deathPos into SimpleChar+0x2b8/0x2bc)
[0x6edf9c  window-open call 0x443c70 on DAT_0297d3d8+0x120] ✓ called  (DAT_0297d3d8 = resurrection-UI mgr, non-null)
[0x6edfa1  window-open returns]                             ✓ completes; observer list non-empty
```

⇒ **There is no missing packet or snapshot field in the feed path.** The whole open sequence executes; the
window simply doesn't paint.

**3. The real gate is the client-side player DEAD STATE.** The window-open completes but manifests nothing
because the local player is never in the **Dead substate of `CharacterStateController_c`** — so the
resurrection-view GUI isn't created/registered (or its handler declines). This is the handoff's _other_
hypothesis (pure-C++ Dead substate), now proven to be THE gate. **The step-2 snapshot/health approach below
aimed at the wrong layer** (it's kept as inert scaffolding; `Character.IsDead` + `CurrentHp=0` make the HP bar
read 0 but do NOT enter the Dead state).

**Next target** find what
transitions the client into the Dead substate. Two leads: (a) Ghidra the `CharacterStateController_c` Dead
transition + what polls the SimpleChar's state/health to trigger it; (b) diff the retail dead player's
`FA015807` spawn / `CharacterIIR` (capture `retail_login-while-dead...` pkt #50, handle `0x021663dd`) against
our generated player spawn for a state field. Key client addresses (no ASLR, runtime==Ghidra): controller
global `DAT_0297d374`; SimpleChar getter `0x596150`; gate `0x1395d20`; `ShowResurrectionView` `0x6edf40`;
window-open `0x443c70`; resurrection-UI mgr global `DAT_0297d3d8`; `0x2024` handler `0x4c16a0`.

---

## UPDATE 2026-06-20 (in-client test) — health=0 is NOT the gate; the construction-latch theory is DISPROVEN

Step 2 was tested in-client (char flagged dead in the DB, relogged). Result: **the 0 HP bar shows correctly
but the release window does NOT, and the avatar stands** — even though the server ALSO fired the full death
sequence (death-pose MoveTask + SimpleCharKilledBy + `0x2024` spots + death effect, confirmed in the log) AND
the snapshot carried `HealthCurrent=0` AND the in-session vitals were 0. So **health=0 at SimpleChar
construction does NOT latch the client dead-flag** — the earlier "construction-latch from health" theory is
wrong. The window is gated on a field we are STILL not setting (confirms the original `vtable+0x10` gdb
finding; neither the snapshot health nor any death packet flips it).

New clue from the retail `login-while-dead` capture: the dead player (handle `0x021663dd`) has its FA015807
spawn (#50) but **NO MoveTask at all** (every `96C46740` in the capture is an NPC's). So the dead pose/state is
carried **inside the player's own FA015807 spawn or its `CharacterIIR`**, not a separate packet — that (or a
non-health state field in the snapshot) is the real gate. Pinning it needs the **step-3 gdb trace** of what
`method_0x10` reads on the live retail client.

Step-2 code is KEPT as scaffolding (Melvin, 2026-06-20: "keep it, set chars alive") but is INERT until the real
field is found: `Character.IsDead` persistence, the snapshot `CurrentHp=0` patch, `BuildVitals`/`EntityVitals`
dead handling, and the `WorldSimulation` set/clear all remain. NB while a char is `IsDead` the world model
treats it dead (NPCs won't attack, no regen) — so only flag a char dead for a death test.

## UPDATE 2026-06-20 (later) — STEP 1 found the (insufficient) field: current-health = 0

The diff (handoff step 1) is **done**. The dead-state field in the `0x737A6DF9` world-entry snapshot is the
**tails stat block's current-health stat**: `statId 0x001B` (`LegacyCharacterStatId.HealthCurrent`) **= 0**
(the `HealthMax`-side slots stay full). Proof + refined mechanism:

- `Captures/scripts/decode_death_snapshot.py`: across **37 alive** login snapshots `HealthCurrent` is always
  full HP; **only** the `login-while-dead` capture has it `= 0`. Locked by `Faolan.Tests/DeathSnapshotTests`.
- Slot map (positional `TailsPostAccessBlock.Values`, the stat block read as `[statId,value]` pairs): the
  current-health VALUE lands at `[23]` (`CurrentHpExtra`), with repeated-block copies at `[54]`/`[202]`; the
  base/max side is `[21]`/`[56]`/`[204]`. Dead capture: `[23]=[54]=[202]=0`, `[21]=[56]=[204]=19600` (max 196).
- This maps 1:1 onto `TailsPostAccessBlock.CurrentHp` (which already writes `[23]`/`[54]`+`[202]`). So a dead
  login snapshot = the normal one with `CurrentHp` patched to **0** (leave the base/max slots full).
- **Refined mechanism (the crux for step 3):** the client's local-player dead-flag (`DAT_0297d374->[0x10]()`)
  is **latched when the SimpleChar is CONSTRUCTED from the snapshot** (health 0 at birth ⇒ flag true). It is
  **NOT** recomputed from a mid-session `HealthCurrent=0` StatUpdate — we already push exactly that in-session
  (`WorldSimulation.ApplyNpcAttack`→`PushVitals`) and the flag stays false (matches `WorldDeath.cs:8`). So the
  login path works via the construction-time latch; the **in-session path needs the state-transition packet
  that re-latches it** (still the gdb-trace work below — find the setter behind `vtable+0x10`).

**Step 2 IMPLEMENTED (commit 1623285) — needs in-client retail test.** A dead character now logs in dead:

- `Character.IsDead` (+ migration `CharacterIsDead`) + `IDatabaseRepository.SaveCharacterDeadState` persist it
  independently of the full-graph save (like the bind point / XP progress).
- `WorldSimulation` sets it on death (`TickPlayerDeaths`, alongside the release screen) and clears it on
  respawn (`RespawnPlayer`, before the revive re-enter so that snapshot is alive).
- Both login tails paths zero `CurrentHp` for a dead char: `RetailLoginReplay` (captured tails) and
  `BeachTailsBuilder.BuildFromScratch` (beach). Max/base HP stays full. Alive path byte-identical (IsDead
  defaults false — the beach goldens still pass); `BuildFromScratch_DeadCharacter_ZeroesCurrentHealthKeepsMax`
  proves the dead shape. 566/566 tests pass; migration auto-applies at server startup.
- **TEST:** rebuild + restart the server (the migration applies on startup), then in-client: die, log out, log
  back in → the release/resurrect window should show at world entry. If it does, the construction-latch theory
  is confirmed and step 3 = replicate that latch mid-session.

Remaining: **step 3** (the in-session re-latch packet — gdb-trace the setter behind `vtable+0x10`).

## TL;DR

The in-client **release/resurrect window still doesn't show on our server**, but a full live-debugging +
capture session proved **our death-packet layer is byte-correct** and pinpointed the real gate. The window is
gated on a **client-side local-player dead-flag** that our world-entry never sets. Retail carries that
dead-state in the **world-entry `FullPlayerChar` snapshot** — not in any combat packet. The fix is to set that
dead-state in the snapshot we already generate (and/or feed it in-session), NOT more death-packet plumbing.

Everything below was verified with gdb attached to the live retail client (no ASLR → Ghidra addrs == runtime;
see agent memory `gdb_live_client_debugging`). Full chain is on Gitea **#214** (comments through 2026-06-20).

## What is already done + committed

- **00982a9** `feat(combat)`: byte-exact player-death sequence (death-pose MoveTask type `0x06`, death-effect
  ApplySpell spell `0x0009F000`, `SimpleCharKilledBy` with self-killer, sender corrected to GameAgent `0x59`)
  - **removed the 10s auto-respawn** (respawn is now purely click-driven via inbound `RespawnMe` c2s 0x2024).
    Golden-tested (`WorldDeathTests` 3/3). All of this is correct — verified on the wire vs retail.
- **dabd2aa** `chore(captures)`: the retail **login-while-dead** capture
  (`Captures/retail_login-while-dead_resurrect-window.{pcapng,tshark.json}`) + Ghidra helpers
  (`Captures/ghidra/{VtableSlots,XrefsRW,WhatClass,CallsWithConst}.java`).

## Proven by live gdb (retail death vs local death, identical results)

The client's death path fires the **same** on our server as on retail:

- `0x148b8f0` `SimpleCharKilledByUpdateIIR::ReadSubClass` — parses our record (version byte `0x01` OK).
- `0x148b880` the apply — records our killer onto the victim `SimpleChar` (`killer=0xC350/0x02000000`).
- `0x4c16a0` the `0x2024 DispatchValidResurrectionSpots` handler → reaches its SUCCESS path `0x4c1829`
  (the `vtable+0x94` window-feed call), no "stream bad" error path.

So **the packets are correct.** Identity is also correct: the victim entity is `SimpleChar_t` (RTTI), class
`0xC350`, handle `0x02000000`; our `EnterWorld`/spawn/kill-record all agree; retail uses the same
char-id-as-handle model (its player handle `0x08006f31`, and `0x021663dd` in the new capture).

## The exact gate (this is the crux)

The `0x2024` window-feed (`vtable+0x94`) resolves to `FUN_0139c990` → **`FUN_01395d20`**:

```
FUN_01395d20(deathPos):
    if (DAT_0297d374 != 0 && DAT_0297d374->vtable[0x10]() != 0):   // is-local-player-dead?
        ShowResurrectionView(deathPos)
```

The window shows **iff `DAT_0297d374->method_0x10()` returns non-zero** (the local-player dead-flag). On our
server the spots are fed but the flag is false → the view stays hidden. `DAT_0297d374` is the local-player /
playfield global; `+0x10` is the dead-flag accessor.

**Refuted:** the Lua `e_OnPlayerDeath` (enum `0x14`, `LuaCallbacks.cpp`) theory — its dispatcher
`FUN_00a1e4f0` fired **0×** during a real retail death. The death UI (corpse + camera-pan + `ResurrectView_c`)
is pure C++ (the Dead substate of `CharacterStateController_c`), not a Lua callback.

## The constructive lead: retail login-while-dead capture

`Captures/retail_login-while-dead_resurrect-window.tshark.json` (518 pkts) — the retail char relogged WHILE
DEAD and came back in the dead state with the window. Decoded:

- **NO `SimpleCharKilledBy`, NO `ApplySpell`** at world-entry.
- The dead-state is carried by the **`FullPlayerChar` snapshot `0x737A6DF9`** (idx 55, ~11.6 KB, receiver =
  player handle `0x021663dd`).
- A single `0x2024` (idx 313) just feeds the window. Its body is **byte-identical to our builder**
  (deathPos + `0x7E2` + spot + `0xC7A0` + mapId `0xFAA`=4010 + zeros + spawnId + `0x0A` + `00`).

⇒ **The dead-state is a field in the world-entry snapshot we already generate**, not a combat replay.

## Next steps (in priority order)

1. **Diff the dead snapshot vs an alive snapshot.** Take `idx 55` of the login-while-dead capture and the
   FullPlayerChar snapshot from a normal (alive) login of the _same/similar_ char, and diff the typed-root +
   tails (grammar in `docs/fullplayer-snapshot-reader.md`). Find the dead-state field (a flag and/or a
   vitals/state value). Caveat: the `0x0000001b` health-stat search hits false positives in string regions
   (`PFAccess`…) — use the snapshot grammar, not a raw byte search.
2. **Set that field in our snapshot builder** for a dead player (and clear it on respawn), so
   `DAT_0297d374->[0x10]()` reads true → the window shows at login-while-dead.
3. **For the in-session death (#214 proper):** trace `DAT_0297d374` (the local-player global) + its method at
   vtable `+0x10` to the underlying flag and its **setter** — then have our in-session death set it the same
   way retail's combat death does (this is the part that does NOT come through the snapshot).
4. Re-test in-client: a dead player at login should show corpse + window; an in-session death should too.

## How to reproduce the debugging setup

- Live gdb on the client: see agent memory `gdb_live_client_debugging` (attach `gdb -p <pid>`, Wine signal
  passthrough, breakpoints at the raw Ghidra addresses, MCP `kill` to trigger a local death). Ghidra project
  at `/tmp/ghidra-aoc/proj`; helper scripts in `Captures/ghidra/`.
- Retail testing: throwaway account, **no anti-cheat** (per Melvin) — connect the client to Funcom, attach
  gdb, die / relog-while-dead. Capture retail traffic with `dumpcap -i wlo1 -f "net 37.18.0.0/16"` (works
  without root) then `Captures/tshark_dump.sh` to decode (GameServer traffic is zlib/plaintext; login is
  TEA-encrypted but irrelevant here).

Related: agent memories `protocol_death_resurrect`, `gdb_live_client_debugging`,
`captures_and_ghidra_always_available`; docs `combat.md`, `fullplayer-snapshot-reader.md`.
