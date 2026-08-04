# The client's own stat-name table

`AgeOfConan.exe` carries a static metadata table that binds **every stat id to a name string**. It is a far wider
name source than LDB category 615: the LDB names none of the `1000000+` range at all, and most of the `0x545..0x593`
run is unnamed there too, while this table names both.

Data: [`aoc-client-stat-names.tsv`](aoc-client-stat-names.tsv) — 747 ids, `statId <tab> hex <tab> clientName`.
630 of them are now members of `CharacterStatId`; the rest are ids the enum already carried, which keep the names
whatever decoded them first rather than being renamed out from under their callers. One id is deliberately absent:
the client uses `Deaths` for **both** `0x292` (which the enum already has) and `0x542`.

Regenerate with:

```zsh
FROM=01d00000 TO=01e60000 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
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

⚠️ The sweep is by code shape, not by table bounds, so a `PUSH <string>` followed by a `MOV [global],<imm>` anywhere
in the swept range can produce a false pair. Ids **below 768** were dominated by such pairs (an audio-preset init
gave `8` "Cave", `11` "Carpeted Hallway", `2` "LoadingMusic") and are excluded from the TSV; every retained name is a
plain identifier. The retained rows have not been individually verified beyond the spot checks above — treat a name
as strong evidence, not as a citation, until the id is used.
