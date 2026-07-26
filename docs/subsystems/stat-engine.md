# Spell / stat computation engine (formulas) — #270

## RESOLVED: recast/cooldown is SERVER-AUTHORITATIVE (not client data)

Found via the exe's recast strings. The recast timer is set by `FUN_007e1080` ("Setting recast timer on
spell: ... to: <param2>"; param2 = recast centiseconds, ×0.01 = seconds). Its callers:
- `FUN_008b0f50` = the **`SetSpellRecastTime` GameAction handler** — reads recast from the action
  (`FUN_0089bd80(action, 0x2f9)`, param id `0x2f9` = recast centiseconds) and sets the timer.
- `FUN_007e1230` = `ChangeLineRecastTime` — adjusts a spell line's recast by a supplied delta.

GameActions in the exe: `SetSpellRecastTime`, `SetSpellLineRecastTime`, `ChangeRecastTime`,
`ChangeLineRecastTime` (`e_Type_GC_GameAction_*`). **The server computes recast and pushes it to the client**
at cast time; the client only enforces/displays it. This is why every client-data search failed (6 static + 2
dynamic) — the recast simply isn't in the client. **Faolan defines it and sends the GameAction**, like retail.
Same pattern likely for mana/damage. Implementation = a feature (emit `SetSpellRecastTime` on cast), not a
decode; values from `Spell.Cooldown` (combos) + `Captures/spell_cooldowns_observed.tsv`.

---


**Why this exists:** an accurate emulator needs the *formulas*, not per-value lookups. Reverse-engineered from
`AgeOfConan.exe` (Ghidra project `tools/ghidra/output`, scripts in `tools/ghidra/` +
`tools/capture/`). Status: **foundation decoded; per-property completion is incremental.**

## Headline finding

Spell stats shown in the tooltip — **mana, recast (cooldown), damage, range** — are **COMPUTED at runtime,
not stored**. Proven empirically (`tools/capture/decode_spell_fields.py`): the displayed value (e.g. Chill
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

### Known base field-ids (from `decode_spell_fields.py` over the RDB records)
`0x126` cast (centiseconds, ×10 = ms) · `0x11F` range (×100) · `0x15C` damage rating (×100). Many more
(`0x36 0x4B 0x7B 0x80 0x81 0x93 0x227 0x34C …`) are unmapped.

### The one place a spell template DOES name a stat
The increasing field run carries magnitudes without saying what they modify (`551` is "400", never "400 of
WHAT"). The `[2][9]` region in the same record is the exception: it carries explicit `(kind, statId, amount)`
records, which is where a buff's armour/rating/attribute deltas come from -
[spell-stat-effects.md](../rdb/spell-stat-effects.md), applied by `BuffStatEffects` (#367). It covers potions,
elixirs and a slice of the class buffs; it is *not* how the buffs seen in retail captures express themselves, so
it does not make this page's headline finding any less true.

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
Lifestrike R6 12s, Life Leech 30s, Unimaginable Terror R2 45s, Death's Terror 90s); combo level/cooldown in
`Captures/ellatha_combos.tsv`.
