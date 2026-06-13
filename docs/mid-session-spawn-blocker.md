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

Memory: `protocol_midsession_spawn_blocked`. Related: `protocol_201c_spawn_subprotocol` (the poll loop),
`npc_model_streaming` (its "interest streaming renders" claim is DISPROVEN — those NPCs were the burst).
