# Spell / stat computation engine (formulas) — #270

## ⚠️ SUPERSEDED: recast/cooldown is CLIENT DATA after all — see [`../re/spell-recast-source.md`](../re/spell-recast-source.md)

This section used to read *"recast is SERVER-AUTHORITATIVE (not client data) … Faolan defines it and sends the
GameAction, like retail"*. Both halves are wrong, and acting on them is what left a spell with no cooldown at all:

- **Retail sends no recast, ever.** All 1,745 `GameActionListIIR` bodies in the corpus, across 101 captures,
  carry five element types and none of the six recast ones. Emitting `SetSpellRecastTime` on cast would have been
  a packet retail never sends.
- **The base IS in the client**, in the spell's own `1040005` GameAction ELEMENT region — a
  `SetSpellRecastTime` / `SetSpellLineRecastTime` element carrying it under key `0x2f9`, the same key the
  tooltip's "Recast" line reads. The earlier searches missed it because they looked for a property FIELD, and it
  is not one. 3,682 spells now carry it (`Spell.RecastCentiseconds`), gated against eight retail tooltip readings.
- `ChangeRecastTime` / `ChangeLineRecastTime` are feat/AA **modifiers** on that base (`FUN_007e1230` computes
  `base + delta`), which is exactly why they exist as GameActions while the base does not.

What survives unchanged is the RE below it: `FUN_007e1080` is the per-spell recast arm ("Setting recast timer on
spell: … to: <cs>", ×0.01 s), and `FUN_008b0f50` is the `SetSpellRecastTime` handler that reads `0x2f9`.

---


**Why this exists:** an accurate emulator needs the *formulas*, not per-value lookups. Reverse-engineered from
`AgeOfConan.exe` (Ghidra project `tools/ghidra/output`, scripts in `tools/ghidra/` +
`tools/capture/`). Status: **foundation decoded; per-property completion is incremental.**

## Headline finding

Spell stats shown in the tooltip — **mana, recast (cooldown), damage, range** — are **COMPUTED at runtime,
not stored**. Proven empirically (`tools/capture/decode_spell_fields.py`): the displayed value (e.g. Chill
rank 5 recast 4s, mana 417 @L80) appears in **no** RDB record in any unit (int/u16/float, s/ds/cs/ms), and no
field is proportional to it. The RDB spell record (type `1040005`) holds only **base** fields; the client
applies modifiers + level/stat scaling. This is why a scraped spell-stat list is wrong (it lists static bases)
and why screenshots were the only ground truth.

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
  (**slot `+0x34` = getIntField(id)**, **`+0x84` = getStringField(id)**; e.g. id `0x21` = name) and `sprintf`s
  the line (format string `DAT_01f78354`, append via `FUN_0060f190`), scaling the value by `×0.01`.
- Spell loader: `FUN_0099bef0` = generic async resource load `(type=0xFDE85 spell, id)`.

### Known base field-ids
`0x126` cast (centiseconds, ×10 = ms) · `0x11F` range (×100) · `0x15C` **splash damage** (×100 — not the
"damage rating" this page called it until #626).

⚠️ "Many more are unmapped" is no longer true, and the ids this page listed as unmapped are the ones that make
the point: the client NAMES them. `0x36` `Level` · `0x4B` `AwardedOmniTekTokens` · `0x7B` `Rank` · `0x80` the
spell line · `0x81` `SpellCriticalDamage` · `0x93` `SpellCriticalChance` · `0x227` `StackingOrder` · `0x34C`
`AttackRatingType`. The field ids ARE wire stat ids, so LDB category 615 names them —
[spell-record-fields.md](../rdb/spell-record-fields.md) has all 111, and Gitea #633 tracks the ones still
unread.

### The one place a spell template DOES name a stat
The increasing field run carries magnitudes without saying what they modify. The `[2][9]` region in the same
record is the exception: it carries explicit `(kind, statId, amount)`
records, which is where a buff's armour/rating/attribute deltas come from -
[spell-stat-effects.md](../rdb/spell-stat-effects.md), applied by `BuffStatEffects` (#367). It covers potions,
elixirs and a slice of the class buffs; it is *not* how the buffs seen in retail captures express themselves, so
it does not make this page's headline finding any less true.

## Open (the remaining engine decode)
1. ~~**Recast base**~~ — **ANSWERED**: it is a `SetSpellRecastTime` / `SetSpellLineRecastTime` GameAction
   ELEMENT in the spell's own record, not a field-table value at all
   ([`../re/spell-recast-source.md`](../re/spell-recast-source.md)). Both of this page's previous positions were
   wrong in the same way — "recast is NOT a field-table value (proven)" was proven only over the ids this page
   had mapped, and the correction that named field **210** `Recharge Delay` as the answer does not survive the
   tooltip oracle: `Life Leech` carries 210 = **100** where its tooltip recast is **3000** cs, and the field is
   **0 on 2,334 of the 3,567 records that carry it** across just 36 distinct values — far too coarse to be a
   catalog whose recasts run from 1 to 180,000. Whatever 210 is, it is not this. Gitea #633 still tracks reading
   it, and #271 now only wants the in-client confirmation.
2. **Stat inputs** — what stat `0x17d` (and siblings) are, and where the buff/gear modifier lists are populated.
3. **Field-id → property map** — map the remaining base field-ids to mana/damage/etc. via the accessor family /
   `FUN_00769220` lines.
4. **Per-property paths** — decode mana, damage, recast end-to-end as each subsystem needs real numbers
   (decode-on-demand beats boiling the whole engine at once).

## Dynamic analysis (gdb) — works again + findings

**Toolchain:** gdb attach needs Proton sync OFF. The launcher now sets `PROTON_NO_ESYNC=1` **and**
`PROTON_NO_FSYNC=1` (Faolan.ClientLauncher/Program.cs:370-371, commit 31a0d26b) — Proton experimental-11 defaults `WINEFSYNC=1`,
and fsync's futex2 waits deadlock when gdb suspends threads (this is why earlier sessions worked and a
mid-session direct/stale launch hung). Also pass Wine signals through, else gdb stops on them instead of your
breakpoints: `handle SIGUSR1 SIGUSR2 SIGSEGV SIG33 SIG34 SIG35 nostop noprint pass`. Avoid `finish` inside
breakpoint commands (aborts under Wine); instead break at the function's RET and read EAX (EDI is callee-saved,
so the field-id survives). Getter `FUN_008d6c80` RETs: `0x8d6c94` (early, returns 0) and `0x8d6d7c` (main).

**Captured (casting Hellfire Breath, recast 2s / cast 2.5s):** at `0x8d6d7c` the getter returns field→value
incl. **`0x126 → 250`** = cast time 2.5s (✓ confirms 0x126 = cast, centiseconds). The cast read-set also hits
`0x4b 0x7e 0x222-0x226 0x2cd 0x1bf 0xc4 0xbc 0x227 0x36 0x11f` — most return 0 (not record fields).
**Recast (2s / 200cs) never appears as a getter return** on cast → recast is NOT read via the stat getter; it's
applied by the **cooldown subsystem** (a separate function, likely at cast-complete/lock-spell). That's the next
target now that gdb works: breakpoint the cooldown/recast-timer set and read the value there.

Ground truth for validation: L80-necro tooltip recasts in `Captures/spell_cooldowns_observed.tsv` (Chill 4s,
Lifestrike R6 12s, Life Leech 30s, Unimaginable Terror R2 45s, Death's Terror 90s) — all of which the decode now
reproduces, see [`../re/spell-recast-source.md`](../re/spell-recast-source.md). A combo's level and reuse time
come from the client's own combo XML (RDB 1010206), not from a scrape.
