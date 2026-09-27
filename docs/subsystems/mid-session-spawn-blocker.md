# Mid-session spawns: the playfield-instance gate

A world entity pushed after the entry burst (an NPC spawn, a kill-drop loot bag, a mount, an interest-streamed NPC,
a respawn) renders only if its playfield-instance token equals the token of a playfield the client has loaded. A
spawn carrying any other token is dropped silently, before the client ever polls it — the packet can be
byte-structurally perfect and still never draw. Verified in-client: a Crocodile spawned mid-session with the live
token draws its `0x2020` poll + `0x2008` SpawnCheck and renders.

## The render mechanism

An entity renders via a poll handshake, not a raw push:

```
server: FA015807 spawn (handle H)
client: registers H, then POLLS  ->  0x2020  body = [0000c350][u32 H][0000c350][u32 charId]
server: SpawnCheck reply         <-  0x2008  body = [0000c350][u32 H][flag]
client: NOW renders H
```

Shown in `Captures/NecroL80_mage-necromancer_login_mount_move_dismount.tshark.json`: the mount (`FA015807`, handle
`0x000286dd`) is pushed at packet idx 1523, the client polls `0x2020` for `0x000286dd` at idx 1545, the server
answers `0x2008` at idx 1553, and it renders. Retail's client polls 69× across the session (world-entry +
mid-session, for the mount and for interest NPCs as the player moves). Our server answers the polls in
`SendSpawnCheckResponse`.

## The playfield-instance map lookup (client)

`DAT_029c7ae0` is a global `std::map<uint, Playfield*>` (red-black tree):

1. **Register (at load):** when a playfield activates (`FUN_005a1390`, a virtual `Playfield::OnLoad`-ish slot) it
   calls `n3Playfield::AddPlayfield` (`FUN_0059bdc0`, n3playfield.cpp:0x318), which inserts the playfield into the
   map keyed by `pf->GetInstance()` = the playfield-instance at object offset `+0x94` (asserts "Cant add same
   playfield twice" / "GetPlayfield(pf->GetInstance()) == NULL"). The map's `operator[]` is `FUN_005a2330`.
2. **Resolve (on every spawn):** the sole `FA015807` apply is `FUN_011215e0` (resolves the SimpleChar via RTTI,
   pumps ~120 stats, then calls the CoreCharacter add-to-playfield `FUN_01458d40` unconditionally). That looks the
   spawn's target playfield up in the map (`FUN_005a2470`, lookup at `01458da1`; value extracted by
   `FUN_005950e0`). If it is not found (`== DAT_029c7ae0` sentinel → `local_360 == 0`) it jumps to `LAB_0145ac67`
   and never calls `AddChildDynel` (`FUN_0059cdb0`) → `n3Dynel::AddToPlayfieldSpaces` (`FUN_00590b70`, real work in
   `FUN_00591570`) — so the entity is never inserted into the spatial/interest grid, never polled, never rendered.

The gate is exactly: `spawn.targetPlayfieldInstance` must equal a loaded playfield's `GetInstance()` (+0x94). The
burst and mid-session spawns go through the same gate.

**Where `+0x94` comes from:** the `n3Playfield` ctor (`FUN_0059a1a0`, writes `[ESI+0x94]` at `0059a242`) copies it
from its descriptor field 8 (`param_2[0x25] = param_1[8]`), set at playfield construction during world-load
(`FUN_008e0df0` Playfield ctor). Which wire field feeds descriptor[8] is not traced.

Other callers of `AddToPlayfieldSpaces`: `n3Playfield::RelocateDynel` = `FUN_0059da00` (asserts the dynel is
non-null, not dying @+0x17, and already in the tree @+0x42). Callers of `AddChildDynel`: `FUN_0083ae30`, and the
game-code dynel-manager funcs `FUN_01458d40` / `FUN_01460f30` / `FUN_014613b0`. `0xFA015807` is not an immediate
anywhere (the spawn IIR dispatches through the BKDR-31 name-hash table, like every IIR), so the network→dynel handler
is reached via the dispatch table, not a constant compare.

The despawn has the same instance check: `n3ToClientQuitIIR::Activate` (`FUN_005cbf80`) logs "Trying to quit a
dynel (…) that no longer (or ever) exist" on a null target and gates an accepted one on
`*(playfield+0x94) == IIR field +0x2c`.

## What the server does

- Every mid-session sender stamps the live token: `SendWorldSpawn` re-stamps the spawn's token to the live
  `Zone.PlayfieldToken` via `SpawnTransformPatch`; loot bags are built with the live `Zone.PlayfieldToken` stamped in
  directly by `WorldObjectCodec.Build`. A token carried over from whichever capture session a row or template was
  built in (a beach Crocodile row carries `0x01EA985C`, the live session `0x004FE428`) misses the map.
- `Zone.PlayfieldToken` is set at world entry (`WorldEntryService`) to the map's own `Map.PlayfieldToken`, falling
  back to `WorldState.DefaultPlayfieldToken` (`0x004FE428`, the beach id) for a map with no captured token, with a
  minted `0x00A00000+` token for server-generated zones.
- **The token must be right at placement time, not at first use.** The stamp sits on the line after the player is
  placed in the zone, and the world tick's 1 Hz interest sweep is gated on `Session.WorldEntryDelivered` — a client
  cannot be streamed to before it has been told which playfield it is in. Otherwise the sweep sees the player first,
  streams every in-range NPC with the zone's minted fallback token (`0x00A00002` against map 4010's `0x01EAFAE7`),
  and seeds those handles into the player's `Known` set, so the entry burst reports `0 in-range NPCs` and never
  corrects them: every NPC inside the entry radius stays invisible for the session while fully alive server-side.
- Runtime handles are small (`RuntimeHandleAllocator`, from `0x00090000`): retail mid-session handles are
  `0x000286xx`, the burst `0x000818xx`, the player `~0x0802xxxx`. A large handle (`0x40000000`) also fails to
  render, but small handles alone are not sufficient — the token is the gate.

## Not the gate

- **Framing** — sender `0x13CE71B1:0x58`, receiver `GameInterfaceType (0x6C67C147):charId`, identical for the burst
  and mid-session. Retail uses one world conversation for both phases too: in `mount_move_dismount` every
  `FA015807`, burst (frames 49–89) and mid-session (frames 1277–1459) alike, rides sender `0xb171ce13:0x57`.
- **A missing registration packet** — none. Retail's world-entry handshake (`0x2003` → `0x200b`/`0x200c`/`0x2001` →
  `0x201c`×5 → burst) is what our server sends, and the rendering retail mount spawn has no precursor. The one
  novel pre-burst frame (frame 41, class `0x1d359116:0x57`) is the multi-agent address handoff (ASCII
  `gs013-nj4.ageofconan.com:7028`) + per-char init stats + global game config, carrying a `full_routing_context`
  (see `docs/protocol/02-rpc.md` "Field 3").
- **Conversation liveness** — streaming a continuous `BehaviorControlCenterIIR` (`0x642CD3D6`) heartbeat on the world
  conversation (1834 packets) does not make the client poll a mid-session spawn. Retail has no such steady stream
  either: the ~1,206 `BehaviorId.BccAction` records in that capture are 1,250-of-1,353 to one actor (the
  necromancer's pet) in bursts of ~60 inside a second, and corpus-wide retail sends the record p50 once per actor
  (37,965 over 7,969 actors) — see [behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md).
  `n3InfoItemRemote::PollStatus` (`FUN_005bae50`, which returns `e_Impossible` when an IIR's remote weak reference —
  `FUN_005bb1c0`/`FUN_005bb1f0` resolving `*(holder+8)` — is null) is a receive-side check, not what drops these
  spawns.
- **`96C46740` MoveTask** — only 8 of 42 burst NPCs have it; it is movement, not a render requirement.

Any change here needs an in-client render to confirm — packet/golden tests cannot see the client's poll loop.
Helper scripts: `tools/ghidra/{DumpVtableX,ReadSlotFn,Xrefs}.java`. Ghidra's headless bundle loader fails on a script
path containing `#` or a space, so copy a `.java` to a plain temp directory before running it headless.

Related open work: interest is re-resolved only when the player moves, tracked in Gitea #778.
