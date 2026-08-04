# Where a spell's recast (cooldown) comes from

Recovered 2026-08-04 against `AgeOfConan.exe` plus a full sweep of the capture corpus. This page exists because
the earlier reading — "base cooldowns are client-side, read from the client's RDB spell record when the spell is
granted" — is **wrong in its second half**, and that mistake is why nothing on our side ever gates a cast.

## The corpus says retail sends no recast, ever

Every `GameActionListIIR` (`0xF508F4C1`) body in the corpus was parsed and its element type ids tallied against
the recovered catalog (`docs/re/gameaction_types.json`, 68 types):

> **1745 GameActionList packets across 101 captures carry exactly 5 distinct types** — `Hit` (38),
> `TimedEffect` (18), `BCCCastSpell` (15), `SpawnRemotePet` (9), `TeamFormula` (5).

None of the six recast/cooldown types (`ChangeRecastTime` `0xCF81`, `ChangeLineRecastTime` `0xD019`,
`SetSpellRecastTime`, `SetSpellLineRecastTime`, `ModifyTemplateCooldown`, `ModifySpellLineCooldown`) appears.
So the recast is **not** something the retail server pushes on a cast, and `SpellRecastActions` staying
construct-only is correct.

## The client's own cast path arms only CONSTANTS

The raw timer arm is `FUN_0074a270(timerSlot, seconds)`. Its callers on the cast path arm fixed floats, not
per-spell data:

| call site | value | meaning |
|---|---|---|
| `FUN_0072e960` (cast state machine, case 1) → `FUN_007d7ee0` | `FLD [0x02019f54]` = **0.5** | global cooldown |
| `FUN_0072f7c0` → `FUN_007d7ee0` | `FLD1` = **1.0** | global cooldown |
| `FUN_00833440` | **2.0**, or **10.0** when a type word is `0x96` | global cooldown |

`FUN_007d7ee0` also walks the caster's other spells and arms the same value on every one sharing a line
(`FUN_005eed00` equality) — i.e. this is the shared GCD, not a spell's own recast.

## The per-spell recast timer has exactly two entry points, and both are server GameActions

`FUN_007e1080` is the only per-spell arm — its debug channel is `GameCode/Magic/Recast` and it logs
`"Setting recast timer on spell: <id> For player: <p> to: <cs>"`, then applies `value * 0.01` seconds. Its only
two callers are GameAction handlers:

- `FUN_008b0f50` — `GameCode/GameAction/SetSpellRecastTime`; reads key `0x463` (spell) and `0x2f9` (recast).
- `FUN_008b07d0` → `FUN_007e1230` — the `ChangeLineRecastTime` (`0xD019`, keys `[0x2d3, 0x2f9]`) handler.

`FUN_007e1230` is the important one, because it shows the recast is a **base plus a delta**:

```c
base  = FUN_007f5360(&spell, 4, 1);   // the spell's own value
base += param_3;                      // + the GameAction's key 0x2f9  -> a MODIFIER, not the value
pct   = FUN_007f5360(&spell, 5, 1);
if (0.009 < pct * 0.01) base = base * pct * 0.01;
FUN_007e1080(spellId, base);
```

So `ChangeRecastTime` / `ChangeLineRecastTime` are **feat/AA modifiers on a base the client already holds**.
They are not the mechanism that gives a spell its cooldown, which is consistent with the corpus never carrying one.

## The tooltip names the base's source

The spell tooltip builder is `FUN_00769220`. It reads the spell's properties through vtable slot `0x34` by
**stat id** — confirmed by the lines whose units are already known: `0x11F` (287 `AttackRange`) rendered
`/ 100` as metres, and `0x1BF` (447 `ChannelingInterval`) rendered `* 0.01` as seconds.

The `"Recast"` label (`@0x01FC74EC`, pushed at `0x00769C33`) is emitted straight after `FUN_0076b200`, which
computes the number. `FUN_0076b200` iterates a **list of GameAction elements** and, for the matching one, does:

```c
base  = FUN_0077a510(element);        // GameActionData::GetIntStat(GameActionStat_e)
base += FUN_007f5360(&charStats, 4, 1);
pct   = FUN_007f5360(&charStats, 5, 1) * 0.01;
```

`FUN_0077a510`'s own assert string is `"Tried to GetIntStat for GameActionStat_e <k> but it did not exist,
returning 0"` under channel `GameActionData/GetStat`. So **the base recast is a key on a GameAction element
attached to the spell**, and the same `+ index 4`, `× index 5 %` shape as the handler above — the tooltip and the
modifier handler agree, which is what makes this reading solid.

The key is `0x2f9` — `GameActionTypes.KeyValue` (index 39; `docs/re/gameactionlist-grammar.md` maps 39 → 761
`0x2f9` `Value`), the same key both recast handlers read.

## `RecastTime` is stat id 65, and on the spell record it is a sparse OVERRIDE

Recovered from the client's static stat-name metadata table (the shape documented for
`docs/re/aoc-client-stat-names.tsv`): at `0x01DB4D31`, `PUSH 0x01F6C250` (`"RecastTime"`) alongside
`MOV [0x029918C0], 0x41` — **stat id `0x41` = 65**.

Field 65 occurs on only **10 of the 21,502** spell records (`1040005`), with values `0` ×5, `99999999` ×3
(= never reusable: *Frenzy*, *Stress*, *Mothers Love*), `1` (*Enrage*) and `30000` (*Lightning Reflexes*). That is
the signature of a per-record override, not the general source — see the sparse-field rule in
`docs/rdb/spell-record-fields.md`. Field 65 does not occur at all on the `1000020` templates.

## Where the base lives: the spell record's own ELEMENT region

The tooltip fetches the element list off the spell object itself, via virtual slot `0x84` called with index `0`
(`0x00769BF9`..`0x00769C05`, the result handed straight to `FUN_0076b200`). The same slot is called with `0x21`
elsewhere in the builder, so it is an indexed getter for the spell's own GameAction lists — the elements come from
the spell's RDB data, not from the wire.

**They are in the `1040005` record's GameAction ELEMENT region** — the same region a feat's spell modifiers live
in, read by the same `ItemTemplateStatModifiers.ParseElements`. Nothing new had to be decoded. The region was
never looked at for a recast because the search was framed as *"which property FIELD is it"*, and the two
eliminations below are both answers to that question; a property field is not what the client reads.

`Life Leech` (220979) carries `SetSpellLineRecastTime(line 41026, 3000)`, and 3000 centiseconds is the **30 s**
its retail tooltip shows.

### The gate: eight tooltip readings, transcribed before any of this was decoded

`Captures/spell_cooldowns_observed.tsv` is a human reading of a level-80 necromancer's spell tooltips, written
down when the value was believed to be unsourceable. Every row comes back out of the decode:

| spell | tooltip | element |
|---|---|---|
| `Chill` rank 5 | 4 s | `SetSpellLineRecastTime(41028, 400)` |
| `Lifestrike` rank 5 | 12 s | `SetSpellLineRecastTime(41027, 1200)` |
| `Life Leech` | 30 s | `SetSpellLineRecastTime(41026, 3000)` |
| `Unimaginable Terror` rank 2 | 45 s | `SetSpellLineRecastTime(41023, 4500)` |
| `Pestilential Blast` | — | no element, and none on line 41001 |
| `Ice Strike` | — | no element, and none on line 41005 |

The three zeroes are the half that makes it a gate rather than a coincidence: `Pestilential Blast` and
`Ice Strike` are that character's most-used spells, and a decode that put a plausible number on everything would
pass the four values and fail here. Pinned by `Faolan.Tests/SpellRecastTests.cs`.

### Sweep and import

`--report spell-recast [--report-arg <spellId>]` prints every recast element in the catalog: **5,872 elements on
3,316 of the 21,502 spell records**, 5,195 `SetSpellRecastTime` and 674 `SetSpellLineRecastTime` (plus 3
`ChangeLineRecastTime`, which is a modifier and is not read).

`RdbSpellImporter` fills `Spell.RecastCentiseconds` from them — **3,682 spells**, against 408 before, all of
which were combos backfilled from the ellatha scrape. Two rules, both forced by the data:

- **A spell's OWN record leads.** 338 spells and 12 lines are named by two records with two different values,
  because a rank arms its whole family and the ranks do not agree: `Embodiment of Terror` rank 1's own record
  says 90 s, rank 2's record says 80 s *for that same rank 1*. Each rank keeps what it says about itself, which
  is what its own tooltip shows.
- **A rank with no element of its own inherits its line's.** `Unimaginable Terror` rank 1 carries nothing;
  rank 2 arms 45 s on their shared line 41023, and arming a line is by definition arming every rank on it.

⚠️ The column is **centiseconds**, the client's own unit (`value * 0.01` s). It held seconds until this landed,
and 67 of the shipped values are not a whole number of them.

### Two plausible-looking answers that were checked and eliminated first

- **Not a top-level field of the spell record.** The `1040005` field run has 111 distinct field ids
  (`docs/rdb/spell-record-fields.md`); `761` (`0x2F9`) is not among them, and `65` `RecastTime` occurs on 10
  records as an override.
- **Not field 321.** `0x141` looks tempting — 10,591 records carry it and its values share an id range with
  `Combos.GameActionId` — but resolving one shows it is the **effectset / animation** id, not a GameAction:
  `3154746` → `212103`, which exists as RDB `1010214` (`<effectset name="effectset_necromancer_lifeleech">`) and
  `1010513` (an `LMXB` animation). A shared id range is not a shared meaning.

⚠️ Beware the oracle when picking a spell to test against. `Spells` holds several rows per display name, and many
are **monster** buffs, not the player spell: `3154746` "Life Leech" is described as *"Buffs the monster with a
minor lifetap ability"*, and the `3000` in its stat region is `0x37E` `HealthTapAmount` (the drain amount), not
the 30 s recast the player-spell tooltip shows. Two unrelated values can agree by coincidence.

Combos are the one place this already works from our side, and by a completely different route: a combo's
cooldown is a plain attribute of the client's combo XML (RDB `1010206`), imported by
`Faolan.Importer/Rdb/RdbComboImporter.cs` — 292 of 744 combos carry one.

## The server gate

`HandleBccCastSpell` refuses a cast whose recast group is still armed, and does it by **sending nothing at all**.
That is not laziness about the reply, it is the only honest option: retail's client will not *send* a cast that is
on cooldown — its own recast timer blocks the button — so retail's server has no captured answer to one, and any
reply we chose here would be invented. Sending no echo-ack is also self-consistent, because the echo-ack is
precisely what makes the client enter the channel state; without it the cast neither bars nor resolves.

The group is the one the client itself arms: the spell's GUI stacking line where it has one (so all five `Chill`
ranks share the 4 s), the spell id otherwise. It is armed **when the spell goes off**, not when the cast begins —
an interrupted cast costs no cooldown — and lives on the session, so a relog re-arms nothing.

## What is still open

Whether the CLIENT gates too. Retail never sends a recast GameAction and the client's cast path arms only the GCD
constants, so the client must read this same base locally at cast time — the elements are right there on the spell
object, and the only per-spell arm (`FUN_007e1080`) is reachable from the `SetSpellRecastTime` handler, which is
exactly the element type the record carries. That is a coherent account, and it is now the leading one, but the
local call site has not been pinned. It changes nothing server-side: the gate above does not depend on it.

Open work tracked in Gitea #271.
