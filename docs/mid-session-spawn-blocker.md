# Mid-session entity spawn doesn't render — blocker (2026-06-13)

**Symptom.** Nothing spawned mid-session renders in-client: `.spawnnpc` (NPC), `.spawnbag` / kill-drop loot
bags, `.mount` (the horse), interest-streamed NPCs. Only the login **world burst** renders entities. The server
spawns them correctly (they appear in the world model, the packet goes out) — the client just never draws them.

**This is NOT a packet bug.** Proven by a loopback capture (`dumpcap -i lo -f "tcp port 7014"`, no root) of our
own `.spawnnpc`: the outgoing `FA015807` is byte-structurally identical to (a) retail's mid-session spawn and
(b) our own rendering login burst — same sender `0x13CE71B1:0x58`, receiver `GamePlayerType:charId`, body
`[u32 recordLen][fa015807][0000c350][u32 handle][00000000][2c 0a 0f 0d <vec3> 12 <quat> ...]`. Our full
world-entry handshake is also structurally identical to retail's (`0x2003`→`0x200b`/`0x200c`/`0x2001`→`0x201c`×5
→burst, consistent on sub-id `0x58`). No registration/precursor packet is missing (the rendering retail mount
spawn has none either).

## The render mechanism (fully decoded)

An entity renders via a **poll handshake**, not a raw push:

```
server: FA015807 spawn (handle H)
client: registers H, then POLLS  ->  0x2020  body = [0000c350][u32 H][0000c350][u32 charId]
server: SpawnCheck reply         <-  0x2008  body = [0000c350][u32 H][flag]
client: NOW renders H
```

Proven in `Captures/account-faolan-character-necrol80-login-mount_move_dismount.tshark.json`: the mount
(`FA015807`, handle `0x000286dd`) is pushed at packet idx 1523, the client polls `0x2020` for `0x000286dd` at idx
1545, the server answers `0x2008` at idx 1553, and it renders. Retail's client polls **69×** across the session
(world-entry + mid-session, for the mount and for interest NPCs as the player moves).

## Where it breaks for us

Our client polls correctly **at world-entry** — ~264 `0x2020`/`SpawnCheck` exchanges for the burst handles
(`0x000818xx`), which is exactly what renders the burst (our server answers via `SendSpawnCheckResponse`). But
**mid-session our client never polls.** A loopback capture during `.spawnnpc` showed the client sending only
`4517aa81` (movement) and **zero `0x2020`** — it never polls for our pushed crocodile, so it never gets a
SpawnCheck, so it never renders. The client **refuses to register our mid-session `FA015807`** in its interest
set (whereas it registers + polls the retail mount, the same kind of unsolicited push).

So the blocker is a **client load-vs-play gate**: it accepts/registers world entities during world-load
(construction phase), but after "ready for play" (`CharInPlayIIR 0x864cfef8`) it rejects entity registration from
us specifically. Same root as the `n3ToClientQuit sent from 0 -> e_Impossible` warning (the client treats our
mid-session world-conversation sender as unknown / "0").

## Ruled out (all match the rendering burst / retail)

- **Framing** — sender `0x13CE71B1:0x58`, receiver `GamePlayerType (0x6C67C147):charId`: byte-identical to the
  burst and to retail's per-session conversation. Our whole session is consistent on `0x58` (handshake + burst +
  mid-session); the burst's `0x58` renders. (Retail's per-session value is `0x57`/`0x08`/`0x0b` — but it's
  server-chosen and the client addresses us with an empty instance, so a consistent value should suffice.)
- **Handle range** — REAL bug, fixed but insufficient. Retail mid-session handles are tiny (`0x000286xx`); the
  burst is `0x000818xx`; the char-id/player is `~0x0802xxxx`. Our allocator was `0x40000000` → out of range; fixed
  `0x40000000`→`0x0F000000`→**`0x00090000`** (`WorldBroadcaster._nextLootHandle`). All three failed to render —
  small is necessary/correct but not sufficient.
- **Body byte-correctness** — proven builders (`NpcSpawnBuilder`, `WorldObjectBuilder`); the captured loot bag
  carried the correct beach playfield token `0x01EA985C`.
- **Missing registration packet** — none. The subop-set diff vs retail is only movement/combat/map types
  (`StopIIR`, `ClimbingDynel`, `ApplySpell`); the rendering mount has no `0x201c`/registration precursor.
- **96C46740 MoveTask** — only 8 of 42 burst NPCs have it (it's movement, not a render requirement).

## Next step — CLIENT-SIDE RE (packet analysis is exhausted)

Ghidra on `AgeOfConan.exe`, the `FA015807` / interest-registration dispatch + the conversation-table validation:
- Why does the client register + poll a world-entity spawn during load but not after `CharInPlayIIR`?
- What does the play-mode entity handler validate (conversation instance? a playfield "object set" populated only
  at load? an interest-subscription the burst seeds that a mid-session push lacks?).
- Find the step retail's world-entry runs that leaves the client's mid-session interest/poll loop ACTIVE for
  newly-pushed handles — and replicate it.

Until then, mid-session spawning is impossible, which gates **loot bags, `.spawnbag`, `.spawnnpc`, mounts, and
interest-streaming**. (The `DispatchAddItem` live inventory add is unaffected — it's a HUD/inventory update on the
GameCharAgent conversation, not a world-entity spawn, and is in-client verified.)

## Ghidra progress (2026-06-16) — the client-side "register handle" chain mapped

The step that must run for the client to start polling `0x2020` for a handle is the dynel's insertion into the
**playfield spaces** (the spatial/interest grid). Located it client-side (analyzed project `/tmp/ghidra-aoc/proj`;
run headless scripts from a path WITHOUT spaces — the repo's `Captures/ghidra` dir fails Ghidra's bundle loader
because of the `#`/space, so copy the `.java` to e.g. `/tmp/aocscripts` and run there):

- **`n3Dynel::AddToPlayfieldSpaces` = `FUN_00590b70`** — logs `"n3Dynel::AddToPlayfieldSpaces() id "` (gated on the
  dead debug flag `DAT_021a2c29`, see `client_debug_logging_dead_end`), real work in `FUN_00591570`. This is the
  "register the handle so the client polls it" step.
- Callers: **`n3Playfield::RelocateDynel` = `FUN_0059da00`** (asserts the dynel is non-null, not dying @+0x17, and
  ALREADY in the tree @+0x42 — from `n3playfield.cpp`) and **`AddChildDynel`-path `FUN_0059cdb0`**.
- Callers of `AddChildDynel` (`FUN_0059cdb0`): `FUN_0083ae30`, and the game-code dynel-manager funcs
  `FUN_01458d40` / `FUN_01460f30` / `FUN_014613b0` (the `0x0145xxxx`/`0x0146xxxx` region).

`0xFA015807` is NOT an immediate anywhere (the spawn IIR dispatches through the BKDR-31 name-hash table, like every
IIR — `opcode_re_feasibility`), so the network→dynel handler is reached via the dispatch table, not a constant
compare.

### Ghidra deepening (2026-06-16, part 2) — the two resolution gates

Both ends of the render path gate on RESOLVING a mid-session object reference to a live client object, which is
exactly the doc's "client treats our sender as 0 / `e_Impossible`" symptom:

- **The dynel-add gate** — `AddChildDynel` (`FUN_0059cdb0`) is reached from the CoreCharacter playfield-add
  `FUN_01458d40`, which FIRST resolves the spawn's parent **playfield** (`FUN_005a2470` → compare to the sentinel
  global `DAT_029c7ae0` → `FUN_005950e0`); if the playfield doesn't resolve (`local_360 == 0`) it jumps to
  `LAB_0145ac67` and NEVER calls AddToPlayfieldSpaces — so the entity is never registered for the `0x2020` poll.
  i.e. a spawn whose playfield reference doesn't resolve to the client's CURRENT live playfield object is silently
  dropped.
- **The in-play transition** — `CharInPlayIIR` is an EMPTY-message IIR (ctor `FUN_0075a800`/`FUN_0075a890`,
  registered in the IIR registry `FUN_004e4c20`). Its slot-8 ReadSubClass (`FUN_0075a920`, log category
  "GameCode/Inplay") RESOLVES the target SimpleChar from the IIR reference (`*(int*)(param_1+8)+8`, cached at
  `+4`) and logs **"Read InPlay from Client"** / **"cant resolve simplechar"**. The "cant resolve simplechar"
  branch is the same failure family: the in-play machinery can't bind the IIR to the live char.

### COURSE-CORRECTION (2026-06-16, part 4) — the playfield gate is the RENDER path, not the selective blocker

The sole FA015807 apply is `FUN_011215e0` (resolves the SimpleChar via RTTI, pumps ~120 stats, then calls the
playfield-add `FUN_01458d40` **unconditionally**). So the burst AND mid-session spawns hit the SAME playfield-map
gate — which means that gate (part 3) is the RENDER mechanism but CANNOT be what selectively blocks mid-session
(the burst renders through it). **The selective drop is UPSTREAM, before the IIR is applied:**

`FUN_005bae50` = **`n3InfoItemRemote::PollStatus`** (log category "n3InfoItemRemote/PollStatus", emits "is
returning e_Impossible"). It resolves the IIR's **target reference** (`param_1+8`, an `n3InfoItemRemote` = a REMOTE
OBJECT PROXY) against the client's object set; if the reference resolves to NULL it logs "...is NULL..." and
returns **e_Impossible** — the IIR never applies. This IS the long-noted "n3ToClientQuit sent from 0 →
e_Impossible" rejection (n3ToClientQuit::Activate = `FUN_005cbf80`). So the root cause is a **remote-reference
resolution failure**: our mid-session packet's target/sender proxy id isn't in the client's registry, so PollStatus
kills it before it ever reaches the spawn apply / playfield-add. This re-centers on the original
conversation/object-registration theory (memory `protocol_midsession_spawn_blocked`), now with the exact client
function (`n3InfoItemRemote::PollStatus` `FUN_005bae50`) that performs the drop.

**NEXT:** decompile how `n3InfoItemRemote` resolves its proxy (the id→object registry it consults, `FUN_005bb1c0`/
`FUN_005bb1f0` are the resolve+null-check) and find what REGISTERS an object into it at world-entry that a
mid-session push lacks. That registry — NOT the playfield map — is the real gate.

### The playfield-instance map lookup (part 3) — the downstream RENDER path (kept for reference)

`DAT_029c7ae0` is a **global `std::map<uint, Playfield*>`** (red-black tree). The full chain:

1. **Register (at load):** when a playfield activates (`FUN_005a1390`, a virtual `Playfield::OnLoad`-ish slot) it
   calls `n3Playfield::AddPlayfield` (`FUN_0059bdc0`, n3playfield.cpp:0x318), which inserts the playfield into the
   map **keyed by `pf->GetInstance()` = the playfield-instance at object offset `+0x94`** (asserts "Cant add same
   playfield twice" / "GetPlayfield(pf->GetInstance()) == NULL"). The map's `operator[]` is `FUN_005a2330`.
2. **Resolve (on every spawn):** `FUN_01458d40` (the CoreCharacter add-to-playfield) looks the spawn's target
   playfield up in that map (`FUN_005a2470`, lookup at `01458da1`; value extracted by `FUN_005950e0`). The compare
   key is the `+0x94` instance. If it isn't found (`== DAT_029c7ae0` sentinel → `local_360 == 0`) it jumps to
   `LAB_0145ac67` and **never calls `AddToPlayfieldSpaces`** → the entity is never registered for the `0x2020`
   poll → never renders.

**So the gate is exactly: `spawn.targetPlayfieldInstance` must equal a LOADED playfield's `GetInstance()` (+0x94).**
The login burst renders because its spawns' playfield-instance matches the playfield the client registered at
MapChange/world-entry; a mid-session push is dropped if its playfield-instance doesn't match the client's CURRENTLY
loaded playfield's `+0x94`. The doc-verified "loot bag carried 0x01EA985C" is necessary but only sufficient *iff*
`pf->GetInstance() == 0x01EA985C` for the live session — i.e. the token we splice via `SpawnPlayfieldRewriter`
must equal the registered playfield instance, not a captured/stale one.

**Where `+0x94` comes from (client):** the `n3Playfield` ctor (`FUN_0059a1a0`, writes `[ESI+0x94]` at `0059a242`)
copies it from its **descriptor field 8** (`param_2[0x25] = param_1[8]`), set at playfield CONSTRUCTION during
world-load — i.e. `+0x94` is whatever instance the world-load/MapChange path stamps into the playfield, NOT
necessarily the snapshot's playfield token. (Tracing descriptor[8] to its exact wire field is the remaining client
step; it's a deep thiscall ctor chain — `FUN_008e0df0` Playfield ctor.)

**The server-side suspect (offline-checkable lead):** our mid-session spawns get their `18 <varint>` token from
`Zone.PlayfieldToken` (`WorldBroadcaster.cs:862`), which is set by `CapturePlayfieldToken` extracting the token
from the **0x737A6DF9 snapshot body** (`WorldDespawn.TryExtractPlayfieldToken`), with a minted `0x00A00000+`
fallback (`WorldState._nextPlayfieldToken`). If the value the client registers as `pf->GetInstance()` (+0x94, from
the world-load descriptor) is NOT the same as the snapshot-extracted token we stamp into spawns, every mid-session
spawn misses the playfield map → dropped. The burst renders because its spawns carry the captured token verbatim
(which, in the captured session, matched the real playfield instance).

**NEXT (fix direction):** (a) confirm the equality — in-client, log the registered `GetPlayfield(+0x94)` instance
vs the token we stamp; OR finish tracing client descriptor[8] → its wire field and compare to what our world-entry
sends; (b) if they differ, stamp mid-session spawns with the world-load-registered instance instead of the
snapshot token. Also still open: the CharInPlayIIR *Activate* slot (same `+0x94` "cant resolve simplechar"
family). Helper scripts: `Captures/ghidra/{DumpVtableX,ReadSlotFn,Xrefs}.java`. (Any candidate fix still needs an
in-client render to confirm — packet/golden tests can't see the client's poll loop.)

Memory: `protocol_midsession_spawn_blocked`. Related: `protocol_201c_spawn_subprotocol` (the poll loop),
`npc_model_streaming` (its "interest streaming renders" claim is DISPROVEN — those NPCs were the burst).
