# Where a spell's recast (cooldown) comes from

Recovered against `AgeOfConan.exe` plus a full sweep of the capture corpus. Summary: the base recast is a
GameAction element in the spell's own RDB record; the client arms nothing per-spell on its own cast path; and the
server tells the client a spell is cooling down with **`DispatchLockTemplate` (0x204E)**, never with a recast
GameAction.

## Retail sends no recast GameAction

Every packet body in all **132** dumps was swept for the GameActionData element signature — a u32be inside the
client's asserted type range (`0xCF09`..`0xD026`, `GameActionTypeId`) with the version word `4` exactly 8 bytes
later. Anchoring on the signature rather than on the `GameActionListIIR` framing counts an element nested as a
**sub-record** too:

> **2,095 elements across 15 distinct type ids**, every one of them inside a `0xF508F4C1` body, every one s2c —
> `CastNano` (967), `VisualLocation` (541), `StartParticleEffect` (290), `StartClientSideScryScriptWithParams`
> (61), `AttractorMesh` (50), `SetIdleBaseAnim` (40), `BindAtLocation` (38), `StopScryScript` (21),
> `CameraRoll` (18), `SetTradingPostAccess` (18), `SendScryEvent` (15), `StartServerSideScryScript` (13),
> `StartServerSideScryScriptWithParams` (12), `StartClientSideScryScript` (10), `CameraShake` (1).

**None of the four recast types appears, under either numbering** (see the catalog warning below), sub-records
included.

### `gameaction_types.json`'s `typeId` column is not the wire value

`docs/re/gameaction_types.json` holds 68 types whose `typeId` is a **step-4 sequence** (53005, 53009, 53013…)
rather than the contiguous enum the wire carries. Resolving observed ids through it lands only 5 of the 15 observed
ids on an entry, and those 5 on the *wrong* entry (a spurious `Hit` / `TimedEffect` / `BCCCastSpell` /
`SpawnRemotePet` / `TeamFormula` census).

**The wire numbering is `docs/re/gameaction-type-ids.tsv`** (contiguous: 53002 `Hit`, 53003
`AddLayeredSkinnedMesh`, …). The corpus anchors it: our capture-verified travel action is `0xD00D` and carries a
`ScriptParams` string (`xpos=..;PlayFieldID=..;Local=1`), which is `StartServerSideScryScriptWithParams` — the
tsv's name for `0xD00D`. The json calls it `TeamFormula`. The tsv reading also produces a coherent corpus
(a `CastNano` on nearly every spell effect, a Scry-script family, camera actions); the json reading does not.
Under the json numbering `ChangeRecastTimeType` would be `0xCF81`, which the real catalog names **`Feared`**.
Pinned by `SpellRecastActionsTests.RecastTypeIds_AreTheWireEnum_NotTheStepFourSequence`.

The four recast types, with the key order `SpellRecastActions` writes (`docs/re/gameaction-keylists.tsv`):

| type | wire id | keyList order |
|---|---|---|
| `SetSpellRecastTime` | `0xCFEA` | `0x463` spell, `0x2F9` recast |
| `SetSpellLineRecastTime` | `0xCF55` | `0x2D3` line, `0x2F9` recast |
| `ChangeRecastTime` | `0xCF27` | `0x4C3` spell, `0x2F9` delta |
| `ChangeLineRecastTime` | `0xCF4D` | `0x2D3` line, `0x2F9` delta |

## The client's own cast path arms only constants

The raw timer arm is `FUN_0074a270(timerSlot, seconds)`. Its callers on the cast path arm fixed floats, not
per-spell data:

| call site | value | meaning |
|---|---|---|
| `FUN_0072e960` (cast state machine, case 1) → `FUN_007d7ee0` | `FLD [0x02019f54]` = **0.5** | global cooldown |
| `FUN_0072f7c0` → `FUN_007d7ee0` | `FLD1` = **1.0** | global cooldown |
| `FUN_00833440` | **2.0**, or **10.0** when a type word is `0x96` | global cooldown |

`FUN_007d7ee0` also walks the caster's other spells and arms the same value on every one sharing a line
(`FUN_005eed00` equality) — i.e. this is the shared GCD, not a spell's own recast.

## The per-spell recast timer has exactly two entry points, both GameAction handlers

`FUN_007e1080` is the only per-spell arm — its debug channel is `GameCode/Magic/Recast` and it logs
`"Setting recast timer on spell: <id> For player: <p> to: <cs>"`, then applies `value * 0.01` seconds. Its only
two callers are GameAction handlers:

- `FUN_008b0f50` — `GameCode/GameAction/SetSpellRecastTime`; reads key `0x463` (spell) and `0x2f9` (recast).
  It has **zero direct callers** (a virtual, reached through the GameAction element's Apply slot) and bails out
  silently on a state check before reading anything: `if (*(int *)(param_1 + 0x1b0) != 2) return;`, where
  `param_1` is the caster/player object. What `+0x1b0` is is unidentified — 287 object-field writes to that
  offset exist and none is attributable without the class.
- `FUN_008b07d0` → `FUN_007e1230` — the `ChangeLineRecastTime` (`0xD019`, keys `[0x2d3, 0x2f9]`) handler. It
  carries no such guard: it walks the spell list at `+0x30`, matches the line with `FUN_00768f90`, and arms each
  match with base + delta.

`FUN_007e1230` shows the recast is a **base plus a delta**:

```c
base  = FUN_007f5360(&spell, 4, 1);   // the spell's own value
base += param_3;                      // + the GameAction's key 0x2f9  -> a MODIFIER, not the value
pct   = FUN_007f5360(&spell, 5, 1);
if (0.009 < pct * 0.01) base = base * pct * 0.01;
FUN_007e1080(spellId, base);
```

So `ChangeRecastTime` / `ChangeLineRecastTime` are **feat/AA modifiers on a base the client already holds**.
The client's own cast path is `CoreMagicHolder::CastMagicSpell` (strings incl. *"CastMagicSpell Apply Spell
effect from caster"*), which is where a locally-applied element would come from.

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
modifier handler agree.

The key is `0x2f9` — `GameActionTypes.KeyValue` (index 39; `docs/re/gameactionlist-grammar.md` maps 39 → 761
`0x2f9` `Value`), the same key both recast handlers read.

## `RecastTime` is stat id 65, and on the spell record it is a sparse override

From the client's static stat-name metadata table (the shape documented for
`docs/re/aoc-client-stat-names.tsv`): at `0x01DB4D31`, `PUSH 0x01F6C250` (`"RecastTime"`) alongside
`MOV [0x029918C0], 0x41` — **stat id `0x41` = 65**.

Field 65 occurs on only **10 of the 21,502** spell records (`1040005`), with values `0` ×5, `99999999` ×3
(= never reusable: *Frenzy*, *Stress*, *Mothers Love*), `1` (*Enrage*) and `30000` (*Lightning Reflexes*). That is
the signature of a per-record override, not the general source — see the sparse-field rule in
`docs/rdb/spell-record-fields.md`. Field 65 does not occur at all on the `1000020` templates.

## Where the base lives: the spell record's own element region

The tooltip fetches the element list off the spell object itself, via virtual slot `0x84` called with index `0`
(`0x00769BF9`..`0x00769C05`, the result handed straight to `FUN_0076b200`). The same slot is called with `0x21`
elsewhere in the builder, so it is an indexed getter for the spell's own GameAction lists — the elements come from
the spell's RDB data, not from the wire.

**They are in the `1040005` record's GameAction element region** — the same region a feat's spell modifiers live
in, read by the same `ItemTemplateStatModifiers.ParseElements`. A search for "which property field is it" cannot
find it; the client does not read a property field.

`Life Leech` (220979) carries `SetSpellLineRecastTime(line 41026, 3000)`, and 3000 centiseconds is the **30 s**
its retail tooltip shows.

### The gate: tooltip readings

`Captures/spell_cooldowns_observed.tsv` is a human reading of a level-80 necromancer's spell tooltips, made
independently of this decode. Every row comes back out of it:

| spell | tooltip | element |
|---|---|---|
| `Chill` rank 5 | 4 s | `SetSpellLineRecastTime(41028, 400)` |
| `Lifestrike` rank 5 | 12 s | `SetSpellLineRecastTime(41027, 1200)` |
| `Life Leech` | 30 s | `SetSpellLineRecastTime(41026, 3000)` |
| `Unimaginable Terror` rank 2 | 45 s | `SetSpellLineRecastTime(41023, 4500)` |
| `Pestilential Blast` | — | no element, and none on line 41001 |
| `Ice Strike` | — | no element, and none on line 41005 |

The zeroes are the half that makes it a gate rather than a coincidence: `Pestilential Blast` and `Ice Strike` are
that character's most-used spells, and a decode that put a plausible number on everything would pass the four
values and fail here. Pinned by `Faolan.Tests/SpellRecastTests.cs`.

### Sweep and import

`--report spell-recast [--report-arg <spellId>]` prints every recast element in the catalog: **5,872 elements on
3,316 of the 21,502 spell records**, 5,195 `SetSpellRecastTime` and 674 `SetSpellLineRecastTime` (plus 3
`ChangeLineRecastTime`, which is a modifier and is not read).

`RdbSpellImporter` fills `Spell.RecastCentiseconds` from them. Two rules, both forced by the data:

- **A spell's own record leads.** 338 spells and 12 lines are named by two records with two different values,
  because a rank arms its whole family and the ranks do not agree: `Embodiment of Terror` rank 1's own record
  says 90 s, rank 2's record says 80 s *for that same rank 1*. Each rank keeps what it says about itself, which
  is what its own tooltip shows.
- **A rank with no element of its own inherits its line's.** `Unimaginable Terror` rank 1 carries nothing;
  rank 2 arms 45 s on their shared line 41023, and arming a line is by definition arming every rank on it.

The column is **centiseconds**, the client's own unit (`value * 0.01` s); 67 of the shipped values are not a whole
number of seconds. Measured against every recast element in the RDB (direct + line inheritance), exactly **3**
spells are unfilled, one of them carrying `cs = 0`.

### Two plausible-looking answers that are wrong

- **Not a top-level field of the spell record.** The `1040005` field run has 111 distinct field ids
  (`docs/rdb/spell-record-fields.md`); `761` (`0x2F9`) is not among them, and `65` `RecastTime` occurs on 10
  records as an override.
- **Not field 321.** `0x141` looks tempting — 10,591 records carry it and its values share an id range with
  `Combos.GameActionId` — but resolving one shows it is the **effectset / animation** id, not a GameAction:
  `3154746` → `212103`, which exists as RDB `1010214` (`<effectset name="effectset_necromancer_lifeleech">`) and
  `1010513` (an `LMXB` animation). A shared id range is not a shared meaning.

Beware the oracle when picking a spell to test against. `Spells` holds several rows per display name, and many
are **monster** buffs, not the player spell: `3154746` "Life Leech" is described as *"Buffs the monster with a
minor lifetap ability"*, and the `3000` in its stat region is `0x37E` `HealthTapAmount` (the drain amount), not
the 30 s recast the player-spell tooltip shows. Two unrelated values can agree by coincidence.

### Combos are a separate source

A combo's cooldown is a plain attribute of the client's combo XML (RDB `1010206`), imported by
`Faolan.Importer/Rdb/RdbComboImporter.cs` — 292 of 744 combos carry one, and it is written onto the spell the combo
drives (`Combos.GameActionId`). The two sources are **disjoint**, measured over the whole catalog: of the 246 combo
spells carrying an XML cooldown, none is named by a `SetSpellRecastTime` element and none sits on a line a
`SetSpellLineRecastTime` element targets.

## Retail's server does not refuse an on-cooldown cast

Filtering c2s `0x642cd3d6` to **begins** (state word `3`; state `2` is the cancel, and counting those as re-casts
inverts the result), the corpus holds **2,131 begins, 9 of them sent while the spell was still on its recast**. So
the client does send them, rarely. Retail's answer to **six of those nine** is `(4, 10)` — begin-echo then
complete, byte-identical to an ordinary cast:

| spell | recast | re-sent after | reply |
|---|---|---|---|
| Dread Archmage: Blood Arcanist | 22 s | 5.29 s | `(4, 10, 4)` |
| Deathless Acolyte: Magus | 22 s | 4.75 s | `(4, 10)` |
| Ancient Soul | 60 s | 9.59 s | `(4, 10)` |
| Gangrenous Stench | 60 s | 4.99 s | `(4, 4, 7)` |
| Waves of Flame (Rank 1) | 4 s | 0.39 s | `(4, 7)` |
| Chill (Rank 5) | 4 s | 0.12 s | `(5,)` |

**Retail simply casts them.** The recast is a client concern: the client blocks its own button, and on the rare
occasion it sends anyway the server obliges. (The three odd replies on the three shortest gaps are unexplained —
3 samples.)

Refusing in silence breaks the client: with no echo-ack the cast bar sticks at 100%, the client stays in the
channel state, and every later cast is dropped. Our server therefore arms the recast on the session
(`Session.SpellReadyAt`) but gates nothing with it. The group is the spell's GUI stacking line where it has one
(so all five `Chill` ranks share the 4 s), the spell id otherwise; it is armed when the spell goes off, so an
interrupted cast costs nothing, and a relog re-arms nothing.

A buff with a real duration blocks its own re-cast client-side regardless: `ApplySpell` sends a `MagicBuffIIR`
carrying the duration, and the client will not re-cast a spell whose own effect is up. That is a buff, not a
cooldown — a spell with `Spells.DurationMs == 0` shows no such block.

## The cooldown is `DispatchLockTemplate`

The recast is not a GameAction on the wire at all. It is **`GameCharInterface.DispatchLockTemplate` (0x204E)** —
the message that greys a bar button and runs its sweep; the corpus carries 992 of them.

Every captured body is 8 bytes:

```
[u32 templateId][f32 secondsRemaining]          the float BIG-endian

0002fa65 41a00000  ->  195173 'Contract of Protection (Rank 1)', 20.0 s
```

Of the 741 bodies whose id resolves against `Spells`, **580 (78.3%) carry exactly that spell's
`RecastCentiseconds / 100`**. The other 161 are partials against the same spell (15.143 / 15.254 / 15.76 where the
recast is 20.0) — the time left rather than the base, the same restore shape a long buff's duration takes.

**One per spell in the line, not one for the cast.** A `Contract of Protection` cast emits five bodies in a
row — 195173 / 195178 / 195179 / 195180 / 1151248, every rank of line 12 — each carrying the same 20.0. That is
the wire form of `SetSpellLineRecastTime` and why all ranks grey together.

The 251 bodies whose id is absent from `Spells` are **abilities and combos**: "template" is the client's word for
the thing on the bar, not specifically a spell.

Built by `LockTemplateCodec`, sent by `CastService.SendSpellCooldown` from the site in `Resolve()` that arms the
server's own timer, carrying what remains after the cast time. Verified in-client.

Sending a `SetSpellRecastTime` GameAction on cast also makes a cooldown appear in-client, but retail never sends
one, so it is not how the server drives cooldowns.

### Related cast-path facts

1. **A duration-0 ApplySpell poisons the cast.** The client answers
   `ERROR: CoreMagicHolder::ActivateBuff <id> remaining time 0` and aborts, losing the spell's own GameActions.
   Retail sends an ApplySpell only for a spell that has something to apply: of 5,423 s2c MagicBuffIIR bodies,
   5,416 carry a spell with a real duration and the 7 that do not are buffs whose duration is missing on our side.
2. **`StatDamage.SpellBaseId` is 0** in retail: 3,059 of 3,061 records, and all 50 with the stat id we use. A
   non-zero id there makes the client log `GetMagicSpell failed to load spell with id …` on every hit.

### The cadence is not matched

The messages and their contents match retail. When they are sent does not:

| | retail | ours |
|---|---|---|
| casts followed by a `LockTemplate` naming the cast spell or its line | 87% of buff casts, **41%** of nuke casts | **100%** of casts with a recast |
| BCC cast records per cast | 7–14 | 2 (begin echo + complete) |

The `LockTemplate` figures are attribution-based (the body must name the cast spell or a line sibling), not mere
proximity — but the window is 45 packets, so a long cast can fall outside it. Retail is plainly more selective
and the rule is unknown; a cancelled cast costing no cooldown is the obvious candidate.

## Instrument: the client's own recast log

The client logs this path itself, and both channel names are verbatim-verified against `AgeOfConan.exe`:

| channel | what it prints |
|---|---|
| `GameCode/Magic/Recast` | `Setting recast timer on spell: <id> For player: <p> to: <cs>` |
| `GameCode/GameAction/SetSpellRecastTime` | `Modifying the recast timer by : <x>` · `The recast timer is already set to a higher value (spell = …)` · `SetSpellRecastTimeGameAction refers to nonexistant spell : <id>` |

```zsh
dotnet run --project Faolan.ClientLauncher -- --console --patch-universeaddr \
  --debug-channels GameCode/Magic/Recast,GameCode/GameAction/SetSpellRecastTime -u faolan -p any -c <char>
```

Cast a spell that has a recast, then read `AgeOfConan.log`. The three outcomes are disjoint: **silence** = the
element is never applied (the `+0x1b0` guard, or `CastMagicSpell` never reaches the apply) · **"nonexistant
spell"** = the client cannot resolve the id · **"Setting recast timer"** = it arms and something downstream
clears it.

Open work tracked in Gitea #271.
