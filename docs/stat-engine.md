# Spell / stat computation engine (formulas) — #270

**Why this exists:** an accurate emulator needs the *formulas*, not per-value lookups. Reverse-engineered from
`AgeOfConan.exe` (Ghidra project `/home/melvin/Development/Ghidra/Aoc`, scripts in `Captures/ghidra/` +
`scripts/`). Status: **foundation decoded; per-property completion is incremental.**

## Headline finding

Spell stats shown in the tooltip — **mana, recast (cooldown), damage, range** — are **COMPUTED at runtime,
not stored**. Proven empirically (`Captures/scripts/decode_spell_fields.py`): the displayed value (e.g. Chill
rank 5 recast 4s, mana 417 @L80) appears in **no** RDB record in any unit (int/u16/float, s/ds/cs/ms), and no
field is proportional to it. The RDB spell record (type `1040005`) holds only **base** fields; the client
applies modifiers + level/stat scaling. This is why ellatha's spell stats are wrong (it lists static bases)
and why screenshots were the only ground truth.

## The computation pipeline

Spell-object stat accessors → generic getter `FUN_008d6c80` → stat-scaling `FUN_008d7080`:

```
base            = spell template field-table lookup       // vtable *(obj+0x10)+0x3c+8, field-id in EDI; values stored x100
per modifier    = (base + mod.add) * (1 + mod.mult/10000) // stacked over TWO lists: obj+0x14 (buffs), obj+0x18 (gear/feats)
display value   = computed * 0.01                          // fields are x100, so /100 to display
final           = + a stat-0x17d scaling layer            // FUN_008d7080, vtable call (0x17d, 2, value*0.01)
```

### Constants (Ghidra `scripts/DumpFloats.java`)
| addr | value | role |
|---|---|---|
| `0x01f2800c` | `0.01` | stored→display (fields stored ×100) |
| `0x01f26250` | `1e-4` | modifier percent scale (`mult/10000`) |
| `0x01f27500` | `1.0` | the `+1` base of the percent multiplier |
| `0x01f24468` | `100.0` | range path (`base × 100`, field `0x11F`) |

### Key functions
- `FUN_008d6c80(statObj)` — generic field getter: base (vtable `*(obj+0x10)+0x3c+8`) + stacked modifiers from
  the two lists. Field-id arrives in EDI.
- `FUN_008d7080` — wraps the getter, applies the stat-`0x17d` scaling (level/equip).
- `FUN_0076b7e0 / 8a0 / 990` — per-property accessor wrappers (the `0x11F` range cluster); each sets a field-id
  then calls the generic getter; `FUN_0076b990` also applies a stat-`0x17d` modifier.
- `FUN_00769220` — a tooltip/stat-line formatter: reads fields via the spell-object vtable
  (**slot `+0x34` = getIntField(id)**, **`+0x84` = getStringField(id)**; e.g. id `0x21` = name) and `sprintf`s
  the line (format string `DAT_01f78354`, append via `FUN_0060f190`), scaling the value by `×0.01`.
- Spell loader: `FUN_0099bef0` = generic async resource load `(type=0xFDE85 spell, id)`.

### Known base field-ids (from `decode_spell_fields.py` over the RDB records)
`0x126` cast (centiseconds, ×10 = ms) · `0x11F` range (×100) · `0x15C` damage rating (×100). Many more
(`0x36 0x4B 0x7B 0x80 0x81 0x93 0x227 0x34C …`) are unmapped.

## Open (the remaining engine decode)
1. **Recast base** — recast is NOT a field-table value (proven). It is computed or uses a **cooldown-category**
   (a small index → a global time table), or a separate path not yet found. Likely needs **dynamic analysis**
   (gdb the client, breakpoint the getField/tooltip with a known spell — see memory `gdb_live_client_debugging`)
   to pin which input yields 4s/45s, rather than more static tracing.
2. **Stat inputs** — what stat `0x17d` (and siblings) are, and where the buff/gear modifier lists are populated.
3. **Field-id → property map** — map the remaining base field-ids to mana/damage/etc. via the accessor family /
   `FUN_00769220` lines.
4. **Per-property paths** — decode mana, damage, recast end-to-end as each subsystem needs real numbers
   (decode-on-demand beats boiling the whole engine at once).

Ground truth for validation: L80-necro tooltip recasts in `Captures/spell_cooldowns_observed.tsv` (Chill 4s,
Lifestrike R6 12s, Life Leech 30s, Unimaginable Terror R2 45s, Death's Terror 90s); combo level/cooldown in
`Captures/ellatha_combos.tsv`.
