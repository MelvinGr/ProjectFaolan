# Sitting on world objects (benches)

Verified in-client. The wire sequence is fully decoded from
`NecroL80_mage-necromancer_login_path-of-azura_sit-bench`; this page is the model, because the shape is
counter-intuitive in one important way: getting up is client-initiated, not something the server infers.

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
- `WorldObjectService.TrySitOnObject` runs the sit's two phases; `SeatedHoldConfirmed` is the latch that answers the
  client's confirm exactly once.
- The **standup request** (`bId=0x01` while seated) is handled in `BehaviorService`'s BCC branch: ack it,
  and on the phase-2 confirm call `WorldObjectService.StandUp`.
- `StandUpIfMoved` is a **fallback only**, for a player who leaves the seat without asking (a teleport, or a client
  that skips the request). It measures from the position the client PARKS the character at, not from where the player
  clicked: a bench has several seats (3 on the Old Tarantia ones) so the snap lands up to ~1.3 units away, in
  whichever direction that seat lies. Retail's own threshold is ~1.25 (`SeatLeaveDistance` = 1.0).

## Things that look like bugs and are not

- **The client re-sends its own sit behaviour** (`0x2A` carrying `chair_sit_calm`) after being seated. It must NOT be
  echoed back - echoing replays the sit, and the animation's 2.6 s blend renders as a progress bar. Retail emits
  `chair_sit_calm` exactly once per sit, s2c only: across all 111 captures the string appears twice, both s2c.
- **`movement_drunk_sit_chair_idle`** appears in unrelated gameplay captures as an 89-byte non-BCC record. That is a
  tavern NPC's seated idle, not part of the player sit flow.
- The `key=""` in our feedback `remoteformat` differs from retail's (which carries the object's localized action
  key). It drives the subtitle lookup, not the animation, and sitting works without it.

## Pitfalls

Each of these breaks the sit, and each hides the next, so a partial fix looks like no progress:

1. the `remoteformat`'s length prefix must be computed, not baked - a constant that matches only the captured
   literal makes the client discard the record whole;
2. the echo-ack pads a tail with 12 zero bytes only for the attack behaviours `0x0F`/`0x11` on an ECHOED tail (a
   replaced `tailOverride` always pads) - a padded standup ack is the wrong length and is dropped;
3. moves must keep being echoed while seated, or the movement-based fallback deadlocks;
4. the seat is anchored to the position the client parks the character at, not the click position;
5. the client's re-sent sit behaviour must not be echoed back, or it replays the sit;
6. standup is a client REQUEST that must be answered, not a movement inference.

The symptom "stuck, then gets up after a bit" is the client asking to stand, never being answered, and timing out -
which is why server-side latency measures 0.0 s while the player sees a stall (the timer starts at the first move,
which only arrives after the client gives up). For any interaction with a handshake, reconstruct the complete packet
sequence for the window first; it explains more than incremental probing.
