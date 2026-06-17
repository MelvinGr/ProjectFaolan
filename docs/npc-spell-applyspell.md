# ApplySpell / MagicBuffIIR payload decode (#232)

The packet NPCs (and players) cast is **ApplySpell** (`0x6B87E316`, = `MagicBuffIIR` in `Opcodes.cs`). Its IIR
carrier content is:

```
[u32 len][u32 0x6B87E316][u32 c350][u32 applyTo][u16 0][u32 spellId][u32 c350][u32 applyFrom][PAYLOAD]
```

`applyFrom` is the caster (an NPC handle for NPC casts, the player for self-buffs/offensive casts), `applyTo`
the target. The **54-byte PAYLOAD** is the `MagicBuffIIR` effect descriptor.

## It is a BUFF descriptor, not raw damage
Ghidra `MagicBuffIIR_c::ReadSubClass` (`FUN_007a12a0`, `Captures/ghidra/DumpMagicBuffReader.java`) reads a type
byte (`+0x7c`, 0 or 2) then a sequence of `ReadU32`s + a 7-word sub-block (`FUN_00897b60`) + a `ReadU64`. The
class's `Activate` logs `"MagicBuffIIR_c::Activate, UpdateBuff with parameters: BuffTemplate ..."` — so the
payload is a **buff/effect record** (template + magnitude + duration + flags), NOT a damage number. The actual
HP change rides a separate vitals `StatUpdate` (`0x96B8DC59`), as combat already does.

## Field map (54-byte payload)
Recovered by the reader grammar + an empirical diff of same-spell instances (per-cast fields) vs different
spells (spell params) — `Captures/decode_applyspell.py`:

| Offset | Field | Notes |
|---|---|---|
| `@0–7`   | const 0 | |
| **`@8–11`** | **u32 magnitude** | per-SPELL constant — the effect strength (e.g. Pillager 1200, Poacher 400, Brutal Pirate 1500, 0x3B3C9C 4000) |
| `@12–19` | const framing | `@17 = 0x01` |
| `@20–26` | echoed spell id | the cast's `spellId` base (`31 6a a6 …`) |
| `@28–31` | rank / element | small per-spell value (`@29`) |
| `@32–43` | const 0 | |
| `@44–47` | small | `@45` per-cast |
| `@48–53` | u48 per-cast counter/timestamp | the only steadily-incrementing field across instances |

Validated: across 34 NPC-cast payloads (9 spells), `@8` is constant per spell and the echoed id matches.

## The rank pair
Every NPC cast arrives as a `(base<<8)|rank` pair: **rank 0** carries the full 54-byte payload (the apply),
**rank 1** carries an **empty** payload — the buff-end / effect-landed marker (same apply-then-end shape as the
mount buff `…01 empty=dismount`). So a caster's real cast = the rank-0 apply; the rank-1 is its terminator.

## What this gives #232
- The cast packet is fully understood and `NpcSpellCastBuilder` reproduces it byte-exact.
- The spell's **magnitude is readable at payload `@8`** (no extra storage needed — it rides the stored
  `NpcSpell.Payload`), available for tuning the cast damage. Mapping magnitude → HP damage (unit/scale) is the
  remaining in-client step; raw magnitudes (400–4000) would one-shot a low-level player, so they are an effect
  strength in a spell unit, not direct HP.
- Importing only the rank-0 (non-empty) row per spell is the clean form (the rank-1 is a terminator, not a
  standalone cast).

## In-client VERIFIED (2026-06-17, #232 done)
A spawned caster casts at the player in-client. Repro: `.spawnnpc Poacher Marksman` (a row with a 54-byte
`NpcSpell.Payload`), engage it → it follows and applies its debuff via `MagicBuffIIR (0x6B87E316)`, applyFrom =
the NPC handle, spell `0x316DCB` ("Ripper Volley") — it casts instead of meleeing.
**Fix that enabled the test (commit `df8f90d`):** `WorldBroadcaster.SpawnNpcForPlayer` (the `.spawnnpc` path) was
a minimal spawn that NEVER loaded `entity.Spells` — only the zone-load path did — so a summoned caster could never
cast regardless of data. It now loads the NPC's `NpcSpells` rows like the zone path, so `IsCaster` lights up.
Caveat: the applied debuff doesn't expire (no duration honored — #67; "Ripper Volley" shows a 6s tooltip but
sticks). NpcSpells data is still sparse (~14 named casters); `.spawnnpc` mobs also get no loot table.
