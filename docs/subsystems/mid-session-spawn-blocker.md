# Mid-session entity spawn doesn't render — blocker (2026-06-13)

> **SOLVED IN-CLIENT (2026-06-16).** The root cause is **part 3** below: our mid-session spawns carried the
> playfield-instance token from whatever capture session the row/template was built in (a beach Crocodile row carries
> `0x01EA985C`), NOT this session's live token (`0x004FE428`). The client looks a spawn's playfield up in its
> loaded-playfield map and silently drops a miss before it ever polls (`0x2020`) the handle. The login burst rendered
> only because it was replayed from the same session whose token IS the live one. Fix: `SendWorldSpawn`
> now re-stamps the token to the live `Zone.PlayfieldToken` via `SpawnPlayfieldRewriter` (loot bags are built from
> scratch with the live `Zone.PlayfieldToken` stamped in directly by `WorldObjectBuilder.BuildBody`, no rewriter
> pass; commit `0827af5`). **Verified in-client:** a `.spawnnpc Crocodile` drew the first mid-session `0x2020` poll +
> `0x2008` SpawnCheck in the project's history and rendered. This fixes `.spawnnpc`, mount, interest-streaming,
> NPC respawn, and loot bags.
>
> The "stateful conversation / `n3InfoItemRemote::PollStatus` keepalive" conclusion below (parts 4–7) was
> **DISPROVEN in-client this session:** streaming a continuous `BehaviorControlCenterIIR` (`0x642CD3D6`) heartbeat on
> the world conversation (matching retail's ~1206-packet steady stream that parts 4–7 never noticed) — 1834 packets,
> conversation provably alive — did NOT make the client poll a mid-session spawn. So conversation liveness is not the
> gate; the playfield-token match is. (Parts 1–7 kept below as the investigation record; part 3 was right.)

> **CONVERGED CONCLUSION (2026-06-16, after the Ghidra + offline-capture deep-dive in parts 2–7 below) — SUPERSEDED, see the SOLVED banner above.**
> The selective mid-session drop is a **remote-reference resolution failure** in the client: `n3InfoItemRemote::
> PollStatus` (`FUN_005bae50`) returns `e_Impossible` when an incoming IIR's *remote* reference (a weak-ref proxy
> to the sender world conversation / owner object) resolves to a null holder — so the IIR is discarded before it
> is ever applied. The login burst renders only because the client accepts it in load/construction mode (PollStatus
> not enforced). Established offline: retail uses the SAME world conversation (`0x13CE71B1:0x57`) for BOTH the burst
> and mid-session spawns (part 6), and the world-entry handshake has NO discrete registration packet we omit
> (part 7). So the fix is **NOT** "port PollStatus" (that's the client's receive-side accept check), **NOT** a new
> play-mode conversation, and **NOT** a missing handshake packet. It is the **stateful server-side
> conversation/object model** the real n3 server maintains (keeping the world conversation + its entities alive as
> resolvable remote objects across the load→play transition) — a slice of the missing simulation layer
> (`server_architecture_roadmap`), which Faolan's byte-replay bridge lacks. **Next, both client-gated to verify:**
> (1) build a minimal live remote-object/conversation registration on the world connection and check in-client
> whether PollStatus then resolves a mid-session spawn; (2) Ghidra: what `CharInPlayIIR`'s Activate flips to end
> construction-mode leniency. Key client functions are listed in parts 4–7. (The playfield-instance map gate in
> parts 2–3 is the downstream RENDER path, hit equally by burst + mid-session — NOT the selective blocker.)

**Symptom.** Nothing spawned mid-session renders in-client: `.spawnnpc` (NPC), `.spawnbag` / kill-drop loot
bags, `.mount` (the horse), interest-streamed NPCs. Only the login **world burst** renders entities. The server
spawns them correctly (they appear in the world model, the packet goes out) — the client just never draws them.

**This is NOT a packet bug.** Proven by a loopback capture (`dumpcap -i lo -f "tcp port 7014"`, no root) of our
own `.spawnnpc`: the outgoing `FA015807` is byte-structurally identical to (a) retail's mid-session spawn and
(b) our own rendering login burst — same sender `0x13CE71B1:0x58`, receiver `GameInterfaceType:charId`, body
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

Proven in `Captures/NecroL80_mage-necromancer_login_mount_move_dismount.tshark.json`: the mount
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

- **Framing** — sender `0x13CE71B1:0x58`, receiver `GameInterfaceType (0x6C67C147):charId`: byte-identical to the
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
**playfield spaces** (the spatial/interest grid). Located it client-side (analyzed project `Captures/ghidra/output`;
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

**Proxy mechanism (part 5):** `n3InfoItemRemote` is a WEAK REFERENCE — `FUN_005bb1c0`/`FUN_005bb1f0` resolve
`*(holder+8)` where `holder = proxy[1]` (a `Ptr_c` control block), returning is/isn't-null. So a mid-session IIR's
`remote` is bound to a holder during dispatch; if the referenced owner (the sender CONVERSATION / world endpoint
`0x13CE71B1:0x58`) was never registered in the client, the holder's object is 0 → PollStatus e_Impossible → drop.
Corroborated by the despawn `n3ToClientQuitIIR::Activate` (`FUN_005cbf80`): a null target logs "Trying to quit a
dynel (…) that no longer (or ever) exist", and an accepted one additionally gates on the playfield instance
(`*(playfield+0x94) == IIR field +0x2c`). So EVERY mid-session world IIR (spawn + despawn) fails the same way:
its remote/owner reference doesn't resolve.

**CONVERGED ROOT (parts 4–5):** the selective mid-session drop is a **remote-reference resolution failure** at
`n3InfoItemRemote::PollStatus` — the world conversation our packets ride (`0x13CE71B1:0x58`) is NOT in the
client's object registry mid-session, so every IIR's remote binds to a null holder and is killed before apply.
This is the original conversation-registration theory, now pinned to the exact client functions. The playfield-map
gate (parts 2–3) is the downstream render path, not the blocker.

**OFFLINE CHECK (part 6) — retail uses ONE conversation for both phases.** In the retail mount capture
(`mount_move_dismount`), EVERY `FA015807` spawn — the world-entry burst (frames 49–89, handles `0x0802e5d4` +
`0x000285xx`) AND the mid-session ones (frames 1277–1459, the mount/interest `0x000286xx` range) — rides the SAME
sender **`0xb171ce13:0x57`**. So retail does NOT switch to a different play-mode conversation; the same world
conversation carries load and play, and the client resolves it in BOTH. This RULES OUT "send mid-session on a new
sub-id." The difference is purely that retail's SERVER keeps `0x13CE71B1:0x57` (and its entities) registered as a
live resolvable remote object for the whole session, so the client's `PollStatus` resolves a non-null holder even
for an unsolicited mid-session push; Faolan replays the burst (accepted while the client is in load/construction
mode) but never performs that server-side registration.

**CONCLUSION / fix framing:** this is NOT "port `PollStatus`" (that's the client's receive-side accept check, which
a server never runs) and NOT "switch conversations." It's that the real n3 server registers the world
conversation + its objects as live remote objects and keeps them alive; Faolan, being a byte-replay bridge with no
stateful conversation/object model (see `server_architecture_roadmap` — "protocol done, simulation layer
missing"), never sends the registration the client needs to later resolve the remote. **Open question:** is that
registration a single replayable handshake packet, or does it require the stateful conversation model (a slice of
the missing simulation layer)? **NEXT (offline):** diff retail's world-entry handshake (around the load→play
`CharInPlayIIR`/`ReadyForPlayScreen` transition) vs ours for a conversation/object-registration step we skip; if
it's a discrete packet it's replayable, otherwise it's a simulation-layer build. Confirming any fix needs an
in-client render (`PollStatus` runs only in the live client).

### HANDSHAKE DIFF (part 7) — no discrete missing registration packet

Mapped retail's full world-entry handshake (`mount_move_dismount`, frames 4–49) and matched it against what our
server sends:
- The **world conversation** (`0xb171ce13:0x57` → `0x6C67C147:char`) is established by `0x200b` / `0x200c` /
  `0x2001` immediately after the client's `0x2003`, THEN the burst (`5D85BFC7` MapChange → dynels → `FA015807`).
  Our server already replicates this exact sequence.
- The only novel pre-burst packet, frame 41's **`0x1a07` from class `0x1d359116:0x57`**, is NOT a world-conversation
  registration: its bodies decode to the **multi-agent address handoff** (ASCII `gs013-nj4.ageofconan.com:7028`) +
  per-char init stats + global game config (`AAPointsPerLevel`, `AlternateAdvancement_*`). Our own opcode enum
  even labels `0x1A07` as `Send{CsPlayerAgent,AgentServer,GameServer}` — the agent-address advert, which we already
  perform (the client connects to our agents). So it's a red herring for the remote-resolution gate.

**So the handshake diff finds NO discrete packet we omit** — corroborating the earlier byte-level loopback
comparison ("our world-entry handshake is structurally identical to retail's"). Combined with part 6 (retail uses
ONE conversation for both phases), the answer to the open question lands on **stateful, not a single packet**: the
real n3 server keeps the world conversation + its entities alive as resolvable remote objects across the load→play
transition, and Faolan's byte-replay bridge has no such model, so once the client leaves construction mode the
remote can't resolve. The fix is therefore a **slice of the missing simulation/conversation layer**
(`server_architecture_roadmap`), not a replayable handshake. This is the end of the productive offline trail for
#23; the next move is either (1) build a minimal live remote-object/conversation registration on the world
connection and test in-client whether `PollStatus` then resolves a mid-session spawn, or (2) deeper Ghidra on the
n3 dispatch to learn exactly when/how the construction-mode leniency ends (what `CharInPlayIIR`'s Activate flips).

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
`Zone.PlayfieldToken`, which is set at world entry (`GameServerListener.WorldEntry.cs:831`) to the map's own
`Map.PlayfieldToken` (#305), falling back to `WorldState.DefaultPlayfieldToken` (`0x004FE428`, the beach id) for a
map with no captured token, with a minted `0x00A00000+` fallback (`WorldState._nextPlayfieldToken`) for
server-generated zones. (Originally this came from extracting the token back out of the `0x737A6DF9` snapshot body
via `WorldDespawn.TryExtractPlayfieldToken`; that extract path is now retired in favour of the per-map source.)
If the value the client registers as `pf->GetInstance()` (+0x94, from
the world-load descriptor) is NOT the same as the zone token we stamp into spawns, every mid-session
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
