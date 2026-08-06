# ApplySpell / MagicBuffIIR payload decode (#232)

The packet NPCs (and players) cast is **ApplySpell** (`0x6B87E316`, = `MagicBuffIIR` in `Opcodes.cs`). Its IIR
carrier content is:

```
[u32 len][u32 0x6B87E316][u32 c350][u32 applyTo][u16 0][u32 spellId][u32 c350][u32 applyFrom][PAYLOAD]
```

`applyFrom` is the caster (an NPC handle for NPC casts, the player for self-buffs/offensive casts), `applyTo`
the target. The **54-byte PAYLOAD** is the `MagicBuffIIR` effect descriptor.

## It is a BUFF descriptor, not raw damage
Ghidra `MagicBuffIIR_c::ReadSubClass` (`FUN_007a12a0`, `tools/ghidra/DumpMagicBuffReader.java`) reads a type
byte (`+0x7c`, 0 or 2) then a sequence of `ReadU32`s + a 7-word sub-block (`FUN_00897b60`) + a `ReadU64`. The
class's `Activate` logs `"MagicBuffIIR_c::Activate, UpdateBuff with parameters: BuffTemplate ..."` — so the
payload is a **buff/effect record** (template + duration + flags), NOT a damage number. The actual
HP change rides a separate vitals `StatUpdate` (`0x96B8DC59`), as combat already does.

## The CLIENT's own field names (#438)
`MagicBuffIIR_c::Activate` (`FUN_007a1580`, vtable slot 2 — `tools/ghidra/DumpMagicBuffConsumers.java`) builds a
`GameCode/Magic` log line whose format string names six fields, and every one of them is an offset the reader
above filled:

```
MagicBuffIIR_c::Activate, UpdateBuff with parameters:
  BuffTemplate <+0x2C>  CasterID <+0x3C>  CastingLevel <+0x38>
  Counter <+0x30>  CounterType <+0x34>  CounterTimeLeft <+0x78 / 100>
```

`CasterID` is `+0x3C`, not `+0x38`: the buff-add call takes `in_ECX + 0x3c` **by pointer**, i.e. the two-word
`ID32` the wire carries as `[u32 0xC350][u32 applyFrom]`. That leaves `+0x38` — the payload's `@42` word — as
`CastingLevel`, which is what `MagicBuffDataCodec` already sends the caster's level in.

## Reading order, and where the record really starts
The client's IIR base read consumes `[u32 0xC350][u32 handle][u8 env]` — **nine** bytes, the same carrier
`TeleportCodec` writes — so `ReadSubClass` starts one byte into what this doc has always drawn as `[u16 0]`:

| content offset | client field | our builder writes |
|---|---|---|
| `0..8` | IIR base carrier (`0xC350`, `applyTo`, `env`) | `[u32 c350][u32 applyTo]` + the first byte of `[u16 0]` |
| `9..12` | **`BuffTemplate`** `+0x2C` | the second `[u16 0]` byte + the top 3 bytes of `spellId` = `spellId >> 8` |
| `13` | **form byte** `+0x7C` | the LOW byte of `spellId` |
| `14..21` | **`CasterID`** (`ID32` at `+0x3C`/`+0x40`) | `[u32 c350][u32 applyFrom]` |
| `22..` | the payload below | |

**The low byte of the wire spell id is the FORM, not a rank.** The reader switches three ways on it and reads a
different number of bytes in each: `0` = APPLY (58 more), `1` = END (nothing more), `2` = UPDATE (20 more).
Checked across all 111 `Captures/*.tshark.json`: `form=0` → 54-byte tail ×1938 and `form=1` → empty tail ×982,
with **zero exceptions** — an exact functional dependency, and the same base ids appear under both, which is an
apply/end pair and not two ranks of one spell. Form 2 is never sent by retail.
[V1: tools/ghidra/DumpMagicBuffReader.java — the three-way switch on `+0x7C`]

## Field map (54-byte payload, form 0)
Framed the way the CLIENT reads it. The byte-level offsets the earlier empirical diff recorded (`@10`, `@23`,
`@29`, `@45`) are the low bytes of these u32s, so both readings agree — but only this one says where a field
ENDS, which is what a builder needs.

| Offset | Client field | Notes |
|---|---|---|
| `@0` | u32 **`Counter`** `+0x30` | we send 0; meaning undecoded, but `Activate` skips the whole update when it is 0 |
| `@4` | u32 **`CounterType`** `+0x34` | we send 0 |
| `@8` | u32 **`Duration`** `+0x44` | centiseconds — `Activate` does `* 10` to get ms. The early "`@10` u16" is this word's low half. |
| `@12` | bool flag `+0x48` | passed to the buff-add |
| `@13` | bool flag `+0x49` | passed to the buff-add (the "flag A" of the empirical map) |
| `@14–41` | **7-word sub-struct** `+0x4C` | read by `FUN_00897b60`, passed to the buff-add BY POINTER. Wire words land in slots `[0],[2],[1],[3],[8],[4],[5]` — so `@22` → slot `[1]` = the spell base id again, and `@26` → slot `[3]`. |
| `@42` | u32 **`CastingLevel`** `+0x38` | the caster's level |
| `@46–53` | u64 **expiry stamp** `+0x70` | NOT a nonce — see below |

**`@46–53` is an absolute expiry, not a counter.** `Activate` computes `(stamp64 - <global clock>) / 10` and, if
that is positive and *shorter* than `Duration`, overwrites `Duration` with it. That is how retail resumes a buff
that is already partly elapsed (relog, or an entity streaming in mid-buff). It is also why the earlier note said
"the client does NOT validate it": the synthetic "effect nonce" we used to write simply fails the
`0 < x <= Duration` test and is ignored.

### The clock is our own pong's uptime counter (#480)

That `<global clock>` is not an unknown — **it is a value only WE can have put there**, and we already send it.

- The global is the u64 at `+0x48` of a `ServerTime_c` singleton (`DAT_02d27428`). Its module ctor
  (`FUN_007a0d60`) zeroes it and connects one slot to one signal; that slot, `FUN_007a0ea0`, is the **only**
  writer:

  ```c
  this[0x48] = (__int64)incoming / 1000;   // µs -> ms
  this[0x40] = timeGetTime();
  this[0x50] = (double)this[0x48] * 0.001; // the same value in seconds
  ```

- The signal's emitter is `GamecodeClient_t` vtable slot 131 → **`DispatchPong`** (GameCharInterface `0x2082`),
  the reply to the client's `LatencyCheck` (`0x2085`) poll. Its body is
  `[u32 echoedCounter][u32 uptimeHi][u32 uptimeLo]` — a 64-bit **microsecond** uptime counter (`KeepAliveService`,
  already corpus-proven to advance at wall-clock rate and to read as the retail server's uptime).

So the client's clock is **the server's uptime in milliseconds**, and the expiry stamp is
`uptimeMs + durationMs`. `Activate` reads the raw field rather than an accessor that adds elapsed time, so the
comparison is always against whatever the **last pong** carried.

**Verified over the corpus, not just read out of the client.** Across the 94 captures carrying both a pong and a
54-byte apply, `stamp − (uptimeMs + duration_cs × 10)` has a **median of +6 ms**, lands within 250 ms for
**2551 of 2929** applies and within 5 s for 98.6% of them; the residue is captures spanning a reconnect, where
the nearest pong belongs to a different server instance. Two independent captures of the same retail session
agree to 92 seconds on when the mount buff was applied (see the `NpcSpellCastCodecTests` cross-check below).
[V1: tools/ghidra/DumpServerTimeClock.java] [V1: Faolan.Tests/MagicBuffExpiryStampTests.cs]

**What we send now.** `ServerUptimeClock` owns the one origin both senders read — the pong in microseconds, the
stamp in milliseconds — and `MagicBuffDatas.ApplyNow` sources it. The buff RESTORE path puts the **remaining**
time in `Duration`, and that is not an interim: **it is the shape retail sends.**

[V1: Faolan.Importer/Cli/ReportCli.cs — `--report buff-durations` prints each spell's catalogue duration beside
every duration retail actually put on the wire. **51 of 256 spells carry at least one below-full value**, and they
are exactly the long ones a session can span: `Contract of Protection (Rank 1)` (full 1,440,000 cs) appears as
1,180,241 / 1,195,185 / 1,213,465 / … / 1,440,000; `XP Boost (4h)` as 1,268,055; `Critical Rating Boost (4h)` as
431,043; `Aura of Dread Fury (Rank 5)` as 910,653. Short buffs (`Wrath` 1000, `Chill` 600) only ever send full,
because they are never alive across a restore. The captured world-entry mount restore is the same story on one
spell: 8,509,794 cs against a fresh cast's 8,640,000, i.e. 21.7 minutes elapsed.]

⚠️ **So do not "finish" this by sending the full duration plus a true expiry and letting the client shorten it.**
That shape appears nowhere in the corpus. Gitea #601 proposed exactly that, gated on a "has this session answered
a pong yet" flag, and was **closed on this measurement rather than implemented** — building it would have put a
packet on the wire retail never sends. The stamp is still sourced from the same remaining time, which as a
side-effect keeps the restore correct before the client has learned our clock at all: it learns that only from a
`DispatchPong`, and world entry can precede the first `LatencyCheck`.

What retail's absolute stamp is genuinely *for* is a different case we do not cover yet — a buffed entity
STREAMING INTO VIEW mid-buff, where the observer has no apply event to count from. Nothing emits another entity's
active buffs at spawn today; tracked separately.

**`@26` stays open [V0].** It sits in the descriptor beside the spell id and tracks `CastingLevel` closely but not
exactly across the captures (1563 equal, 283 below, 92 above), so a second input exists. What the read DOES settle
is that neither `@26` nor `@42` is a rendered magnitude or a stack count — the question #438 was opened to answer.

## The EMPIRICAL map, and how it lines up
Before the client's reader was walked, the payload was recovered by diffing same-spell instances (per-cast
fields) against different spells (spell params), and modelled as `NpcSpell` columns in #304
(`Faolan.Core.Network/Codecs/Combat/MagicBuffDataCodec.cs`, byte-exact over all 21 distinct captured payloads —
`Faolan.Tests/NpcSpellEffectColumnsTests.cs`, which is where the proof lives now that the Python dissector that
did the original diff has been retired). Every one of its offsets is the **low byte** of a u32 in the table
above, so the two readings agree everywhere; the column names are what changes:

The columns have since been RENAMED to the client's reading (#480): `NpcSpell.FlagB/Magnitude/FlagC/Rank` are now
`DescriptorSlot0/DescriptorSlot3/DescriptorSlot8/CastingLevel`, and the old `Counter` is gone — it was the low half
of the u64 expiry stamp, which is session state rather than per-type content.

⚠️ If you ever redo that rename on another table, note that EF's scaffolder paired the old and new names
**alphabetically** and got three of the four backwards; the migration had to be hand-corrected offset-for-offset,
and the wrong version migrates and builds cleanly while transposing the bytes.

| empirical | client | reading |
|---|---|---|
| `@10–11` u16 `Duration` | `@8` u32 `Duration` | same field, low half — the wide read is the client's |
| `@13` flag A | `@13` bool `+0x49` | confirmed a bool, not a small int |
| `@17` flag B (1/2) | sub-struct slot `[0]`, low byte | a descriptor word, not a payload flag |
| `@23–26` u32 spell id | `@22` u32 = sub-struct slot `[1]` | reads as the full wire id only because `@26`'s top byte is 0 |
| `@29` byte `Magnitude` | `@26` u32 = sub-struct slot `[3]`, low byte | **not a magnitude** — see the `[V0]` note above |
| `@33` flag C | sub-struct slot `[8]`, low byte | a descriptor word |
| `@45` byte `Rank` | `@42` u32 `CastingLevel`, low byte | **not a rank** — the caster's level |
| `@50–53` u32 counter | the LOW half of the `@46` u64 expiry stamp | not a counter, and not standalone |

The `NpcSpell` columns still round-trip byte-exact, so nothing was ever broken by this — but `Magnitude` and
`Rank` were misnomers, and a builder that treats `@50` as an independent u32 is writing half of a 64-bit field.
Both halves of #480 have since landed: the columns carry the client's names, and the stamp is sourced rather
than invented (so the per-application "effect nonce" — and the `CharacterBuff.Nonce` column that persisted it —
are gone; a restore recomputes the stamp from `ExpiresAt`).

## The apply / end pair
Every cast arrives as a pair of ids differing only in their low byte: **form 0** carries the full 54-byte
payload (the apply), **form 1** carries an **empty** payload — the buff-end marker (the same apply-then-end
shape as the mount buff `…01 empty = dismount`). So a caster's real cast is the form-0 apply and the form-1 is
its terminator. This was read as a `(base<<8)|rank` convention with "rank 0" and "rank 1"; it is the client's
form byte (see above), and the ids pair up as one spell's start and stop rather than two ranks of it.

## What this gives #232
- The cast packet is fully understood and `MagicBuffCodec` reproduces it byte-exact.
- The 54-byte payload is decoded into `NpcSpell` columns (`Duration`, `Magnitude`, `Rank`, `FlagA/B/C`) and the
  sim **builds** the wire payload from them via `MagicBuffDataCodec` (#304) — there is no stored blob any more. The
  value at `@10` that the early decode read as a u32 "magnitude" is actually the **Duration** (centiseconds; the
  `@10` u16 read big-endian off a zeroed high half, e.g. Poacher 400 = 4s), which is why the raw 400–4000 range
  is durations, not HP. The value at `@50` is not stored — it is the low half of the client's expiry stamp,
  which is session state (the server's uptime at the buff's end) rather than anything about the spell.
- Importing only the form-0 (non-empty) row per spell is the clean form (the form-1 is a terminator, not a
  standalone cast).

## In-client VERIFIED (2026-06-17, #232 done)
A spawned caster casts at the player in-client. Repro: spawn a Poacher Marksman (a caster with an `NpcSpells`
row, spell id `0x316DCB00`), engage it → it follows and applies its debuff via `MagicBuffIIR (0x6B87E316)`,
applyFrom = the NPC handle, spell `0x316DCB` ("Ripper Volley") — it casts instead of meleeing.
**Fix that enabled the test:** `WorldBroadcaster.SpawnNpcForPlayer` (the summon/test-spawn path) was
a minimal spawn that NEVER loaded `entity.Spells` — only the zone-load path did — so a summoned caster could never
cast regardless of data. It now loads the NPC's `NpcSpells` rows like the zone path, so `IsCaster` lights up.
Caveat: the applied debuff doesn't expire (no duration honored — #67; "Ripper Volley" shows a 6s tooltip but
sticks). NpcSpells data is still sparse (~14 named casters); summoned test mobs also get no loot table.
