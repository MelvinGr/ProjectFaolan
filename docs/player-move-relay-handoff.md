# Player→player movement relay — handoff (#48)

**Move stream: FIXED + verified in-client (2 clients).** Moving and jumping now render correctly on the
other client. Two follow-on multiplayer-visibility gaps remain (combat + worn weapons), tracked below.

## Combat visibility (attacks) — PARTIAL

Combat packets were all framed to the ACTOR only (`CombatService.SendWorldPacket(client, …)` =
`WorldFraming.Receiver(client)`), so other players saw nothing when someone attacked. Retail relays the
attacker's `BehaviorControlCenterIIR` (the swing/cast animation) to nearby players, carrying the actor's
handle as instance (verified in the other-players-visible capture: s2c BCC for other-player handles
`0x1316xxx`, structurally identical to the self-echo — NOT the targeted result-block ack).

**Added:** `WorldBroadcaster.BroadcastBehaviorToViewers(actor, body)` + `CombatService.BuildBehaviorRelayBody(...)`,
called from `Handle0x2000` for `NormalAttack`/`UnarmedAttack`/`CastSpell`. The relay is gated on the
viewer's `Known` set.

**OPEN — asymmetric:** in-client, the attack renders on only ONE of the two clients. Since moves are
symmetric, both `Known` sets *should* contain each other, so the relay should fire both ways — yet it
doesn't. Next step: runtime-log which viewer is `Known`-gated when each player attacks (a diagnostic
logging pass was prototyped then reverted). Likely a `Known`-set desync after re-entries
(beach→city / relog), OR a per-player handle mismatch between the body's `instance` and the spawn handle.

**OPEN — cast effect:** only the BCC cast-start animation is relayed; the spell projectile/effect
(`ApplySpell 0x6B87E316`, sent attacker-only by the listener) is not yet broadcast to viewers. NPC
target-vitals/damage numbers from another player's hits are also still attacker-only.

## Worn weapons not visible on other clients — OPEN

A viewer only ever receives the actor's `FA015807` spawn body (`CaptureOwnSpawnBody` stashes the
burst-less player spawn). That body is built with worn gear via `PlayerSpawnMessage.ApplyCharacter` →
`PlayerSpawnEquipment.BuildInventoryEntries(equipment)`. Other players see the avatar but NOT its weapon.
Next step: confirm `BuildInventoryEntries` includes the weapon slots (MainHandLeft/Right), and whether a
drawn weapon needs the `CombatMode` stat pushed to viewers (the player-combat-stance broadcast already
exists: `BroadcastPlayerCombatMode`).

---

## Move stream — root cause (DONE)

Root cause = the **CharDCMove `env` byte**: the
client streams its move with `env=0x01` (the "I am the controlling client" flag); the server's echo +
relay must carry `env=0x00`. We forwarded the client's `0x01` verbatim, so every OTHER viewer's client
treated B as a **locally-controlled** entity and ran its own player-physics on it — which is why B
drifted/teleported even while standing still (B sends nothing when idle, so the viewer's client just
free-runs the prediction). Retail strips it to `0x00` on both directions.

**Proof (byte-exact):** in the other-players-visible capture, a c2s move (frame 145) and its s2c twin
(frame 151) are **identical except this one byte** — `...01316e71 **01**1e ...` vs `...01316e71 **00**1e ...`.
The whole transform retail applies on echo/relay is `env: 0x01 -> 0x00`. The velocity/heading tail is
forwarded as-is (capture confirmed).

**Fix:** `Faolan/GameServer/Inbound/Handle0x2000.cs` Move case — the carrier's `movingType` u16 is
`[u8 env][u8 flag1]`; mask off the env byte (`relayMovingType = movingType & 0x00FF`) and use it for BOTH
the self-echo and `BroadcastPlayerMove`. (`docs/chardcmove-grammar.md` documents `env` as the 0x00/0x01
envelope byte.)

---

## Original diagnosis (kept for reference)

**Was:** spawn/visibility/despawn WORK; the **move stream is broken** — a viewer sees another player
drift and teleport, even when that player stands still. Diagnosed, not fixed.

## Symptom (in-client)

Two clients on the **same Shared instance** (teleport both to Tortage city **4020**, `InstanceMode 0`):

- They **see each other** (interest streams the spawn) — visibility is fine.
- The **other player jitters/teleports all over**, including while standing still.

Cross-check: on the **Solo** beach (4010, `InstanceMode 1`) two players correctly do **not** see each
other (#268, verified). So instancing + interest spawn/despawn are good; only the move stream is wrong.

## What the wire does today

Inbound player move (the moving player B → server), `GameServer0X2000Opcodes.Move`:

- `Faolan/GameServer/Inbound/Handle0x2000.cs` ~L81–152. Reads
  `dataobjdec` (the `0xC350` type word), `dataclientinst` (B's handle), `movingType:u16`
  (= CharDCMove `env`+`flag1`), then `movePosition:vec3`, `moveRotation:vec3` (comment: *"not sure if
  this is correct"*), `datadat` = remaining bytes.
- Self-echoes the move back to B (L138–148, framed `LoginWorldSender`/`LoginWorldReceiver`).
- Relays to other players via `_broadcaster.BroadcastPlayerMove(...)` (L152).

Relay path:

- `WorldBroadcaster.BroadcastPlayerMove` (~L358): sets `entity.Position = position`, runs
  `InterestManager.ResolveMove`, then `DispatchInterest(events, new MoveForward(...))`.
- `InterestManager.ResolveMove` (~L78–92) section 2: for each viewer A in range that knows B, emits a
  `Move` event (or `Spawn` if newly in range).
- `WorldBroadcaster.SendWorldMove` (~L1578) writes the move to A:
  `[subop][DataObject][DataClientInstance][MovingType:u16][Position:vec3 BE][Rotation:vec3 BE][Tail]`,
  framed `WorldFraming.Sender` / `WorldFraming.Receiver(recipient)`.

CharDCMove grammar + codec (already decoded, round-trip golden-tested): `docs/chardcmove-grammar.md`,
`Faolan/GameServer/World/DeadReckoningCodecs.cs` (`CharDCMoveCodec.Parse`/`Build`). The record is
**variable**: `[u8 env][u8 flag1][pos:vec3]`, then `flag1` bit4 → 1 collapsed-facing float **or** a
3-float move-vector, then `param6`, `flag2`, and a velocity/accel/heading block gated by flag1/flag2.

## Capture analysis (retail = leading)

Reference: `Captures/Hoxxling_mage-heraldofxotli_login_guild_invite_accepted-random-gameplay-other_playersvisible.tshark.json`
(another player is visible the whole time). Findings:

1. **Retail relays other players via CharDCMove (`0x4517AA81`)** — the same form we use. (Other-player
   handles are `0x01316xxx`, each getting 100–461 CharDCMove; NPCs `0x000195xx` get MoveTask. So the
   form choice is NOT the bug — don't switch players to MoveTask.)
2. **Retail does NOT strip the velocity/heading block.** The relayed s2c `flag1` distribution
   (`0x1e/0x1a/0x3a/0x72`) matches the client's own c2s outbound distribution — it forwards the move
   largely as-is.
3. **Our relay is byte-exact in content.** The fixed `pos(3f)+rot(3f)+tail` split in Handle0x2000 looks
   wrong against the variable grammar, but `pos ++ rot ++ tail` reassembles the original post-position
   bytes exactly, so nothing is corrupted.

So: **form right, content byte-exact, flags match retail** — yet it drifts/teleports.

## Follow-up (not blocking the drift fix)

- **Player StopIIR to viewers.** The capture shows retail's *server* emits `StopIIR (0xD751EBB4)` to
  viewers for stopped PLAYERS too (s2c for handles `0x1316c55`/`0x1316a88`/self), but the player client
  sends **no** StopIIR upstream (c2s StopIIR count = 0). We only generate StopIIR for NPCs
  (`BroadcastNpcStop`). With the env fix the idle viewer holds B at its last position, so this is no
  longer a drift bug — but for an exact retail match the server should synthesize a player StopIIR when
  a player's move stream goes idle (mirror the NPC stop logic). Not implemented.
- **Unhandled c2s move forms.** The inbound `MovingType` switch is hand-curated; the capture shows c2s
  `0x0172` (3 pkts) hitting `default` (dropped, not relayed). Harmless at this volume; widen the relay to
  forward any CharDCMove form if a gap shows in-client.

## Remaining suspects (where to look next) — superseded by the env-byte fix above

- **(a) Dead-reckoning timing/cadence.** CharDCMove carries velocity/accel/heading; the viewer's client
  *extrapolates* B from it. If our relay cadence differs from retail's (too sparse, or we don't relay
  B's idle/stop updates), the viewer over-predicts between updates and snaps on each correction — which
  matches "drifts even when standing still." Check: how *often* does retail emit the s2c move per other
  player, and does it relay the "stopped" frames?
- **(b) RPC framing / move attribution.** Self-echo uses `LoginWorldSender`/`LoginWorldReceiver(client)`;
  the relay uses `WorldFraming.Sender`/`WorldFraming.Receiver(recipient)`. Verify the relayed move is
  attributed to the **mover's** endpoint (B), not a generic/recipient one — retail's move sender encodes
  the moving entity's instance (see the Handle0x2000 L135 note). A wrong sender could make A apply B's
  move to the wrong frame of reference.

## Concrete next step

**Frame-by-frame diff** of our on-wire relayed move vs the retail s2c CharDCMove stream for ONE other
player, including timing. Specifically capture a loopback two-client session (see the
`client_loopback_test_commands` memory) and compare against the reference capture on:

1. **Cadence** — inter-packet interval of the s2c move per other-player handle (retail vs ours).
2. **Framing** — `SenderClass`/`SenderInstance`/`ReceiverClass`/`ReceiverInstance` of the relayed move
   (retail attributes it to the mover; confirm ours does too).
3. **Stop frames** — does retail send a move when the player stops? Do we?

Throwaway analysis scripts used this session (recreate as needed; they live in the session scratchpad):
- tally move sub-opcodes per handle (MoveTask vs CharDCMove), split by `IsDownload`;
- dump sample s2c CharDCMove bodies for one other-player handle and decode `flag1`;
- compare c2s vs s2c `flag1` distributions.
All parse `Captures/*.tshark.json` `Packets[].BodyHex`, finding the subop bytes `96c46740` / `4517aa81`
and reading `[+8]=handle`.

## Pointers

- `Faolan/GameServer/Inbound/Handle0x2000.cs` — inbound Move case + self-echo + relay call.
- `Faolan/GameServer/World/WorldBroadcaster.cs` — `BroadcastPlayerMove`, `SendWorldMove`, `MoveForward`.
- `Faolan/GameServer/World/InterestManager.cs` — `ResolveMove` (section 2 = others' view of the mover).
- `Faolan/GameServer/World/DeadReckoningCodecs.cs` + `docs/chardcmove-grammar.md` — the CharDCMove codec.
- Spawn/visibility + Solo isolation are done (#268); this ticket is the **move stream** only.
