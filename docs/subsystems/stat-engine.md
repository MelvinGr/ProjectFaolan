# Spell / stat computation engine (formulas)

An accurate emulator needs the formulas, not per-value lookups. This page maps the client's spell-stat computation,
reverse-engineered from `AgeOfConan.exe` (the Ghidra project lives in the data repo's `Ghidra/`; scripts in
`tools/ghidra/` and `tools/capture/`). The foundation is decoded; individual properties are decoded as a subsystem
needs real numbers.

## Spell stats are computed, not stored

Spell stats shown in the tooltip — mana, recast (cooldown), damage, range — are computed at runtime, not stored
as displayed. Shown empirically (`tools/capture/decode_spell_fields.py`): the displayed value (e.g. Chill rank 5
recast 4s, mana 417 @L80) appears in no RDB record in any unit (int/u16/float, s/ds/cs/ms), and no field is
proportional to it. The RDB spell record (type `1040005`) holds only base values; the client applies modifiers +
level/stat scaling. A scraped spell-stat list is therefore wrong (it lists static bases), and tooltips are the
ground truth.

### Recast is client data, and retail never sends it

See [`../re/spell-recast-source.md`](../re/spell-recast-source.md).

- **Retail sends no recast.** All 1,745 `GameActionListIIR` bodies in the corpus, across 101 captures, carry five
  element types and none of the six recast ones. Emitting `SetSpellRecastTime` on cast would be a packet retail
  never sends.
- **The base is in the client**, in the spell's own `1040005` GameAction element region — a
  `SetSpellRecastTime` / `SetSpellLineRecastTime` element carrying it under key `0x2f9`, the same key the
  tooltip's "Recast" line reads. It is not a property field, so a field search does not find it. 3,682 spells
  carry it (`Spell.RecastCentiseconds`), gated against eight retail tooltip readings.
- `ChangeRecastTime` / `ChangeLineRecastTime` are feat/AA modifiers on that base (`FUN_007e1230` computes
  `base + delta`), which is why they exist as GameActions while the base does not.
- `FUN_007e1080` is the per-spell recast arm ("Setting recast timer on spell: … to: <cs>", ×0.01 s), and
  `FUN_008b0f50` is the `SetSpellRecastTime` handler that reads `0x2f9`.
- Field **210** `Recharge Delay` is not the recast: `Life Leech` carries 210 = 100 where its tooltip recast is
  3000 cs, and the field is 0 on 2,334 of the 3,567 records that carry it across just 36 distinct values — far too
  coarse for a catalog whose recasts run from 1 to 180,000.

Open work tracked in Gitea #271.

## The computation pipeline

Spell-object stat accessors → generic getter `FUN_008d6c80` → stat-scaling `FUN_008d7080`:

```
base            = spell template field-table lookup       // vtable *(obj+0x10)+0x3c+8, field-id in EDI; values stored x100
per modifier    = (base + mod.add) * (1 + mod.mult/10000) // stacked over TWO lists: obj+0x14 (buffs), obj+0x18 (gear/feats)
display value   = computed * 0.01                          // fields are x100, so /100 to display
final           = + a stat-0x17d scaling layer            // FUN_008d7080, vtable call (0x17d, 2, value*0.01)
```

### Constants (Ghidra `tools/ghidra/ReadFloat.java`)
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
  (slot `+0x34` = getIntField(id), `+0x84` = getStringField(id); e.g. id `0x21` = name) and `sprintf`s
  the line (format string `DAT_01f78354`, append via `FUN_0060f190`), scaling the value by `×0.01`.
- Spell loader: `FUN_0099bef0` = generic async resource load `(type=0xFDE85 spell, id)`.

### Known base field-ids
`0x126` cast (centiseconds, ×10 = ms) · `0x11F` range (×100) · `0x15C` splash damage (×100).

The field ids are wire stat ids, so LDB category 615 names them: `0x36` `Level` · `0x4B` `AwardedOmniTekTokens` ·
`0x7B` `Rank` · `0x80` the spell line · `0x81` `SpellCriticalDamage` · `0x93` `SpellCriticalChance` · `0x227`
`StackingOrder` · `0x34C` `AttackRatingType`. [spell-record-fields.md](../rdb/spell-record-fields.md) has all 111.
Open work tracked in Gitea #633.

### The one place a spell template does name a stat
The increasing field run carries magnitudes without saying what they modify. The `[2][9]` region in the same
record is the exception: it carries explicit `(kind, statId, amount)`
records, which is where a buff's armour/rating/attribute deltas come from -
[spell-stat-effects.md](../rdb/spell-stat-effects.md), applied by `BuffStatEffects`. It covers potions,
elixirs and a slice of the class buffs; it is not how the buffs seen in retail captures express themselves, so
the computed-not-stored rule above still holds.

Not decoded: what stat `0x17d` (and its siblings) are, where the buff/gear modifier lists are populated, and the
end-to-end mana and damage paths.

## Dynamic analysis (gdb)

gdb attach needs Proton sync off: `Faolan.ClientLauncher` sets `PROTON_NO_ESYNC=1` and `PROTON_NO_FSYNC=1`
(Proton experimental-11 defaults `WINEFSYNC=1`, and fsync's futex2 waits deadlock when gdb suspends threads). Pass
Wine signals through, or gdb stops on them instead of your breakpoints:
`handle SIGUSR1 SIGUSR2 SIGSEGV SIG33 SIG34 SIG35 nostop noprint pass`. Avoid `finish` inside breakpoint commands
(it aborts under Wine); break at the function's RET and read EAX instead (EDI is callee-saved, so the field-id
survives). Getter `FUN_008d6c80` RETs: `0x8d6c94` (early, returns 0) and `0x8d6d7c` (main).

Casting Hellfire Breath (recast 2s / cast 2.5s): at `0x8d6d7c` the getter returns field→value incl.
`0x126 → 250` = cast time 2.5s (confirms 0x126 = cast, centiseconds). The cast read-set also hits
`0x4b 0x7e 0x222-0x226 0x2cd 0x1bf 0xc4 0xbc 0x227 0x36 0x11f` — most return 0 (not record fields). The recast
(200 cs) never appears as a getter return: it is applied by the recast arm above, not read through the stat getter.

Ground truth for validation: L80-necro tooltip recasts in `Captures/spell_cooldowns_observed.tsv` (Chill 4s,
Lifestrike R6 12s, Life Leech 30s, Unimaginable Terror R2 45s, Death's Terror 90s), all reproduced by the decode
in [`../re/spell-recast-source.md`](../re/spell-recast-source.md). A combo's level and reuse time come from the
client's own combo XML (RDB 1010206), not from a scrape.
