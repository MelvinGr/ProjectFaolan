# Where a spell's recast (cooldown) comes from

Recovered 2026-08-04 against `AgeOfConan.exe` plus a full sweep of the capture corpus. This page exists because
the earlier reading — "base cooldowns are client-side, read from the client's RDB spell record when the spell is
granted" — is **wrong in its second half**, and that mistake is why nothing on our side ever gates a cast.

## The corpus says retail sends no recast, ever

Every packet body in all **132** dumps was swept for the GameActionData element signature — a u32be inside the
client's asserted type range (`0xCF09`..`0xD026`, `GameActionTypeId`) with the version word `4` exactly 8 bytes
later. Anchoring on the signature rather than on the `GameActionListIIR` framing means an element nested as a
**sub-record** is counted too, which the earlier framing-based tally could not see:

> **2,095 elements across 15 distinct type ids**, every one of them inside a `0xF508F4C1` body, every one s2c —
> `CastNano` (967), `VisualLocation` (541), `StartParticleEffect` (290), `StartClientSideScryScriptWithParams`
> (61), `AttractorMesh` (50), `SetIdleBaseAnim` (40), `BindAtLocation` (38), `StopScryScript` (21),
> `CameraRoll` (18), `SetTradingPostAccess` (18), `SendScryEvent` (15), `StartServerSideScryScript` (13),
> `StartServerSideScryScriptWithParams` (12), `StartClientSideScryScript` (10), `CameraShake` (1).

**None of the four recast types appears, under either numbering** (see the catalog warning below). So the recast
is **not** something the retail server pushes on a cast. The sub-record blind spot was checked precisely because
it would have been the obvious place for the carrier to hide; it is not there either.

### ⚠️ `gameaction_types.json`'s `typeId` column is NOT the wire value

The earlier reading of this sweep — *"1745 packets carry exactly 5 distinct types: `Hit` (38), `TimedEffect`
(18), `BCCCastSpell` (15), `SpawnRemotePet` (9), `TeamFormula` (5)"* — is an artifact of resolving observed ids
through `docs/re/gameaction_types.json`, which holds 68 types and whose `typeId` is a **step-4 sequence**
(53005, 53009, 53013…) rather than the contiguous enum the wire carries. Only 5 of the 15 observed ids happened
to land on an entry, and those 5 landed on the *wrong* entry.

**The wire numbering is `docs/re/gameaction-type-ids.tsv`** (contiguous: 53002 `Hit`, 53003
`AddLayeredSkinnedMesh`, …). The corpus anchors it: our capture-verified travel action is `0xD00D` and carries a
`ScriptParams` string (`xpos=..;PlayFieldID=..;Local=1`), which is `StartServerSideScryScriptWithParams` — the
tsv's name for `0xD00D`. The json calls it `TeamFormula`. The tsv reading also produces a coherent corpus
(a `CastNano` on nearly every spell effect, a Scry-script family, camera actions); the json reading does not.

This is not academic. `SpellRecastActions` took its ids from the json, so `ChangeRecastTimeType` was `0xCF81` —
which the real catalog names **`Feared`**. The builder was construct-only, which is the only reason a fear was
never sent in place of a cooldown. Pinned by `SpellRecastActionsTests.RecastTypeIds_AreTheWireEnum_NotTheStepFourSequence`.

The four recast types, with the key order `Build` writes (`docs/re/gameaction-keylists.tsv`):

| type | wire id | keyList order |
|---|---|---|
| `SetSpellRecastTime` | `0xCFEA` | `0x463` spell, `0x2F9` recast |
| `SetSpellLineRecastTime` | `0xCF55` | `0x2D3` line, `0x2F9` recast |
| `ChangeRecastTime` | `0xCF27` | `0x4C3` spell, `0x2F9` delta |
| `ChangeLineRecastTime` | `0xCF4D` | `0x2D3` line, `0x2F9` delta |

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

## The per-spell recast timer has exactly two entry points, and both are GameAction handlers

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
which were combos backfilled from a since-retired scrape. Two rules, both forced by the data:

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
`Faolan.Importer/Rdb/RdbComboImporter.cs` — 292 of 744 combos carry one, and it is written onto the spell the
combo drives (`Combos.GameActionId`).

⚠️ The two sources are **disjoint**, measured over the whole catalog: of the 246 combo spells carrying an XML
cooldown, none is named by a `SetSpellRecastTime` element and none sits on a line a `SetSpellLineRecastTime`
element targets. A combo's reuse time and a spell's recast are two different facts in two different places.

## ~~The server gate~~ — REMOVED. Retail has no such gate (#767)

⚠️ **This section used to say the server refuses an on-cooldown cast by sending nothing, and called that "the only
honest option" because "retail's server has no captured answer to one". Both halves were wrong, and the gate is
gone.**

The premise was that retail's client never sends a cast on cooldown. Measured over the dumps — filtering c2s
`0x642cd3d6` to **BEGINs** (state word `3`; state `2` is the CANCEL, and counting those as re-casts is what makes
this look the other way) — the corpus holds **2,131 begins, 9 of them sent while the spell was still on its
recast**. So the client does send them, rarely. And retail's answer to **six of those nine** is `(4, 10)` —
begin-echo then COMPLETE, byte-identical to an ordinary cast:

| spell | recast | re-sent after | reply |
|---|---|---|---|
| Dread Archmage: Blood Arcanist | 22 s | 5.29 s | `(4, 10, 4)` |
| Deathless Acolyte: Magus | 22 s | 4.75 s | `(4, 10)` |
| Ancient Soul | 60 s | 9.59 s | `(4, 10)` |
| Gangrenous Stench | 60 s | 4.99 s | `(4, 4, 7)` |
| Waves of Flame (Rank 1) | 4 s | 0.39 s | `(4, 7)` |
| Chill (Rank 5) | 4 s | 0.12 s | `(5,)` |

**Retail simply casts them.** The recast is a CLIENT concern: the client reads it from its own RDB and blocks its
own button, and on the rare occasion it sends anyway the server obliges. (The three odd replies on the three
shortest gaps are unexplained — 3 samples, could be a fast-resend path or a move-interrupt landing in the window.)

⚠️ **And refusing in silence BREAKS THE CLIENT.** No echo-ack means the cast bar sticks at 100%, the client stays
in the channel state, and every later cast is dropped. Reproduced in-client: *"after casting ancient soul, i cant
cast anymore"*, with `CastSpell REFUSED spell=0x3E338F (Ancient Soul): 16764 ms of its 6000 cs recast left` in the
log at that moment.

The recast is still **armed** on the session — the timer is real and is wanted for the client-side signal work
below — it just gates nothing. The group is the spell's GUI stacking line where it has one (so all five `Chill`
ranks share the 4 s), the spell id otherwise; armed when the spell goes off, so an interrupted cast costs nothing,
and a relog re-arms nothing.

## The cooldown never worked for ANY spell — and the "working" spells were a buff (#767)

The reported symptom was per-spell and looked unexplainable: Flesh to Worms (R5), Gangrenous Stench, Freeze (R2),
Tome of Epithur, Ghoulish Feast (R3) and Quell the Ether (R5) showed a cooldown and could not be spammed; Chill
(R5), Lifestrike (R5), Life Leech, Gelid Bones (R5), Ancient Soul, Dread Archmage and Cannibalism showed nothing
and could be. Six readings were tested and falsified against it (over-attribution from `SetSpellLineRecastTime`;
"needs a direct `SetSpellRecastTime`"; "direct AND non-zero line"; the bar casting a different id; line
membership; spell-id magnitude).

**The split is `Spells.DurationMs`, and it is total: all six working have `DurationMs > 0`, all seven failing
have `DurationMs == 0`.** That is a BUFF column, not a cooldown one — so the six were never on cooldown. They
have a buff sitting on them: `ApplySpell` sends a `MagicBuffIIR` carrying a real duration, the client renders it
and will not re-cast a spell whose own effect is up. A `DurationMs == 0` spell gets a duration-0 `MagicBuffIIR`,
which #371 established is an INSTANT — no icon, untracked — so nothing blocks it. The two spells that break every
*other* pattern fall out of this one: **Dread Archmage** has `DurationMs 0` but `EffectSpell 1366143` (4 h), so
the buff we send is a *different id* from the bar button and blocks nothing; **Ancient Soul** resolves as a pet
summon and returns before `ApplySpell` is reached at all.

So the recast had never worked for any spell, and there was no per-spell mystery to solve.

**The data was never the problem.** `RecastCentiseconds` is filled for 3,710 spells; measured against every
recast element in the RDB (3,279 spells' worth, direct + line inheritance), exactly **3** are unfilled and one of
those carries `cs = 0`. Life Leech holds 3000 cs = the 30 s its in-client tooltip renders.

**The problem was that the number never left the server.** `RecastCentiseconds` occurred in four non-comment
places, all in `HandleBccCastSpell`, all writing `Session.SpellReadyAt` — a server-side dictionary. Nothing wrote
it to a packet.

### ⭐ THE COOLDOWN IS `DispatchLockTemplate` — SOLVED, in-client verified

The recast is not a GameAction on the wire at all (0 across all 132 dumps, sub-records included). It is
**`GameCharInterface.DispatchLockTemplate` (0x204E)** — the message that greys a bar button and runs its sweep —
and we sent **none** of the 992 the corpus carries. The opcode existed in the enum and was named in exactly one
comment; nothing built it.

Every captured body is 8 bytes:

```
[u32 templateId][f32 secondsRemaining]          the float BIG-endian

0002fa65 41a00000  ->  195173 'Contract of Protection (Rank 1)', 20.0 s
```

Of the 741 bodies whose id resolves against `Spells`, **580 (78.3%) carry exactly that spell's
`RecastCentiseconds / 100`**. The other 161 are partials against the same spell (15.143 / 15.254 / 15.76 where the
recast is 20.0) — the time LEFT rather than the base, the same restore shape a long buff's duration takes (#601).

⚠️ **One per spell in the LINE, not one for the cast.** A `Contract of Protection` cast emits five bodies in a
row — 195173 / 195178 / 195179 / 195180 / 1151248, every rank of line 12 — each carrying the same 20.0. That is
the wire form of `SetSpellLineRecastTime` and why all ranks grey together.

The 251 bodies whose id is absent from `Spells` are **abilities and combos**: "template" is the client's word for
the thing on the bar, not specifically a spell.

Built by `LockTemplateCodec`, sent by `SendSpellCooldown` from the site in `Resolve()` that already arms the
server's own timer, carrying what REMAINS after the cast time. **In-client PASS 2026-08-09.**

### The two bugs found on the way, both real and both fixed

1. **A duration-0 ApplySpell POISONS the cast.** We sent one on every cast; the client answers
   `ERROR: CoreMagicHolder::ActivateBuff <id> remaining time 0` and aborts, losing the spell's own GameActions.
   Retail sends an ApplySpell only for a spell that has something to apply: of 5,423 s2c MagicBuffIIR bodies,
   5,416 carry a spell with a real duration and the 7 that do not are buffs whose duration is missing on OUR side.
2. **`StatDamage.SpellBaseId` was `base >> 8`.** The client said so on every spell hit —
   `GetMagicSpell failed to load spell with id 14321` (= Chill's 3666380 >> 8). Retail sends **0** there: 3,059 of
   3,061 records, and all 50 with the stat id we use.

### ⚠️ What is NOT yet matched: the CADENCE

The messages and their contents match retail. **When** they are sent does not, and neither gap is closed:

| | retail | ours |
|---|---|---|
| casts followed by a `LockTemplate` naming the cast spell or its line | 87% of buff casts, **41%** of nuke casts | **100%** of casts with a recast |
| BCC cast records per cast | 7–14 | 2 (begin echo + complete) |

The `LockTemplate` figures are attribution-based (the body must name the cast spell or a line sibling), not mere
proximity — but the window is 45 packets, so a long cast can fall outside it. Retail is plainly more selective
than we are and the rule is unknown; a cancelled cast costing no cooldown is the obvious candidate.

### ⛔ A version that INVENTED a recast element was reverted

Sending `SetSpellRecastTime` on cast makes the cooldown appear and passes in-client. It was still **wrong and is
reverted**: retail sends no recast GameAction, so shipping one is inventing behaviour the captures contradict.
"It works" is not evidence about retail. See the `retail_leads_never_invent_a_packet` agent memory.

The real mechanism is the LOCAL arm, and it is not yet pinned. What the RE has established so far:

- **`FUN_008b0f50` (the `SetSpellRecastTime` handler) has ZERO direct callers** — it is a virtual, reached
  through the GameAction element's Apply slot. So the element is applied by the generic machinery, and the
  question is what feeds that machinery the spell's own elements.
- **`FUN_008b0f50` bails out silently on a state check**, before reading anything:
  ```c
  if (*(int *)(param_1 + 0x1b0) != 2) { return; }   // no arm, no log
  ```
  `param_1` is the caster/player object (in the sibling `FUN_007e1230`, `param_1 + 0x30` is that object's spell
  list). A silent no-op on a state word matches the symptom exactly. What `+0x1b0` is has NOT been identified —
  287 object-field writes to that offset exist and none is attributable without the class.
- **`FUN_007e1230` (the line handler) carries no such guard.** It walks the spell list at `+0x30`, matches the
  line with `FUN_00768f90`, and arms each match with base + delta.
- The client's own cast path is `CoreMagicHolder::CastMagicSpell` (strings incl. *"CastMagicSpell Apply Spell
  effect from caster"*), which is where a locally-applied element would come from.

### The instrument that settles it

The client logs this path itself, and both channel names are verbatim-verified against `AgeOfConan.exe`:

| channel | what it prints |
|---|---|
| `GameCode/Magic/Recast` | `Setting recast timer on spell: <id> For player: <p> to: <cs>` |
| `GameCode/GameAction/SetSpellRecastTime` | `Modifying the recast timer by : <x>` · `The recast timer is already set to a higher value (spell = …)` · `SetSpellRecastTimeGameAction refers to nonexistant spell : <id>` |

```zsh
dotnet run --project Faolan.ClientLauncher -- --console --patch-universeaddr \
  --debug-channels GameCode/Magic/Recast,GameCode/GameAction/SetSpellRecastTime -u faolan -p any -c <char>
```

Cast a spell that has a recast, then read `AgeOfConan.log`. The three outcomes are disjoint and each names the
next step: **silence** = the element is never applied (the `+0x1b0` guard, or `CastMagicSpell` never reaches the
apply) · **"nonexistant spell"** = the client cannot resolve the id we granted · **"Setting recast timer"** = it
arms and something downstream clears it.

Open work tracked in Gitea #271 / #767.

Open work tracked in Gitea #271 / #767.
