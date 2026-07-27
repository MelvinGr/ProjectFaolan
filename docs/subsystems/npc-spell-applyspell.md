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
payload is a **buff/effect record** (template + magnitude + duration + flags), NOT a damage number. The actual
HP change rides a separate vitals `StatUpdate` (`0x96B8DC59`), as combat already does.

## Field map (54-byte payload)
Initially recovered by the reader grammar + an empirical diff of same-spell instances (per-cast fields) vs
different spells (spell params); then fully decoded and modelled as columns in #304
(`Faolan.Core.Network/Codecs/Combat/NpcSpellEffect.cs`, byte-exact over all 21 distinct captured payloads —
`Faolan.Tests/NpcSpellEffectCodecTests.cs`, which is where the proof lives now that the Python dissector that
did the original diff has been retired). The payload is a sparse fixed-offset layout, big-endian,
with every other byte a constant 0:

| Offset | Field | Notes |
|---|---|---|
| `@0–9`   | const 0 | |
| **`@10–11`** | **u16 Duration** | centiseconds; per-SPELL constant (Rend 700 = 7s, Poacher 400 = 4s, "Ripper Volley" 600 = 6s) |
| `@13` | flag A | 0/1 (meaning undecoded) |
| `@17` | flag B | 1/2 (meaning undecoded) |
| **`@23–26`** | **u32 spell id** | the cast's wire id `(base<<8)|rank` (`31 64 49 …`) |
| `@29` | byte Magnitude | effect strength |
| `@33` | flag C | 0/1 (meaning undecoded) |
| `@45` | byte Rank | equals Magnitude on most casts, differs on a few |
| `@50–53` | u32 counter/timestamp | the only steadily-incrementing field; the client does NOT validate it (0 when we synthesise a cast) |

Validated: `NpcSpellEffect.Parse`→`ToArray` reproduces every captured payload byte-for-byte, and `Duration` (`@10`)
and the echoed spell id (`@23`) are constant per spell.

## The rank pair
Every NPC cast arrives as a `(base<<8)|rank` pair: **rank 0** carries the full 54-byte payload (the apply),
**rank 1** carries an **empty** payload — the buff-end / effect-landed marker (same apply-then-end shape as the
mount buff `…01 empty=dismount`). So a caster's real cast = the rank-0 apply; the rank-1 is its terminator.

## What this gives #232
- The cast packet is fully understood and `NpcSpellCastBuilder` reproduces it byte-exact.
- The 54-byte payload is decoded into `NpcSpell` columns (`Duration`, `Magnitude`, `Rank`, `FlagA/B/C`) and the
  sim **builds** the wire payload from them via `NpcSpellEffect` (#304) — there is no stored blob any more. The
  value at `@10` that the early decode read as a u32 "magnitude" is actually the **Duration** (centiseconds; the
  `@10` u16 read big-endian off a zeroed high half, e.g. Poacher 400 = 4s), which is why the raw 400–4000 range
  is durations, not HP. The per-cast counter (`@50`) is session state and is not stored.
- Importing only the rank-0 (non-empty) row per spell is the clean form (the rank-1 is a terminator, not a
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
