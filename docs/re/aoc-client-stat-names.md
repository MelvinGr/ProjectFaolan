# The client's own stat-name table

`AgeOfConan.exe` carries a static metadata table that binds **every stat id to a name string**. It is a far wider
name source than LDB category 615: the LDB names none of the `1000000+` range at all, and most of the `0x545..0x593`
run is unnamed there too, while this table names both.

Data: [`aoc-client-stat-names.tsv`](aoc-client-stat-names.tsv) — **1,511** ids,
`statId <tab> hex <tab> clientName`. 1,234 of them are members of `CharacterStatId`; the rest are ids the enum
already carried, which keep the names whatever decoded them first rather than being renamed out from under their
callers. One id is deliberately absent: `0x542`, whose client name is `Deaths` — a name this enum already uses for
`0x292` (from LDB 615, where the client's own table calls that one `PvPTotalDeaths`). That is a genuine two-source
disagreement, not a duplicate name in the client.

Regenerate with:

```zsh
FROM=01d00000 TO=01e80000 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
  -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpStatNameTable.java
```

## Shape

Each stat is registered as several 0x20-byte records in a long static-init run, and the **first** record of each stat
is preceded by a `PUSH <name string>`:

```
01de6e16: PUSH 0x1f72a00              ; "MaxEnergy"
01de6e1b: MOV EAX,0x29c6ca8           ; record base + 8 (the variant slot)
01de6e20: MOV dword ptr [0x029c6ca0],0xf425d   ; the stat id
01de6e2a: MOV dword ptr [0x029c6ca4],0x0       ; the record's kind
01de6e34: CALL 0x010daa10             ; store a STRING variant (type tag 9)
```

The stat's remaining records take the same id with a different kind and store an *int* variant (type tag 4) through
`FUN_010da850` / `FUN_010da880`, and carry no `PUSH` — hence one name per stat. None of them holds a game value: the
ints are small kind/flag words, so this table is metadata, not defaults.

## Why the names can be trusted

The table is self-checking against ids recovered from entirely unrelated directions:

- `804` `Strength` and the `805..815` `Max*`/attribute run — the ids in `CharacterStatId` from the character sheet.
- `1071` `OffhandChance` — measured in-client as Offhand Attack Chance.
- `1351` `NaturalEnergyRegeneration` — LDB category 615 names the same id "Natural Energy Regeneration".

## What it named that nothing else could

**The whole spawn scaffold.** All 31 of the `0x545..0x593` word stat ids in `SpawnPreIir.WordStatIds` are in the
table, and they are exactly the PvP / minigame career that scaffold's decode predicted — `PvPFatalities` (`0x564`),
`PvPMachesAbandoned` (`0x560`), the `Annihilation`/`CTF`/`Conquest`/`Siege`/`Tournament` won-lost pairs, and the
`Onslaught*` wave counters. Two independent corroborations fall out:

- the scaffold's two FLOAT slots are `0x545` **`ResurrectionTime`** and `0x546` **`ResurrectionTimePvP`** — timers,
  which is what a float belongs to;
- `0x1E86AE` (word 18) is **`SelectedTag`**, and the flag bytes `0xF4261`/`0xF4260` are **`NumFeatSpecs`** and
  **`ActiveFeatSpec`** — matching the feat-spec sublist that sits in the same scaffold.

**The energy pool** — `1000028` `Energy`, `1000029` `MaxEnergy`, `1000031` `EnergySprintDrain` — which is the model
behind the yellow sprint/spellweave bar. See [`../subsystems/combos.md`](../subsystems/combos.md).

**`1000070` `DailyPvPKills`** and `1000069` `DailyPvEKills`, the counters paired with `ChainKillIIR_t`.

## The sub-768 half, and how it stopped being noise

The first sweep kept a pair when the STORED VALUE looked like a plausible stat id — a guess about the data rather
than a bound on the table. Below 768 the false pairs dominated it (a nearby audio-preset init stores `8` "Cave",
`11` "Carpeted Hallway", `2` "LoadingMusic" in exactly this shape), so the whole range was thrown away and the TSV
stopped at 768.

**The fix is to key on the descriptor SLOT** — the address the id is written TO. The descriptors live in two
contiguous global regions and the id sits at a fixed offset inside each 0xA0-byte record:

| region | what it holds | id at `slot % 0xA0` |
| --- | --- | --- |
| `0x0298F000..0x029CC000` | the main table | `0x00`, `0x20`, `0x40`, `0x60` (1,361 of them) or `0x80` |
| `0x021B8000..0x021BC000` | the `1000000+` table | `0x98` |

That is a statement about the table, so it does not care what the value looks like, and it comes with its own
check: a descriptor writes its id exactly ONCE, so a correct filter yields **no duplicate ids**. It yields 1,511
rows and 1,511 distinct ids. What the offset set EXCLUDES is the point — `0x44` and `0x64` are two neighbouring
fields a descriptor also writes, and they are where the head-of-region artifacts live (four descriptors whose kind
word is 8, which would otherwise claim id 8 from `TimeExist`).

⚠️ **CONSUME and EMIT have to be different tests**, and getting that wrong loses rows in both directions. The
pending name is consumed by the first store into the TABLE carrying a non-zero value — not by every store (which
pairs a name with a kind word, losing `GameAction_LocalizerType` and seven others) and not only by an id-slot store
(which lets a name run past its own descriptor into the next one's id, mislabelling `3 AttackSpeed` as
`VolumeMass`).

**It is a strict widening, not a re-reading.** All 747 names the id-filtered sweep found are reproduced with **zero
disagreements**; 764 ids are added, 762 of them below 768.

The retained rows have not been individually verified beyond the spot checks above — treat a name as strong
evidence, not as a citation, until the id is used.

### What the sub-768 half named

- **`0x069` `NextPowersessionReset` / `0x06A` `PowerSessionRemaining`.** The second was in the enum as
  `LastKillNumber`, "a per-kill sequence number, semantics unknown", and it is in the captured snapshot at `0x14`
  — a full session of 20. That reframes Gitea #656: the live observation of stat 106 counting 20/19/18 DOWN while
  `DailyPvPKills` counts 0/1/2 UP is a **power session being spent by kills**, not a second kill counter.

  ⭐ **The game's own documentation names the player-facing concept and settles what the 20 is a quota OF.** The
  official quickstart guide calls them **Fresh Kills**: *"You get 20 Fresh Kills every day. The number displayed at
  the end of the bar are 'Fresh Kills', which grant extra XP when killing worthy monsters."*
  ([ageofconan.com/quickstart](https://www.ageofconan.com/quickstart)) So `PowerSessionRemaining` is the number the
  client draws at the end of the XP bar, `NextPowersessionReset` is when it refills, and `0x0C3`
  `PowerSessionBonusesCounter` is the third member of the trio. A **daily** refill is stated outright, which was one
  of #656's open questions.

  The corpus agrees from the other side: **every captured character's login snapshot carries `0x06A`**, at `20` for
  Hakotep / DemoL80 / AssassinL23 and at **`17` for NecroL80** — who is exactly the character the live observation caught
  three kills into her session. Nothing else in the trio is in the snapshot, so the reset instant and the bonus
  counter arrive (if at all) as stat updates.

  ⚠️ Still open, and it is what any implementation turns on: **how much extra XP a Fresh Kill grants, and what makes
  a monster "worthy"**. Neither the client's names nor the guide says, and no capture measures it.
- **`0x292` is `PvPTotalDeaths`, not `Deaths`**, sitting in a coherent run — `656 PvPTotalKills`,
  `657 PvPFinishingBlows`, `658 PvPTotalDeaths`, `659 PvPMatchesWon`, `660 PvPMatchesLost`. So the client does NOT
  use `Deaths` twice, and the belief that it did was the old sweep mispairing `0x542`'s name onto a neighbour.
- The engine's Anarchy-Online lineage shows through the low ids — `4 Breed`, `13 MagicBurden`, `17
  MemorizationSlot` — which is itself a check on the range: those are names nothing in this codebase would invent.

## ⚠️ A name is not a consumer: `ChosenGod` (`0x0A9`) is stored and never read

The table names `0x0A9` `ChosenGod`, which is why #553 asked what its VALUES mean — retail sends `1` for one
captured character and `0` for the other 183, and Mitra/Set/Ymir is the in-game set. **The client cannot answer
that, and the reason is worth writing down: it never looks at the value.**

`ConstScan CONST=a9` finds exactly **two** uses of `0xA9` as a stat id in the whole image, and both are WRITES —
`FUN_007a2ad0` (`SetStat(0xa9, *(obj + 0x14c))`) and the stat-apply run in `FUN_01458d40`, where it is one of a
block (`0xd7 GmLevel`, `0xed`, `0x42`, `0xa9`) copied out of consecutive fields of a server-fed struct. Every
other `PUSH 0xa9` in the binary is an assert's line number (`formationdata.cpp`, `DepthPrepassShader.cpp`,
`fmod_codec_wav.cpp`, …). There is **no** `GetStat(0xa9)` anywhere.

It is not that the stat is ignored, either — one class overrides its stat accessors to give this single id a
dedicated slot:

```c
// FUN_00843d10 / FUN_00843d40, adjacent in vtable 0x01fd2640
if (param_1 != 0xa9) { return FUN_0080ab20(param_1, param_2); }   // everything else: the generic stat bag
return *(undefined4 *)(in_ECX + 0x244);                            // 0xA9 lives here
```

…and those two functions are the only ones on that class that touch `+0x244`. So the client caches the value,
mirrors it, and consumes it nowhere.

**That makes `ChosenGod` a SERVER-side number**, the same tell as the `Combat_{PvE,PvP}*ShieldBonus` tweaks the
client registers and never reads ([`../rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)). The practical
consequence for anyone picking up #553: there is no value→deity mapping hidden in the client to recover, and no
in-client check that could confirm one, because nothing the client draws depends on it. Whatever we emit is
invisible until religion is modelled server-side.

**The general rule:** the stat-name table tells you what an id IS called, not that the client does anything with
it. Before treating a named id as decodable from the binary, check that something READS it.


## ⚠️ This table is an INTERNAL name list, and LDB 615 outranks it for meaning (#625)

The client carries **three** name sources for a stat id, and they are not interchangeable:

| source | what it is |
| --- | --- |
| **LDB category 615** | the **DISPLAY** name — localized, shipped in seven languages, and what the client's own tooltip prints |
| **LDB category 633** | an **INTERNAL** identifier list, English only |
| **this table** (the exe's static descriptors) | also internal, and a MIX — it sides with 633 sometimes and with 615 others |

Measured over the 341 ids that carry both a 615 and a 633 name: **263 agree (77%) and 78 disagree.** Most
disagreements are cosmetic (`Default Position` / `DefaultPos`, `Bonus XP` / `XPBonus`), but some are a different
claim entirely:

| id | LDB 615 (display) | 633 / this table (internal) |
| ---: | --- | --- |
| `1037` | **Knockback Resistance** | `Weight` |
| `147` | **Spell Critical Chance** | `MaxSneak` |
| `120` | **Money** | `PickPocketCash` |
| `8` | **Duration** | `TimeExist` |
| `294` | **Casting Time (Seconds)** | `ItemDelay` |
| `129` | **Spell Critical Damage** | `SpellCriticalModifier` |

⭐ **`1037` is the case that settles which source wins.** `CharacterStatId.KnockbackResistance` is live code —
`KnockbackService` reads it and `BuffStatEffects` honours it — and 615 names it *Knockback Resistance* in all
seven languages while this table says `Weight`. Renaming toward the internal name would have been a regression,
and `147` (*Spell Critical Chance* vs `MaxSneak`) shows the internal list can be flatly wrong about a field's
identity rather than merely terser.

**So: cite LDB 615 for what a stat MEANS.** This table's value is coverage, not authority — it names the
`1000000+` range and the `0x545..0x593` run that 615 does not reach at all, and for an id 615 never names it is
the only AoC-era evidence there is. Where they disagree and the id is user-facing, 615 is the answer.

⚠️ It also still carries **Anarchy Online** vocabulary in places (`NanoFocusLevel` for `355`, where this table
says `LastPvPXP`), which is a second reason not to treat an internal name as a decode result.
