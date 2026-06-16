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

**Working theory:** the load-time burst's spawns resolve their playfield/owner because they're processed while the
playfield/object-set is being constructed and "current"; a mid-session push carries the right playfield TOKEN
(0x01EA985C verified) but the client can't resolve that token to the live `Playfield` OBJECT after `CharInPlay`,
so `FUN_01458d40` early-outs before AddToPlayfieldSpaces. **NEXT:** (a) decompile `FUN_005950e0`/`FUN_005a2470`
to see how a playfield token → live `Playfield*` is resolved and what populates that map at load vs play; (b) find
the CharInPlayIIR *Activate* slot (not slot-8 ReadSubClass) to see the state it flips. Helper scripts added:
`Captures/ghidra/DumpVtableX.java` (dump a named `*::vftable`'s slots), `ReadSlotFn.java` (decompile the fn at a
vtable+offset). (Any candidate fix still needs an in-client render to confirm — packet/golden tests can't see the
client's poll loop.)

Memory: `protocol_midsession_spawn_blocked`. Related: `protocol_201c_spawn_subprotocol` (the poll loop),
`npc_model_streaming` (its "interest streaming renders" claim is DISPROVEN — those NPCs were the burst).
