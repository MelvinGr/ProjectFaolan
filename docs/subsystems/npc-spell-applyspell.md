# ApplySpell / MagicBuffIIR payload decode

The packet NPCs (and players) cast is **ApplySpell** (`0x6B87E316`, = `MagicBuffIIR` in `Opcodes.cs`). Its IIR
carrier content is:

```
[u32 len][u32 0x6B87E316][u32 c350][u32 applyTo][u16 0][u32 spellId][u32 c350][u32 applyFrom][PAYLOAD]
```

`applyFrom` is the caster (an NPC handle for NPC casts, the player for self-buffs/offensive casts), `applyTo`
the target. The 54-byte PAYLOAD is the `MagicBuffIIR` effect descriptor.

## It is a buff descriptor, not raw damage
Ghidra `MagicBuffIIR_c::ReadSubClass` (`FUN_007a12a0`, `tools/ghidra/DumpMagicBuffReader.java`) reads a type
byte (`+0x7c`, 0 or 2) then a sequence of `ReadU32`s + a 7-word sub-block (`FUN_00897b60`) + a `ReadU64`. The
class's `Activate` logs `"MagicBuffIIR_c::Activate, UpdateBuff with parameters: BuffTemplate ..."` — so the
payload is a buff/effect record (template + duration + flags), not a damage number. The actual
HP change rides a separate vitals `StatUpdate` (`0x96B8DC59`), as combat already does.

## The client's own field names
`MagicBuffIIR_c::Activate` (`FUN_007a1580`, vtable slot 2 — `tools/ghidra/DumpMagicBuffConsumers.java`) builds a
`GameCode/Magic` log line whose format string names six fields, and every one of them is an offset the reader
above filled:

```
MagicBuffIIR_c::Activate, UpdateBuff with parameters:
  BuffTemplate <+0x2C>  CasterID <+0x3C>  CastingLevel <+0x38>
  Counter <+0x30>  CounterType <+0x34>  CounterTimeLeft <+0x78 / 100>
```

`CasterID` is `+0x3C`, not `+0x38`: the buff-add call takes `in_ECX + 0x3c` by pointer, i.e. the two-word
`ID32` the wire carries as `[u32 0xC350][u32 applyFrom]`. That leaves `+0x38` — the payload's `@42` word — as
`CastingLevel`.

## Reading order, and where the record really starts
The client's IIR base read consumes `[u32 0xC350][u32 handle][u8 env]` — nine bytes, the same carrier
`TeleportCodec` writes — so `ReadSubClass` starts one byte into what the carrier diagram above draws as `[u16 0]`:

| content offset | client field | our builder writes |
|---|---|---|
| `0..8` | IIR base carrier (`0xC350`, `applyTo`, `env`) | `[u32 c350][u32 applyTo]` + the first byte of `[u16 0]` |
| `9..12` | **`BuffTemplate`** `+0x2C` | the second `[u16 0]` byte + the top 3 bytes of `spellId` = `spellId >> 8` |
| `13` | **form byte** `+0x7C` | the LOW byte of `spellId` |
| `14..21` | **`CasterID`** (`ID32` at `+0x3C`/`+0x40`) | `[u32 c350][u32 applyFrom]` |
| `22..` | the payload below | |

**The low byte of the wire spell id is the form, not a rank.** The reader switches three ways on it and reads a
different number of bytes in each: `0` = APPLY (58 more), `1` = END (nothing more), `2` = UPDATE (20 more).
Checked across all 111 `Captures/*.tshark.json`: `form=0` → 54-byte tail ×1938 and `form=1` → empty tail ×982,
with zero exceptions — an exact functional dependency, and the same base ids appear under both, which is an
apply/end pair and not two ranks of one spell. Form 2 is never sent by retail.
[V1: tools/ghidra/DumpMagicBuffReader.java — the three-way switch on `+0x7C`]

## Field map (54-byte payload, form 0)
Framed the way the client reads it (the byte offsets of an empirical diff — `@10`, `@23`, `@29`, `@45` — are the
low bytes of these u32s; see the last table on this page).

| Offset | Client field | Notes |
|---|---|---|
| `@0` | u32 **`Counter`** `+0x30` | we send 0; `Activate` skips the counter update when it is 0 (see `UpdateBuff` below) |
| `@4` | u32 **`CounterType`** `+0x34` | we send 0 |
| `@8` | u32 **`Duration`** `+0x44` | centiseconds — `Activate` does `* 10` to get ms |
| `@12` | bool flag `+0x48` | `bool2` — whether the caster is a player; stored on the buff, see below |
| `@13` | bool flag `+0x49` | stored on the buff (the empirical map's "flag A") |
| `@14–41` | **7-word sub-struct** `+0x4C` | read by `FUN_00897b60`, passed to the buff-add BY POINTER. Wire words land in slots `[0],[2],[1],[3],[8],[4],[5]` — so `@22` → slot `[1]` = the spell base id again, and `@26` → slot `[3]`. |
| `@42` | u32 **`CastingLevel`** `+0x38` | a level — the target's, see the level pair below |
| `@46–53` | u64 **expiry stamp** `+0x70` | absolute, see below |

### The buff-add call, argument by argument — and where each payload field lands

`Activate`'s call into the magic holder is `[entity+0x2D8]`'s vtable slot 2. It is a ten-argument
`__thiscall`, and the call site (`007a164d..007a1671`) pushes:

| arg | pushed | what it is |
|---:|---|---|
| 1 | `&ESP+0x30` | an out slot |
| 2 | `[IIR+0x2C]` | `BuffTemplate` — the spell base id |
| 3 | `&IIR+0x4C` | the 7-word descriptor, by pointer |
| 4 | `[IIR+0x44]` | `Duration`, centiseconds |
| 5 | `0` | — |
| 6 | `&IIR+0x3C` | `CasterID`, the two-word `ID32`, by pointer |
| 7 | `[IIR+0x48]` | payload `@12` |
| 8 | `[IIR+0x49]` | payload `@13` |
| 9, 10 | `0`, `0` | — |

**The callee is `CoreMagicHolder::ActivateBuff` (`FUN_007eddc0`), not `UpdateBuff`** — the log string inside
`Activate` says `UpdateBuff`, but the vtable decides it: `+0x2D8` holds
`CoreMagicHolder`'s `+8` sub-object, whose vtable (`01fcdaf0`) has `FUN_007eddc0` in slot 2 — the function whose
own log strings are `"ActivateBuff missing SpellTemplate"` / `"Tried to ActivateBuff with a nonbuff spell"`.
`ActivateBuff` is the only caller of `CoreMagicHolder::AddBuffInfo` (`FUN_007eca90`), which allocates the
`BuffInfo_c` and stores the payload onto it:

| `BuffInfo_c` slot | receives | verified at |
|---|---|---|
| `+0x10` | the spell base id | `AddBuffInfo` body |
| `+0x14` | the apply INSTANT, centiseconds off the `GameTime_t` clock | `ActivateBuff` sources it from `FUN_004ed7d0()+0x40` |
| `+0x18` | duration × stat `0x1D0 DurationModifier`, the scaled total | `AddBuffInfo` body |
| `+0x24` | wire descriptor `[3]` — the caster's level | `007ee1a2` pushes `[desc+0xC]` as arg 4 → `*(buff+0x24) = arg4` |
| `+0x28/+0x2C` | `CasterID`, the two-word `ID32` | `AddBuffInfo` body |
| `+0x20` (u32) / `+0x30` (byte) | the payload's two bools `@12`/`@13` | `AddBuffInfo` body |
| `+0xA4` | wire descriptor `[0]` | `007ee196` pushes `[desc]` as arg 8 → `007ecb9c MOV [buff+0xA4],EDX` |

(The `@12`/`@13` ↔ `+0x20`/`+0x30` pairing between the two slots is read from the decompiler's argument
ordering, one step less certain than the rest; the two stores themselves are instruction-verified.)

The spawn scaffold's buff record independently fills the same `+0x24` slot, and the corpus measures that value as
the caster's level at cast time. Two wire encodings, one object slot, one meaning — which confirms the level-pair
assignment (`descriptor[3]` = caster, `CastingLevel@42` = target) from the client side.

### `UpdateBuff` (`FUN_007ee710`) is the counter path, and retail never drives it with a descriptor

`UpdateBuff`'s six arguments are the Counter family. `Activate` calls it at `007a1897` — only when payload
`@0 Counter != 0` (a form-0 apply falls through to it; the never-sent form 2 reaches it directly) — passing
`(BuffTemplate, Duration, CastingLevel@42, Counter@0, CounterType@4, CounterTimeLeft)`, and the store block
(`007eeb52..007eeb7f`) is `CastingLevel → buff+0x24`, `Counter → +0xAC`, `CounterType → +0xA8`,
`CounterTimeLeft → +0xC0`, with the `CMP [buff+0xAC],0 / JLE` gate being "no counter, nothing to update". Its three
other callers (`FUN_007f1d30`, `FUN_007f2340`, `FUN_007f32b0`) are the buff-counter GameAction executors. So on a
counter update the client overwrites `+0x24` with `CastingLevel@42` — it treats the two wire level slots as one
"casting level" field, refreshed from whichever encoding arrived last.

### `descriptor[0]` is write-only in the client — nothing reads `BuffInfo_c+0xA4`

No consumer names the 1-vs-2 split:

* the magic module's every `[reg+0xA4]` touch is attributed to OTHER objects (the spell record's own `+0xA4`
  property word, `+0xA0/+0xA4` vector begin/end pairs, `GameTime_t`, stack slots);
* all ten callers of `FindBuffInfo` (`FUN_007ed310`) read only the timer pair and `+0x20` (`FUN_007f17a0` is a
  `GetBuffBool` accessor returning `+0x20`; `FUN_007f16e0` computes time-remaining off `+0x14/+0x18`);
* the GUI exporter `FUN_00b50270`, which builds the Flash `com.GameInterface.Game.BuffData` object, reads
  `+0x10`, `+0x14/+0x18` (or `+0xBC/+0xC0` for spells with head-flags bit 20), `+0x2C`, and spell-record
  properties — not `+0xA4`;
* the spawn-scaffold serialization (`FUN_007ed9b0`) fills twelve fields and `+0xA4` is not among them — a
  streamed-in buff never has it;
* image-wide, exactly one `CMP dword ptr [reg+0xA4], 1/2/3` exists (`FUN_017d0ca0`) and it is a COLLADA physics
  exporter.

And the `BuffInfo_c` ctor (`FUN_00849770`) defaults `+0xA4` to 1 — the wire's majority value is the
client's own neutral state.

So `descriptor[0]` is a server-side provenance tag the client stores and never consumes: `1` = a plain cast
(the client default), `2` = applied by another spell's effect (the corpus's `d0=2` spells are 0/46 castable and
83% chain/area-effect targets), `3` = applied by a combo, naming it in `descriptor[1]`. A builder that writes
`1` for a direct cast — every apply this server performs — is correct by construction, and no value in this slot
can change client behaviour. An effect-chain application writes `2` (open work tracked in Gitea #784); a
combo-applied buff writes `3` + the combo id (open work tracked in Gitea #623).

**The same holds for the rest of the descriptor: the client consumes exactly two of its seven words.**
`ActivateBuff` reads only slots `[0]` and `[3]` off the pointer (`007ee196`/`007ee1a2` — offsets `0` and
`0xC`), `AddBuffInfo` receives those two by value, and no other `MagicBuffIIR_c` method touches the parsed
descriptor region (`IIR+0x4C..+0x6C`) at all — every vtable method decompiled, zero references. So the combo
id in `descriptor[1]`, the spell-id echo in `[2]`, the hostile flag at wire `@30` and the two zero words are
parsed and never read: server-side facts riding along, only recoverable from the corpus. There is no client consumer to name the `@30` hostile flag (`gaps.md`) either.

**`@46–53` is an absolute expiry, not a counter.** `Activate` computes `(stamp64 - <global clock>) / 10` and, if
that is positive and *shorter* than `Duration`, overwrites `Duration` with it. That is how retail resumes a buff
that is already partly elapsed (relog, or an entity streaming in mid-buff). A value that fails the
`0 < x <= Duration` test is ignored.

### The clock is our own pong's uptime counter

That `<global clock>` is a value only we can have put there, and we send it.

- The global is the u64 at `+0x48` of a `ServerTime_c` singleton (`DAT_02d27428`). Its module ctor
  (`FUN_007a0d60`) zeroes it and connects one slot to one signal; that slot, `FUN_007a0ea0`, is the only
  writer:

  ```c
  this[0x48] = (__int64)incoming / 1000;   // µs -> ms
  this[0x40] = timeGetTime();
  this[0x50] = (double)this[0x48] * 0.001; // the same value in seconds
  ```

- The signal's emitter is `GamecodeClient_t` vtable slot 131 → `DispatchPong` (GameCharInterface `0x2082`),
  the reply to the client's `LatencyCheck` (`0x2085`) poll. Its body is
  `[u32 echoedCounter][u32 uptimeHi][u32 uptimeLo]` — a 64-bit microsecond uptime counter (`KeepAliveService`,
  corpus-proven to advance at wall-clock rate and to read as the retail server's uptime).

So the client's clock is the server's uptime in milliseconds, and the expiry stamp is
`uptimeMs + durationMs`. `Activate` reads the raw field rather than an accessor that adds elapsed time, so the
comparison is always against whatever the last pong carried.

Verified over the corpus: across the 94 captures carrying both a pong and a
54-byte apply, `stamp − (uptimeMs + duration_cs × 10)` has a median of +6 ms, lands within 250 ms for
2551 of 2929 applies and within 5 s for 98.6% of them; the residue is captures spanning a reconnect, where
the nearest pong belongs to a different server instance. Two independent captures of the same retail session
agree to 92 seconds on when the mount buff was applied (see the `NpcSpellCastCodecTests` cross-check below).
[V1: tools/ghidra/DumpServerTimeClock.java] [V1: Faolan.Tests/MagicBuffExpiryStampTests.cs]

`ServerUptimeClock` owns the one origin both senders read — the pong in microseconds, the stamp in milliseconds —
and `MagicBuffDatas.ApplyNow` sources it. The buff restore path puts the remaining time in `Duration`, which is the
shape retail sends:

[V1: Faolan.Importer/Cli/ReportCli.cs — `--report buff-durations` prints each spell's catalogue duration beside
every duration retail actually put on the wire. 51 of 256 spells carry at least one below-full value, and they
are exactly the long ones a session can span: `Contract of Protection (Rank 1)` (full 1,440,000 cs) appears as
1,180,241 / 1,195,185 / 1,213,465 / … / 1,440,000; `XP Boost (4h)` as 1,268,055; `Critical Rating Boost (4h)` as
431,043; `Aura of Dread Fury (Rank 5)` as 910,653. Short buffs (`Wrath` 1000, `Chill` 600) only ever send full,
because they are never alive across a restore. The captured world-entry mount restore is the same story on one
spell: 8,509,794 cs against a fresh cast's 8,640,000, i.e. 21.7 minutes elapsed.]

So a restore does not send the full duration plus a true expiry for the client to shorten: that shape appears
nowhere in the corpus. The stamp is sourced from the same remaining time, which also keeps the restore correct before
the client has learned our clock at all: it learns that only from a `DispatchPong`, and world entry can precede the
first `LatencyCheck`.

### The stream-in re-assert

What retail's absolute stamp is genuinely *for* is the case an observer arriving mid-buff has: no apply event to
count from. Retail sends it; `--report buff-on-spawn` shows the shape.

Over 17,717 captured spawn bodies and 2,929 applies the corpus holds exactly one, and it is unique in three
independent ways at once — which is what makes one sighting enough to build from rather than a lone data point:

```
NecroL80_..._login_spellweaving  +135.5s   to 0x0215E80C (a CHARACTER), spawned 0.00s earlier
Slayer of the Leviathus Spawn  18,735,004 cs of 276,480,000 cs (6.8%)
ApplyFrom     = 0    <- a CHARACTER reference whose handle is zero, the ONLY one in the corpus
descriptor[3] = 0    <- no caster named, so no caster level
CastingLevel  = 80   <- the TARGET's level
```

So it is a separate `MagicBuffIIR` right at the spawn, not a field of the spawn body — 0 of those 17,717
bodies carries a buff list — carrying the remaining time, exactly like the login restore.

**The zero-handle caster is not the class-0 "no caster" form.** `ApplyNoCaster` is the 11 applies that write a
zero class word and no reference at all (retail's Racer buff, [local-travel.md](local-travel.md)); this one writes the Character
class and a zero instance behind it. The 54-byte data region is identical between the two — same `Bool2 = 0`, same
`descriptor[3] = 0` — so they share the payload factory, but the carrier differs and `ApplyFrom` must be `0u`
rather than `null`.

`WorldBroadcaster.SendActiveBuffsOnStreamIn` emits this after every spawn, for every TIMED buff the entity holds.
**What one sighting does not settle is which buffs qualify** — 1 of 2,929 says it is not every buff on every
stream-in, and the one we have is a 32-day title buff. Open work tracked in Gitea #677.
[V1: Faolan.Tests/MagicBuffCorpusTests.cs — `TheCasterlessStreamInApply_RebuildsByteExact`]

**`@26` is the caster's level and `@42` is the target's.** Scope separates them without having to resolve a handle to a level: grouped by `(capture, caster)` `@26` varies on 20 of
1075 and `@42` on 36; grouped by `(capture, target)` `@26` varies on 48 of 1183 and `@42` on 6. `@42` is six
times more stable per target than per caster and `@26` the other way round. The corroboration is that 2,241 of the
2,929 applies are self-applies, where the two are one number by construction, and the agreement rate splits exactly
on that line (>80% self, <50% other) — which a caster/target pair predicts and a caster/spell pair would not.
281 self-applies still disagree, 238 of them on one spell (3911037), so the fields keep their client names.
[V1: Faolan.Tests/MagicBuffCorpusTests.cs — `TheTwoLevelSlots_AreTheCastersAndTheTargets`]

### The descriptor's own words: `--report buff-descriptor`

The seven-word sub-struct at `@14` is `[descriptor0, descriptor1, spellBaseId, casterLevel, descriptor4, 0, 0]`
in wire order. Measured over all 2,929 captured applies:

**`descriptor[1]` is the combo that applied the buff, and `descriptor[0] == 3` marks it.**
`descriptor[0] == 3` and `descriptor[1] != 0` are the *same two applies* — 2 of 2 in both directions, no
exceptions. Both carry `descriptor[1] = 3165331`, which is a `Combos.Id`:
`combo_npc_guardian_shield_70_decisive_opening`, on the buff `Decisive Opening` (3315223). So `3` is the combo-applied form, naming its source combo in `descriptor[1]`.

**`descriptor[4]` is a boolean, not a value.** Its only non-zero value is `1`, on 134 applies — and on 0 of the
2,241 self-applies. The ten spells that carry it are hostile debuffs and DoTs (`Pestilential Blast (Rank 6)`,
`Flesh to Worms (Rank 5)`, `Mark of the Devourer`, `Repenting`, `Poisoned`, `Ice Shackle`), so it reads as a
hostile/debuff marker on a cast at someone else. It is not merely "not self": 554 of the 688 non-self applies
leave it clear. [V0: what separates the 134 from those 554]

### `bool2` is whether the caster is a player

Not a constant, and not a property of the spell at all. Over all 3,424 captured applies in the dumps it splits
with no exceptions: 1 on all 2,090 whose caster is
a player, 0 on all 1,334 whose caster is a creature or absent. Of the 159
casters that cast more than one distinct spell, zero vary `bool2`, while 4 of the 128 spells cast by more than
one caster do, so the field belongs to the caster and not to the spell.

A no-caster apply carrying 0 is the same rule rather than a third case.

`MagicBuffDatas.Apply` takes `casterIsPlayer` and every call site states it, answered from the world model
(`CasterIsPlayer`) rather than from the handle's shape, because retail's split is by what the caster
IS and a handle-range test would be a coincidence of this corpus's own id allocation.
[V3: MagicBuffCorpusTests.Bool2_IsWhetherTheCasterIsAPlayer]

**`descriptor[0]`'s 1-vs-2 split is not in the spell's RDB record.** It is nearly per-spell (varying within a
spell on 7 of 256), but `--report buff-descriptor --report-arg rdb` sweeps, over the 248 unsplit spells with a
readable 1040005 record:

| swept | best purity | baseline |
|---|---|---|
| the 20-word fixed head, by value | 93.5% at word 13 (80 distinct values) | 83.5% |
| the fixed head, bit by bit | 83.5% — nothing above the baseline | 83.5% |
| every property field, by value | 100% only on fields carrying ≤16 records | 83.5% |
| every property field, bit by bit | same, all on tiny populations | 83.5% |
| every word of the common 36-word prefix | 97.2% at word 23, 119 distinct values | 83.5% |

Nothing coarse separates it (the same sweep finds the spell record's mana mode at a single bit, so the method works
when the answer is there). Nor does any `Spells` column we hold (16 candidates, all at baseline) or catalog
membership (`ClassSpells`, `Combos.GameActionId`, `SpellEffects`, `SpellStatEffects`, `NpcSpells`, `FeatSpellGrants` —
all at baseline). It is not an arity count (its `2` form populates no extra descriptor slot, unlike `3`), not a
re-apply/refresh marker (it appears on both the first and later applies of one target+spell), and not the
player-ness of either endpoint. Since the client never reads it (see the write-only section), no spell field needs
to separate it. The `descriptor[0] == 2` set is all combo- or proc-applied by name: `Wrath` (21 ranks), `Spiritual
Torment`, `Furious Inspiration`, `Mutilation`, `Chill`, `Momentum`, `Blessed Blade`, `Renewal`, `Ripping Revenge`,
`Sacrarial Guard`, `Covenant of Vengeance`, `Stunned`.

### The buff object is `BuffInfo_c`, and it is a plain data struct

`CoreMagicHolder::AddBuffInfo` (`FUN_007eca90`) allocates it inline —

```
PUSH 0xc8 ; CALL FUN_00c9cce0        ; 200 bytes
MOV  ESI,EAX ; CALL FUN_00849770     ; the constructor
```

— and that constructor installs the vtable at `0x1fd2b78`, whose RTTI CompleteObjectLocator names
`.?AVBuffInfo_c@@`. Its base is `PtrTarget_c` (vtable `0x200cde4`), which is why `AddBuffInfo`'s `param_2` is a
`Ptr` and every field write goes through `*param_2`.

`BuffInfo_c`'s vtable has 7 slots and all seven are `PtrTarget_c`'s refcount interface: a plain data struct with no
virtual accessors, so there is no method set to sweep for a field's consumer. To find readers of a field, scope the
offset sweep to `CoreMagicHolder`'s own band (`0x007e8000`–`0x007f8000`): 13 functions touch `+0x20` there. Four of
them write it as a byte, which is a different object (`AddBuffInfo` stores `bool2` as a u32).

Two functions look like `+0x20` consumers and are other objects wearing the same offset: `FUN_007eaf40` iterates a
pointer vector and tests `*(int *)(elem + 0x20) == 0 || == 6`, but the same element's `+0x18` is compared against
50000, an LDB category, while `BuffInfo_c`'s `+0x18`/`+0x1c` are written from the duration path; and
`FUN_007f1760`.

### `BuffInfo_c` has its own serialization pair, and `+0x20` is field 4 of it

`FUN_007ed9b0` allocates a `BuffInfo_c` — the same
`FUN_00c9cce0(200, 4, 0)` + `FUN_00849770` pair — and then fills it from a stream cursor (`FUN_0048f900()`,
called once per field) in offset order:

```
+0x14  +0x18  +0x1c  +0x20  +0x24  (+0x28 +0x2c)  +0xa8  +0xac  +0xb8  +0xbc  +0xc0
```

`FUN_007edac0` and `FUN_007f17a0` are the writer side of the same layout — `FUN_007edac0` loads `+0x14`,
byte-swaps it when a flag byte on the writer is set, and hands it to `FUN_00cba060(&value, 4)`.

That stream is the character full-update's buff list, grouped by spell —
`[outer ListFrame] N × { [u32 spellId][inner ListFrame] M × [u32 discarded][12 fields] }` — modelled as
`ScaffoldBuffGroup`/`ScaffoldBuff` with each field's client offset documented on the type. The corpus holds 1,471
records. `+0x18` (`TimerTotal`) is computed on the apply path — duration × stat `0x1D0 DurationModifier` × a
constant — so the scaffold carries the same scaled total precomputed, and `+0x1c` gets the unscaled duration.

### What `descriptor[0]` is not, and what the numbers favour

**`descriptor[0]` gates nothing in the payload.** Asked of all 54 bytes (not just the near-zero slots `[1]` and
`[4]`, which cannot see a byte populated on both values that merely differs), the only one whose value sets are disjoint between `d0=1` and
`d0=2` is **byte @17 — descriptor[0]'s own low byte**. So the arity reading holds for `3` (which really does
gate `descriptor[1]`, 2 of 2) and for nothing else.

**It is not a per-cast stack or running count**, which the `Furious Inspiration (6)` / `Mutilation (1)` style
of name invites. Over the 397 re-application runs (same capture, same target, same spell, 2+ applies) it is
constant on 394, and the three that vary do not ramp — `2,1`, `1,1,2,1`, `2,2,2,1,2,1`.

**What does separate is provenance, measured as a rate over applies rather than as a partition's purity** — which
a purity sweep cannot see: against an 83.5% baseline a purity score needs a
near-perfect partition, and a 2× rate lift scores at the baseline. The cut the membership sweep did not include
is `SpellAreaEffects.EffectSpellId` — "can this spell be reached as another spell's effect at all":

| | `d0=1` | `d0=2` | `d0=3` |
|---|---:|---:|---:|
| reachable as another spell's effect | 35.3% | 52.0% | 100% |
| castable (a `ClassSpells` row) | 16.7% | 4.1% | 0% |
| only reachable indirectly | 25.7% | 48.2% | 100% |

So the reading with numbers behind it is an apply-source tag — `1` a direct cast, `2` applied by another effect,
`3` applied by a combo with `descriptor[1]` naming it. A rate lift is a lead, not a decode: none of these is a
partition, and `MagicBuffDatas.Apply` writes `1`.

**`bool2 = 0` is essentially never a castable spell**, per apply rather than per spell:

| | `bool2=1` | `bool2=0` |
|---|---:|---:|
| castable (a `ClassSpells` row) | 25.0% | 0.2% |
| carries a `SpellStatEffect` | 65.6% | 96.3% |
| reachable as another spell's effect | 49.3% | 18.0% |

3 applies of 1,334 at `bool2=0` are of a castable spell, against 523 of 2,090 at `bool2=1` — a 125× lift — and
the `bool2=0` population is almost entirely stat-effect-bearing, mana-free, recast-free and range-free. That is
the profile of a passive or derived effect rather than a cast, and it agrees with the casterless form:
the casterless apply (`ApplyNoCaster`, retail's local-travel "Racer") carries `bool2 = 0` on all 11.

Reproduce all of it with `--report buff-descriptor`.

## The empirical map, and how it lines up

An empirical diff of same-spell instances (per-cast fields) against different spells (spell params) models the
payload as `NpcSpell` columns (`Faolan.Core.Network/Codecs/Combat/MagicBuffDataCodec.cs`, byte-exact over all 21
distinct captured payloads — `Faolan.Tests/NpcSpellEffectColumnsTests.cs`). Every one of its offsets is the low byte
of a u32 in the client's table, and the columns carry the client's names: `DescriptorSlot0`, `DescriptorSlot3`,
`DescriptorSlot8`, `CastingLevel`, `Duration`, `FlagA`. The expiry stamp is session state, not a column.

When renaming columns like these, check the migration by hand: EF's scaffolder pairs old and new names
alphabetically, and a transposed pairing migrates and builds cleanly while moving bytes between fields.

| empirical | client | reading |
|---|---|---|
| `@10–11` u16 `Duration` | `@8` u32 `Duration` | same field, low half — the wide read is the client's |
| `@13` flag A | `@13` bool `+0x49` | a bool, not a small int |
| `@17` (1/2) | sub-struct slot `[0]`, low byte | `DescriptorSlot0` — a descriptor word, not a payload flag |
| `@23–26` u32 spell id | `@22` u32 = sub-struct slot `[1]` | reads as the full wire id only because `@26`'s top byte is 0 |
| `@29` byte | `@26` u32 = sub-struct slot `[3]`, low byte | `DescriptorSlot3` — the caster's level, not a magnitude |
| `@33` | sub-struct slot `[8]`, low byte | `DescriptorSlot8` — a descriptor word |
| `@45` byte | `@42` u32 `CastingLevel`, low byte | the target's level, not a rank |
| `@50–53` u32 | the low half of the `@46` u64 expiry stamp | not a standalone counter |

The `@8` duration is centiseconds (Poacher 400 = 4 s), which is why the raw 400–4000 range is durations, not HP.

## The apply / end pair

Every cast arrives as a pair of ids differing only in their low byte: form 0 carries the full 54-byte payload (the
apply), form 1 carries an empty payload — the buff-end marker (the same apply-then-end shape as the mount buff
`…01 empty = dismount`). So a caster's real cast is the form-0 apply and the form-1 is its terminator; the ids pair
up as one spell's start and stop, not two ranks of a `(base<<8)|rank` id. The importer keeps only the form-0 row per
spell.

## NPC casting

`MagicBuffCodec` reproduces the cast packet byte-exact, and the simulation builds the wire payload from the
`NpcSpell` columns via `MagicBuffDataCodec` — no stored blob. Verified in-client: spawn a Poacher Marksman (a caster
with an `NpcSpells` row, spell id `0x316DCB00`), engage it → it follows and applies its debuff via `MagicBuffIIR
(0x6B87E316)`, applyFrom = the NPC handle, spell `0x316DCB` ("Ripper Volley") — it casts instead of meleeing. An
entity is a caster (`WorldEntity.IsCaster`) when its `NpcSpells` rows are loaded, which both the zone-load path and
`NpcBroadcaster.SpawnNpcForPlayer` do. `NpcSpells` data is sparse (~14 named casters).
