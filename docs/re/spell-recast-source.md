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

## Where the base lives is still OPEN — and two plausible-looking answers are ruled out

The tooltip fetches the element list off the spell object itself, via virtual slot `0x84` called with index `0`
(`0x00769BF9`..`0x00769C05`, the result handed straight to `FUN_0076b200`). The same slot is called with `0x21`
elsewhere in the builder, so it is an indexed getter for the spell's own GameAction lists. That means the
elements come from the spell's RDB data rather than from anything on the wire — but the exact record has not been
pinned, and **two candidates have been checked and eliminated**:

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

## What is still open

Finding the record the spell's GameAction elements live in, and importing the `KeyValue` recast as
`Spell.Cooldown`. Today only 408 of 20,848 `Spells` rows carry a cooldown, all of them combos backfilled from
the ellatha scrape, and **none** is a castable player spell — which is why a spell can be re-cast with no delay.

A caveat to settle alongside it: if retail never sends a recast GameAction *and* the client's cast path arms only
the GCD constants, then the client must read that base locally at cast time, and that local read has not been
pinned to a call site. So it is not yet proven that importing the value alone would make the client gate. The
**server-side gate is required regardless** — a server must not depend on the client to refuse a cast.

Open work tracked in Gitea #271.
