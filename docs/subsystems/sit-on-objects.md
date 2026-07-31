# Sitting on world objects (benches)

Verified working in-client 2026-07-25. The wire sequence is fully decoded from
`NecroL80_mage-necromancer_login_path-of-azura_sit-bench`; this page is the model, because the shape is
counter-intuitive in one important way: **getting up is client-initiated, not something the server infers.**

## The sequence

Both halves are the same two-phase confirm on `BehaviorControlCenterIIR` (`0x642CD3D6`); only the behaviour id
differs. Packet numbers are from the sit capture.

```
SIT     6805  c2s  bId=0x12  seq=0x0003     the client asks to use the bench
        6807  s2c  bId=0x12  seq=0x0004     ack (the hold-echo)
        6808  s2c  bId=0x2A  seq=0x0004     the SIT ANIMATION ("chair_sit_calm")
        6811  c2s  bId=0x12  seq=0x0002     the client CONFIRMS
        6813  s2c  bId=0x12  seq=0x000F     the hold-assert - commits the pose

STAND   6829  c2s  bId=0x01  seq=0x0003     the client asks to stand
        6837  s2c  bId=0x01  seq=0x0004     ack
        6830  c2s  bId=0x01  seq=0x0002     the client CONFIRMS
        6838  s2c  bId=0x01  seq=0x000F     commit
        6844  s2c  bId=0x2A  seq=0x0004     the STANDUP ANIMATION ("standup")
```

Moves are echoed **throughout**, including while seated (`6812 -> 6815`, `6841 -> 6842`). Suppressing them
deadlocks anything that waits on movement.

### The seq mapping is not `seq + 1`

`0x0003 -> 0x0004` and **`0x0002 -> 0x000F`** - four matched pairs above, and consistent corpus-wide (a `0x0002`
request is answered with `0x000F` in 70 of 118 cases). `0x0002` is the client confirming; `0x000F` commits.
Implemented in `BehaviorAcks`.

## Our implementation

- `BehaviorAnimations.PlayerSitOnObject` / `PlayerStandUp` - the two `0x2A` animation records (byte-exact vs
  F2795 / F2840). `BenchSitState` builds the `0x12` hold records.
- `WorldBroadcaster.TrySitOnObject` runs the sit's two phases; `SeatedHoldConfirmed` is the latch that answers the
  client's confirm exactly once.
- The **standup request** (`bId=0x01` while seated) is handled in `GameServerListener.Inbound`'s BCC branch: ack it,
  and on the phase-2 confirm call `WorldBroadcaster.StandUp`.
- `StandUpIfMoved` is a **fallback only**, for a player who leaves the seat without asking (a teleport, or a client
  that skips the request). It measures from the position the client PARKS the character at, not from where the player
  clicked: a bench has several seats (3 on the Old Tarantia ones) so the snap lands up to ~1.3 units away, in
  whichever direction that seat lies. Retail's own threshold is ~1.25 (`SeatLeaveDistance` = 1.0).

## Things that look like bugs and are not

- **The client re-sends its own sit behaviour** (`0x2A` carrying `chair_sit_calm`) after being seated. It must NOT be
  echoed back - echoing replays the sit, and the animation's 2.6 s blend renders as a progress bar. Retail emits
  `chair_sit_calm` exactly once per sit, s2c only: across all 111 captures the string appears twice, both s2c
  (re-measured 2026-07-28 - the corpus has since been curated down from 213, and the claim still holds).
- **`movement_drunk_sit_chair_idle`** appears in unrelated gameplay captures as an 89-byte non-BCC record. That is a
  tavern NPC's seated idle, not part of the player sit flow.
- The `key=""` in our feedback `remoteformat` differs from retail's (which carries the object's localized action
  key). It drives the subtitle lookup, not the animation, and sitting works without it.

## Why this took six fixes (#355)

Each defect hid the next, and the first five were only reachable once the one before it was fixed:

1. the `remoteformat`'s length prefix was a baked constant matching only the captured literal, so the client
   discarded the record whole (`c6e015f3`);
2. the echo-ack padded every tail with 12 zero bytes - only the attack behaviours `0x0F`/`0x11` pad an ECHOED tail
   (a replaced `tailOverride` always does), so the standup ack was 8 bytes long and dropped (`063a5a83`);
3. moves were suppressed while seated, which deadlocked the movement-based standup (`9f559caf`);
4. the seat was anchored to the click position rather than the parked one (`346ef3e2`);
5. the client's re-sent sit behaviour was echoed back, replaying the sit (`db9017ec`);
6. and finally: standup is a client REQUEST, not a movement inference (`34a45e4e`).

The lesson worth keeping: the symptom "stuck, then gets up after a bit" was the client asking to stand, never being
answered, and timing out - which is why the server-side latency measured **0.0 s** while the player experienced a
stall (the timer started at the first move, which only arrived after the client gave up). **Reconstructing the
complete packet sequence for the window - one query - explained more than six rounds of incremental probing. For any
interaction with a handshake, build that sequence first.**
