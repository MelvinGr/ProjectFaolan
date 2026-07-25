# `GameTime_t` — the day/night clock, and what `GameTimeIIR`'s fields mean

> Naming note: this record used to be annotated `"SetDayTime"` throughout the repo. That name has no source —
> it is in no RPC catalog and is not a string in the client — and it implies a setter when the client treats it
> as a resync of a running clock. The client's own names are `GameTimeIIR_t` (the record) and `GameTime_t` /
> `GameTime_t::Update` (the clock it drives).

Recovered from `AgeOfConan.exe` (Ghidra headless, 2026-07-25) plus a regression over every captured
`GameTimeIIR` record. The client side settles the **unit**, the corpus settles the **real-time period**, and the
two agree exactly — which is the only reason either is trustworthy on its own.

Reproduce the client side with `Q=gametime … FindSym.java` to get the string set, then `StrXref.java` on
`GameTime_t::` and `DecompileFns.java` / `Callers.java` / `ReadVals.java` on what it names.

## The class

`gametime.cpp`, a singleton (`GameTime_t::GetInstance()`). Ctor `FUN_004ed470`, and the fields that matter — all
read out with `ReadVals.java` where they are indirect:

| offset | value | meaning |
|---|---|---|
| `+0x38` | accumulator | the running clock, advanced per frame |
| `+0x58` / `+0x5C` / `+0x60` | — | decomposed seconds / minutes / hours |
| `+0x68` | — | recomposed total, used by the asserts |
| `+0x74` | **5** | game seconds per REAL second |
| `+0x78` | **86400.0** | the wrap period — one game day |
| `+0x80` / `+0x88` | **21600.0** / **64800.0** | the day window: 06:00 and 18:00 |
| `+0x98` / `+0x9C` / `+0xA0` / `+0xA4` | **24** / **60** / **60** / **3600** | hours per day, minutes per hour, seconds per minute, seconds per hour |
| `+0xA8` / `+0xAC` / `+0xB0` / `+0xB4` | **19** / **4** / **7** / **2** | dusk start hour + length, dawn start hour + length |
| `+0xBC` / `+0xC0` / `+0xC4` / `+0xD4` | 1.0f | base rate, **day** rate, **night** rate, and the rate the wire sets |

## dayTime is a count of game seconds, 86400 to the day

`FUN_004ee4f0` (`SetCurrentRealTime`) and `FUN_004ee710` (the IIR handler) both decompose the value the same way:

```
seconds = t % 60            # +0xA0
minutes = (t / 60) % 60     # +0xA0, +0x9C
hours   = t / 3600          # +0xA4
```

Only minutes and seconds are taken modulo — **`hours` is not** — so the value is an *absolute running count* of
game seconds, and the time of day is `dayTime % 86400`. The `+0x78` wrap of 86400.0 is only applied to
*negative* inputs (`while (t < 0) t += 86400`), i.e. it normalises, it does not clamp.

Both functions end in an assert that prints `Hours: %f Minutes: %f Seconds: %f` — those out-of-line assert
blocks (`FUN_004f02c0` / `FUN_004f0400`) are what the string xrefs land on first; the real methods are their
callers.

## The tick: 5 game seconds per real second

`FUN_004eddb0` (`GameTime_t::RunFunction`, the per-frame update — reached as a data reference, it is a signal
callback):

```c
fVar1 = dt * rate[0xBC] * wireRate[0xD4] * phaseRate;   // phaseRate = +0xC0 (day) or +0xC4 (night)
this[0x38] += (float)this[0x74] * fVar1;                // +0x74 == 5
```

So with every rate at its 1.0 default the clock advances **5 game seconds per real second**, and a game day of
86400 game seconds takes **86400 / 5 = 17280 real seconds = 4.8 real hours**.

Note `phaseRate`: **day and night have separate multipliers** and can run at different speeds (both default to
1.0). The selector is a dawn/dusk window built from `+0xA8`…`+0xB4` (19h + 4h, 7h + 2h) interpolated by a
global. Not modelled by us — recorded because it means "the cycle" is not necessarily uniform.

## The wire record

`[u32 TypeTag=0][u32 Handle=0][u64 dayTime][u8 flag][u64 timestamp][f32 rate]`, 29 bytes.

### The reader — `GameTimeIIR_t::ReadSubClass` (`FUN_005bde10`, vtable slot 8)

Reached with `CLASS=GameTimeIIR_t … VtReader.java` (the leaf-name searches never find `::vftable`). It is five
straight big-endian `FUN_0048f900` (ReadU32) calls into the IIR object, and the apply `FUN_005bdf70` (slot 2)
then forwards four of the five to the clock:

| field | wire word | forwarded as |
|---|---|---|
| `+0x1C` | dayTime **high** | `newTime` — the game-second count |
| `+0x20` | dayTime **low** | **not forwarded** — the client discards the fraction |
| `+0x24` | timestamp **high** | `p3` → stored at `clock+0x64` (the game-day counter) |
| `+0x28` | timestamp **low** | `p4` → the `- __time64(NULL)` skew |
| `+0x2C` | rate | `p5` → `clock+0xD4` |

Two things fall out of this:

- **the seconds live in the HIGH word and the fraction is ignored**, so emitting whole seconds with a zero low
  word is exactly what the client consumes;
- **`flag` is not a clock field.** `ReadSubClass` consumes only those five u32s, so the mid-record byte is read
  by the base IIR class and never reaches `GameTime_t` - which is why its 0/1/2/3 spread does not correlate with
  time of day. It is a generic IIR field, not a day/night selector.

Field roles, from the IIR handler `FUN_004ee710(this, float newTime, p3, int p4, p5)`:

- **`dayTime`** → `newTime`. 32.32 fixed point: game seconds in the high word, a fraction in the low word (19
  of 141 captured records carry `0x80000000`, i.e. half a second — so the ticket-era claim that the low word is
  always 0 was wrong). The fraction is read but never forwarded.
- **`timestamp`** → its two halves split: the low word is `p4`, and the handler does
  `this[0xD0] = p4 - __time64(NULL)`, comparing the field against **current unix time** and storing the skew;
  the high word is `p3`, parked at `clock+0x64`. Independent client-side confirmation of the encoding below.
- **`rate`** → feeds `+0xD4`, one of the multipliers in the tick above.
- **`flag`** is a base-class field, not the clock's — see the reader table above.

### `timestamp` = `[u32 unix / 17280][u32 unix epoch seconds]`

From all 141 captured records:

- the **low word** matches the capturing frame's own `TimeUtc` for **141/141**, running 0.2–22.0 s behind it
- the **high word** equals `low / 17280` for **140/141**; solving for the period directly as `low / high` over
  every sample gives **17280.001 … 17280.169**, and candidate periods 3600 / 21600 / 43200 / 86400 match
  **0/141**

The single miss (`DarkTemplar-login`) is off by −1 with its low word 155 s past a period boundary — a field
change, whose two halves straddle it, rather than a clock tick.

That 17280 s period is derived with no reference to the client, and it is exactly `86400 / 5`. The high word is
therefore the **game-day counter**.

## What retail's dayTime does NOT do

It is not a global monotonic clock, and it is not phase-locked to wall time:

- it runs **backwards** between captures, and even *within* one continuous capture
  (`Hakotep_hox_login_gameplay0`: 21456915 → 21457484 → 21459957 → **21458914**)
- `(unix % 17280) * 5` does not reproduce the captured values

Both fit dayTime being **per-playfield** — expected, since Tortage has separate day and night instances of the
same zone (one capture is named `move-from-night-to-day`). So a server may choose its own phase; ours locks it
to the wall clock so it is deterministic across restarts and rolls over when the anchor's game-day counter
ticks.

## Dead ends, recorded so they are not re-walked

- **`86400` in the client is mostly real time.** Of its non-CRT uses, `FUN_00531290` is a d/h/m/s countdown
  formatter (the `GuildNameWaitTime` path) and `FUN_01305660` is an hour-of-day `[start,end)` gate that looks
  exactly like a day/night phase check — until its only caller `FUN_01303c90` turns out to build its time from
  `__time64` + `localtime64` + `mktime64`. Real local time, a live-event schedule. `17280` is not a literal
  anywhere.
- **The IIR identity is not a static anchor.** `0x4F57DC08` appears neither as a code immediate nor as a 4-byte
  value in initialised memory, and the type name is a BKDR-31 *preimage*, so hash and name searches both come
  up empty. The string `"GameTimeIIR_t"` *is* present (`0x01F943EC`, xrefd by `FUN_004e4c20` /
  `FUN_005BDD60`) — but `FindStr.java` misses it while `FindSym.java` finds it, so prefer the symbol search for
  anything that is defined data.
