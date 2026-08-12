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

### The STREAM-IN re-assert (#677)

What retail's absolute stamp is genuinely *for* is the case an observer arriving mid-buff has: no apply event to
count from. **Retail does send it, and `--report buff-on-spawn` is what settled the shape.**

Over 17,717 captured spawn bodies and 2,929 applies the corpus holds **exactly one**, and it is unique in three
independent ways at once — which is what makes one sighting enough to build from rather than a lone data point:

```
NecroL80_..._login_spellweaving  +135.5s   to 0x0215E80C (a CHARACTER), spawned 0.00s earlier
Slayer of the Leviathus Spawn  18,735,004 cs of 276,480,000 cs (6.8%)
ApplyFrom     = 0    <- a CHARACTER reference whose handle is zero, the ONLY one in the corpus
descriptor[3] = 0    <- no caster named, so no caster level
CastingLevel  = 80   <- the TARGET's level
```

So it is a **separate `MagicBuffIIR` right at the spawn, not a field of the spawn body** — 0 of those 17,717
bodies carries a buff list — carrying the **remaining** time, exactly like the login restore.

⚠️ **The zero-handle caster is not the class-0 "no caster" form.** `ApplyNoCaster` was written for the 11 applies
that write a zero CLASS word and no reference at all (retail's Racer buff, #636); this one writes the Character
class and a zero instance behind it. The 54-byte data region is identical between the two — same `Bool2 = 0`, same
`descriptor[3] = 0` — so they share the payload factory, but the carrier differs and `ApplyFrom` must be `0u`
rather than `null`.

`WorldBroadcaster.SendActiveBuffsOnStreamIn` emits this after every spawn, for every TIMED buff the entity holds.
⚠️ **What one sighting does NOT settle is which buffs qualify** — 1 of 2,929 says it is not every buff on every
stream-in, and the one we have is a 32-day title buff. Open work tracked in Gitea #677.
[V1: Faolan.Tests/MagicBuffCorpusTests.cs — `TheCasterlessStreamInApply_RebuildsByteExact`]

**`@26` is the CASTER's level and `@42` is the TARGET's** — the "second input" the earlier note suspected. SCOPE
separates them without having to resolve a handle to a level: grouped by `(capture, caster)` `@26` varies on 20 of
1075 and `@42` on 36; grouped by `(capture, target)` `@26` varies on 48 of 1183 and `@42` on **6**. `@42` is six
times more stable per target than per caster and `@26` the other way round. The corroboration is that 2,241 of the
2,929 applies are SELF-applies, where the two are one number by construction, and the agreement rate splits exactly
on that line (>80% self, <50% other) — which a caster/target pair predicts and a caster/spell pair would not.
⚠️ 281 self-applies still disagree, 238 of them on one spell (3911037), so the fields are not renamed yet.
[V1: Faolan.Tests/MagicBuffCorpusTests.cs — `TheTwoLevelSlots_AreTheCastersAndTheTargets`]

### The descriptor's own words: what `--report buff-descriptor` settles (#658)

The seven-word sub-struct at `@14` is `[descriptor0, descriptor1, spellBaseId, casterLevel, descriptor4, 0, 0]`
in wire order, and three of those had no reading at all. Measured over all 2,929 captured applies:

**⭐ `descriptor[0]` is an ARITY/VARIANT tag, and `descriptor[1]` is the COMBO that applied the buff.**
`descriptor[0] == 3` and `descriptor[1] != 0` are the *same two applies* — 2 of 2 in both directions, no
exceptions. Both carry `descriptor[1] = 3165331`, which is a `Combos.Id`:
`combo_npc_guardian_shield_70_decisive_opening`, on the buff `Decisive Opening` (3315223). So the first word is
not a flag whose value happens to vary; it says how the rest of the descriptor is populated, and the `3` form
names its source combo. That reframes the whole field: a builder must not write a constant there.

**`descriptor[4]` is a BOOLEAN, not a value.** Its only non-zero value is `1`, on 134 applies — and on **0 of the
2,241 self-applies**. The ten spells that carry it are hostile debuffs and DoTs (`Pestilential Blast (Rank 6)`,
`Flesh to Worms (Rank 5)`, `Mark of the Devourer`, `Repenting`, `Poisoned`, `Ice Shackle`), so it reads as a
hostile/debuff marker on a cast at someone else. It is not merely "not self": 554 of the 688 non-self applies
leave it clear. [V0: what separates the 134 from those 554]

### ⭐ `bool2` is whether the CASTER IS A PLAYER (#811's corpus, #658)

Not a constant 1, and not a property of the spell at all — which is why every sweep below came back at baseline.
Over all **3,424** captured applies in the dumps it splits with **no exceptions**: 1 on all 2,090 whose caster is
a player, 0 on all 1,334 whose caster is a creature or absent. The scope statement is what found it — of the 159
casters that cast more than one distinct spell, **zero** vary `bool2`, while 4 of the 128 spells cast by more than
one caster do, so the field belongs to the caster and not to the spell.

A no-caster apply carrying 0 is the same rule rather than a third case, which is what `ApplyNoCaster` had already
written from the other direction.

`MagicBuffDatas.Apply` now takes `casterIsPlayer` and every call site states it; the listener answers it from the
world model (`CasterIsPlayer`) rather than from the handle's shape, because retail's split is by what the caster
IS and a handle-range test would be a coincidence of this corpus's own id allocation.
[V3: MagicBuffCorpusTests.Bool2_IsWhetherTheCasterIsAPlayer]

**⚠️ `descriptor[0]`'s 1-vs-2 split is NOT in the spell's RDB record, and that is now measured
rather than assumed.** It is nearly per-spell (varying within a spell on 7 of 256), which is what pointed
at the record in the first place — but `--report buff-descriptor --report-arg rdb` sweeps, over the 248 unsplit
spells with a readable 1040005 record:

| swept | best purity | baseline |
|---|---|---|
| the 20-word fixed head, by value | 93.5% at word 13 (80 distinct values) | 83.5% |
| the fixed head, **bit by bit** | 83.5% — nothing above the baseline | 83.5% |
| every property field, by value | 100% only on fields carrying ≤16 records | 83.5% |
| every property field, bit by bit | same, all on tiny populations | 83.5% |
| every word of the common 36-word prefix | 97.2% at word 23, 119 distinct values | 83.5% |

Nothing coarse separates it. The same sweep answered #630's mana-mode question at a single bit, so the shape
does work when the answer is there — its absence here says the source is not a single field of the spell record.
⭐ For `bool2` that negative is now EXPLAINED rather than open: it is a caster property, so no spell field could
ever have separated it. `descriptor[0]` stays open, and the readings ruled out for it since are that it is not an
arity count (its `2` form populates no extra descriptor slot, unlike the decoded `3`), not a re-apply/refresh
marker (it appears on both the first and later applies of one target+spell), and not the player-ness of either
endpoint.
Neither is it any `Spells` column we hold (16 candidates, all at baseline) nor catalog MEMBERSHIP
(`ClassSpells`, `Combos.GameActionId`, `SpellEffects`, `SpellStatEffects`, `NpcSpells`, `FeatSpellGrants` — all at
baseline). The `descriptor[0] == 2` set is characterised only by its names so far, and they are all combo- or
proc-applied: `Wrath` (21 ranks), `Spiritual Torment`, `Furious Inspiration`, `Mutilation`, `Chill`, `Momentum`,
`Blessed Blade`, `Renewal`, `Ripping Revenge`, `Sacrarial Guard`, `Covenant of Vengeance`, `Stunned`.

### ⭐ The buff object is `BuffInfo_c`, and it is a plain data struct (#658)

`bool2` lands in the client's own buff model, and the object it lands on is now named rather than described.
`CoreMagicHolder::AddBuffInfo` (`FUN_007eca90`) allocates it inline —

```
PUSH 0xc8 ; CALL FUN_00c9cce0        ; 200 bytes
MOV  ESI,EAX ; CALL FUN_00849770     ; the constructor
```

— and that constructor installs the vtable at `0x1fd2b78`, whose RTTI CompleteObjectLocator names
`.?AVBuffInfo_c@@`. Its base is `PtrTarget_c` (vtable `0x200cde4`), which is why `AddBuffInfo`'s `param_2` is a
`Ptr` and every field write goes through `*param_2`.

⛔ **And that kills the route this ticket proposed for it.** "Identify the class and sweep ITS methods" assumes
the class has methods: `BuffInfo_c`'s vtable has **7 slots and all seven are `PtrTarget_c`'s refcount
interface**. It is a plain data struct with no virtual accessors, so there is neither a method set to sweep nor
a slot for the `name_a_virtual_slot_by_who_overrides_it` technique to work on.

What DOES narrow it is scoping the offset sweep to `CoreMagicHolder`'s own band (`0x007e8000`–`0x007f8000`):
**13 functions** touch `+0x20` there, not the 90 the earlier attempt reported over a wider range. All 13 read
out; four of them write it as a **byte**, which is a different object (`AddBuffInfo` stores `bool2` as a u32).

⛔ **A second plausible-and-wrong consumer, recorded so it is not re-found.** `FUN_007eaf40` iterates a pointer
vector and tests `*(int *)(elem + 0x20) == 0 || == 6` — an **enum** test, which is exactly what this ticket
predicted from the u32 widening ("a field the client keeps as a word is more likely an enum than a flag"). It is
not the buff: the same element's `+0x18` is compared against **50000**, an LDB category, while `BuffInfo_c`'s
`+0x18`/`+0x1c` are written from the duration path. That is the second candidate on this ticket that looks
exactly like the answer and is a different object wearing the same offset — `FUN_007f1760` was the first.

### ⭐ `BuffInfo_c` has its own SERIALIZATION pair, and `+0x20` is field 4 of it

The one genuinely new route out of that sweep. `FUN_007ed9b0` allocates a `BuffInfo_c` — the same
`FUN_00c9cce0(200, 4, 0)` + `FUN_00849770` pair — and then fills it from a stream cursor (`FUN_0048f900()`,
called once per field) in offset order:

```
+0x14  +0x18  +0x1c  +0x20  +0x24  (+0x28 +0x2c)  +0xa8  +0xac  +0xb8  +0xbc  +0xc0
```

`FUN_007edac0` and `FUN_007f17a0` are the writer side of the same layout — `FUN_007edac0` loads `+0x14`,
byte-swaps it when a flag byte on the writer is set, and hands it to `FUN_00cba060(&value, 4)`.

So the client persists/transports a buff in a layout whose fourth field IS `bool2`, and that is a place a value
can be lined up against something observable — which none of the five closed routes offered.

### Two readings killed, and the one the numbers now favour (#658)

⛔ **`descriptor[0]` gates NOTHING in the payload.** The old cross-tab asked that of slots `[1]` and `[4]` only,
because those are the two that are near-zero rather than zero — which cannot see a byte populated on both values
that merely differs. Asked of **all 54 bytes**, the only one whose value sets are disjoint between `d0=1` and
`d0=2` is **byte @17 — descriptor[0]'s own low byte**. So the ARITY reading holds for `3` (which really does
gate `descriptor[1]`, 2 of 2) and for nothing else.

⛔ **It is not a per-cast stack or running count**, which the `Furious Inspiration (6)` / `Mutilation (1)` style
of name invites. Over the **397** re-application runs (same capture, same target, same spell, 2+ applies) it is
**constant on 394**, and the three that vary do not ramp — `2,1`, `1,1,2,1`, `2,2,2,1,2,1`.

⭐ **What does separate is PROVENANCE, measured as a RATE OVER APPLIES rather than as a partition's purity** —
which is exactly why the sweep above could not see it: against an 83.5% baseline a purity score needs a
near-perfect partition, and a 2× rate lift scores at the baseline. The cut the membership sweep did not include
is `SpellAreaEffects.EffectSpellId` — "can this spell be reached as another spell's effect at all":

| | `d0=1` | `d0=2` | `d0=3` |
|---|---:|---:|---:|
| reachable as another spell's effect | 35.3% | **52.0%** | 100% |
| castable (a `ClassSpells` row) | 16.7% | **4.1%** | 0% |
| ONLY reachable indirectly | 25.7% | **48.2%** | 100% |

So the reading with numbers behind it is an APPLY-SOURCE tag — `1` a direct cast, `2` applied by another
effect, `3` applied by a COMBO with `descriptor[1]` naming it. ⚠️ **A rate lift is a lead, not a decode**: none
of these is a partition, so `MagicBuffDatas.Apply` still writes the constant.

⭐ **`bool2 = 0` is essentially never a castable spell** — the single sharpest signal in the whole field, and it
holds per apply rather than per spell:

| | `bool2=1` | `bool2=0` |
|---|---:|---:|
| castable (a `ClassSpells` row) | 25.0% | **0.2%** |
| carries a `SpellStatEffect` | 65.6% | **96.3%** |
| reachable as another spell's effect | 49.3% | 18.0% |

3 applies of 1,334 at `bool2=0` are of a castable spell, against 523 of 2,090 at `bool2=1` — a 125× lift — and
the `bool2=0` population is almost entirely stat-effect-bearing, mana-free, recast-free and range-free. That is
the profile of a PASSIVE or derived effect rather than a cast, and it agrees with the one form we already know:
the casterless apply (`ApplyNoCaster`, retail's local-travel "Racer") carries `bool2 = 0` on all 11.

Reproduce all of it with `--report buff-descriptor`. Open work tracked in Gitea #658.

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
