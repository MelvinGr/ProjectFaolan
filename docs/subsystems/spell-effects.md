# Spell effects: what a spell makes the server do

Most of a spell needs no server logic beyond the wire path: `ApplySpell` carries the buff, `Spell.DurationMs`
counts it down, and `SpellStatEffect` folds its stat deltas into the carrier (`BuffStatEffects`). A
handful of spells do something none of that can express — they summon a creature, or they arm a proc that
fires on a later event. Those live in the **`SpellEffects`** table, and `SpellEffectService` dispatches them.

## The row

| Column | Meaning |
|---|---|
| `SpellId` | the BASE spell id (the `Spells` key), never the wire `(base << 8) \| rank`. FK to `Spells`. |
| `Trigger` | `OnCast` — the spell is cast · `OnFatality` — a fatality lands while this spell is a live buff on the killer |
| `Sequence` | run order within one `(SpellId, Trigger)`; one trigger can carry several effects |
| `Kind` | `SummonPet` · `ApplySpell` |
| `TargetSpellId` | `ApplySpell`: the base id to apply to the player |
| `Template`, `Combat` | `SummonPet`: the creature TYPE name (`NpcTemplate.Name`) and whether it is a combat pet or a social companion |
| `DurationMs`, `MaxDurationMs` | `SummonPet`: a TIMED pet's lifetime, and the ceiling a re-summon may extend it to. NULL = lives until dismissed / no cap |

Key is the natural `(SpellId, Trigger, Sequence)`. The rows are seeded by the importer's `spell-effects`
module, which is `Db`-sourced — the facts are decoded, not extracted, so it runs with no captures directory.

## What is in it

- **The 12 necromancer pet summons**: spell id = the `CastSpell` trailer id, template = the name of the
  `FA015807` creature that spawned after the channel. Roster from
  `NecroL80_..._mount-horse_all-necro-summons_dismiss-all` (all 12 summoned + mass-dismissed).
  `Pleasurable Company` is the one social companion.
- **Frozen Hatred's fatality proc**, as two rows on the buff `4377209` that arms it: the 6s
  `3666099` self-buff, then the `Frozen Hatred` pet for 60s capped at 5 minutes. The client states the rule
  itself in that spell's description (LDB 50211), and 21 of the 36 captured fatality bursts carry the proc
  (`xxx_mage-necromancer` beach→city fr1267).

## What is NOT in it, and why

Not everything keyed by an id belongs here — the table answers "what does this SPELL do", and three
neighbouring mechanisms are keyed by something else:

- **Mounts** (the `Mount` table) are keyed by the client's MOUNT id (the `SummonMount` behavior's `from` word,
  which is the summon spell's own record id) and carry the mount creature the client animates from. The
  ride-buff payload and the summon channel stay in `MountCatalog` — see `mounts.md`.
- **Spellweaves** (`SpellweaveCatalog`) are keyed by CLASS: weave elements and the energy pool are per-class
  profiles.
- **Shield of the Risen** and **Exhausted** are single spell ids used at one call site each (world entry,
  energy depletion). Those are spell references, not a dispatch table.
- **`SpellStatEffect`** is the client's own `[2][9]` template region, machine-extracted per spell (1714 rows)
  and re-derived on every RDB re-import, keyed by the identity the region gives it. `SpellEffects` is decoded
  BEHAVIOUR that appears nowhere in the template: no field of a summon record names the creature it spawns.

## Dispatch

`SpellEffectService` loads the table once and answers two questions:

- `ForCast(spellBase)` — the cast resolve (`CastService`) runs these instead of applying a
  target effect, so a summon cast summons rather than landing a buff icon.
- `ForFatality(entity)` — `FatalityService` runs these after the two Fatality buffs, for every live buff on
  the killer that arms one.

The wire side is the `ISpellEffectOutbound` port (apply a self-buff, summon a pet, summon-or-extend a timed pet),
implemented by `SpellEffectOutbound` over `BuffService` and `PetService`, so the service holds a narrow port rather
than the listener.

Open work tracked in Gitea #307.
