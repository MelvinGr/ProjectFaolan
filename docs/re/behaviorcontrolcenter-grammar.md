# BehaviorControlCenterIIR_c (0x642CD3D6) wire grammar — "BccIIR"

Recovered from `AgeOfConan.exe` (Ghidra). The Behavior Control Center IIR is the NPC/player **behavior
command** carrier (the combat/interact "BccAck"). Gitea #190.

## Reader — `BehaviorControlCenterIIR_c::ReadSubClass` (`FUN_005e24e0`)

```
[u8 behaviorType]        (+0x1c)   # selects the command form
switch (behaviorType):
  2,3,4,5,7,10,0xF,0x10,0x12 : [u32 commandId] -> look up the Command (FUN_007dcd60), then the Command's own
                               reader (vtable slot 0x80) reads its behavior-specific data
  6,8,9,0xD,0x11             : (terminal - no extra read)
  0xB,0xC                    : [u32] (+0x28)
  0xE                        : [u32 delay (+0x24)] ; if delay != 0 -> [u32 commandId] -> Command reader
  default                    : (nothing)
```

So a BccIIR = `[u8 type][u32 delay?][u32 commandId?][command-specific data]`, where the command data is read by
the looked-up Command object's reader (another vtable hop). The combat-relevant forms (the attack/interact ack)
are already built byte-exact by `CombatService`/`BehaviorControlCenterCodec` (`BehaviorAcksTests`).

⚠️ **Type `0x0E` is the one branch that puts an extra word in front of the commandId** — see "The delay phase"
below. Most other types the corpus carries (2, 3, 4, 5, 7, 0xA, 0xF, 0x10) go straight to the commandId, which
is why reading them all one way looked right for so long.

⭐ **`0x0B` ENTERS a substate and `0x0C` LEAVES it; the bare `u32` is the SUBSTATE ID** (#810). Both carry NO
command at all — the corpus has nine, 18-byte records that `BehaviorControlCenterCodec` refused outright until
#809, running out of bytes looking for an `argsLen` that is not there. The writer (`FUN_005e2650`) mirrors the
reader exactly: its `case 0xb: case 0xc:` writes one word and returns.

The client names the field itself, and the route in is a FIELD-xref rather than a call-xref — all nine records are
s2c, so no client-side *call* site names it. `ReadSubClass` stores the word at `this+0x28`, and exactly one other
function in the image touches that offset on this object: **`BccIIR`'s vtable slot 2, `FUN_005E1C70`, the
executor**. Its two arms are one predicate and its negation:

```c
case 0xb:  if (FUN_005dc110() == 0)  FUN_005EB5C0(bcc, this->word);   // enter
case 0xc:  if (FUN_005dc110() != 0)  FUN_005EB890();                  // leave
```

`FUN_005EB5C0` scans the BCC's **active-substate list** for the value, and its two log strings are both about that
argument:

```
"Tried to enter substate %d. Already in it!"                              <- the id is already in the list
"Tried to enter substate %d, but did not find it in this bcc (%d)"        <- the id does not resolve
```

On neither, it inserts the id into the list in priority order and activates it. The same list is what the
`:: Substate list ::` debug dump walks, printing ` %2d : %s` per entry — an id and a NAME.

Two independent corroborations: `FUN_005EB5C0` reads stats `0x14` and `0x15`, whose client-side names are
**`BCCState`** and **`BCC`**; and **`Substate` is already a known GameAction key** (174 / `0x4B6`) across ten
GameAction kinds in [`gameaction-keylists.tsv`](gameaction-keylists.tsv).

⚠️ **The id is scoped to the BCC, not global.** The client's own message says *"in this bcc"*, and the resolution
path is a per-BCC registry keyed by stat `0x15`. So the captured values cannot be named without the target's BCC
resource, and 60 appearing on four different actors in four captures is only a shared default *if those actors
share a behaviour tree* — do not build a global substate enum from them. The captured values are 60 ×4, 84 ×3 on
one Tortage NPC (`0x0B` once then `0x0C` twice, over the ~15 s before it dies — enter, leave, and a redundant
leave), and 33 and 83 once each.

⚠️ **The `default:` arm reads nothing**, so an unlisted phase byte ends the record at the phase word exactly like
the explicitly terminal `6/8/9/0xD/0x11` do. `CommandPhases` in the codec is therefore a POSITIVE list: guessing
the other way round would make an unseen phase parse as a command and swallow whatever followed it.

## The Command catalog — `InstantiateCommand::classmap` (#536)

`FUN_007dcd60` is `InstantiateCommand` (named by its own error string, *"InstantiateCommand::classmap is NOT
initialized!"*, log channel `Control`). The `commandId` above is a key into that map, and **two registration
functions populate it, pushing each command's NAME STRING next to its id** — so the whole catalog is readable
off the binary rather than inferred from behaviour:

* `FUN_007375b0` — 22 commands, the interaction/animation set.
* `FUN_01134510` — 12 commands, the combat/movement set.

**There is no third one.** Both sites inline the same four-call registration sequence
(`FUN_0040ec10(name, len)` → id → `FUN_007de4c0()` slot → factory pointer → `FUN_00708ec0(&id)`), and every
reference to either map helper is inside one of them — bar `FUN_007dcd10`, the out-of-line form of the same
sequence, which **nothing calls**. Cross-checked from the other end: `FUN_007dc7a0` is `CommandBase(id)`, and
decompiling all 69 of its callers enumerates every Command class the binary can construct. The highest id any of
them passes is `0x2F`, and exactly one class is built but never registered — `LockPickingCommand_c` (`0x2D`,
`FUN_0080b430` / `FUN_00849a60`), which the wire therefore cannot reach.

All 34 registered commands are enumerated in `BehaviorId`. The names corroborate the corpus independently: `UseDynel` (0x12) carries
a 12-byte tail (a class+handle reference), `UseCharacter` (0x16) four (a handle), `PlayAnimation` (0x1D) a
*variable* 10–39 (an animation name), and `Emote` (0x05), `BccAction` (0x07) and `Limbo/Death` (0x1F) none at
all — those three alone are 55,443 of the 65,877 captured records.

⚠️ Two names this codebase had invented are corrected by it: `0x0F` is **Combat:Melee**, not "NormalAttack", and
`0x07` is **BccAction**, not "the idle pulse". `0x12` is **UseDynel**, not "an object-hold state".

## Tail census (all 65,877 captured records)

⚠️ **This section said 67,307/55,699/3,687 until 2026-08-05. The corpus holds 65,877, and always did** — that
figure was never reproducible against the committed dumps (#666, resolved). Verified four ways: the same census
code from 2026-07-31 gives 65,877 against today's captures; the pre-re-dump dumps in the data repo's git history
give 65,877 too, with all 419,297 packet counts identical per capture; every BCC packet carries **exactly one**
record, so packet-vs-record counting cannot explain a gap; and no capture has ever been deleted. The likeliest
source is a pre-reassembly count off the raw pcaps — 1,430 is ~2.2%, a plausible retransmission rate — but the
original measurement predates `--report bcc-census` and cannot be re-run.

The **grammar is unaffected**: it is proven per-record, not by totals. Re-derive any figure here with
`--report bcc-census` rather than quoting it.

Most commands have a FIXED tail length: `Transition` (0x01) 20, `UseCharacter` 4, `UseDynel` 12, `Equip` (0x09)
88, and the three above 0. What varies is `Magic:Attack` (0x04), `PlayAnimation`, `ObjectInteraction` (0x2A) and
the three attack commands.

The three attack commands — `Combat:Melee` (0x0F), `Combat:Ranged` (0x10), `Combat:Unarmed` (0x11) — share one
tail family whose first word is a COUNT, and the stride differs by direction:

```
c2s request : base 28|32  + 8  x count      (counts 0..2 observed)
s2c ack     : base 40|41|44|81|82|85 + 49 x count
```

Subtracting `49 x count` collapses every one of the s2c attack tails onto those six bases, and the bases
themselves differ by 41 (81 = 40+41, 82 = 41+41, 85 = 44+41), i.e. one optional 41-byte block on a 40/41/44
head. `BehaviorAcks.HitResultBlock` builds the single-hit s2c form and is byte-exact against the capture.

## Per-Command readers — the hop, and the ones that are done (#536)

Each command's wire reader is **vtable slot 32** (byte offset `0x80`) of its Command class.
`CLASSES=<Class> SLOT=32 tools/ghidra/DumpVtableReaders.java` resolves and decompiles it in one run.

⚠️ Getting from the registration to the class is **two hops, not one**: the address the registration stores
(`MOV [EAX],<addr>`) is a **factory FUNCTION**, not a vtable — it disassembles as a prologue. Decompiling the
factory names the class through its RTTI (`*puVar1 = AttackCommandMelee_c::vftable`) *and* re-states the command
id in its base-ctor call (`FUN_0114c4d0(0xf)`), which cross-checks the whole catalog: every id embedded in a
factory matches the id its registration pushed.

| id | command | class | reader | reads | tail |
|---:|---|---|---|---|---:|
| `0x05` | Emote | `SocialCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x07` | BccAction | `BCCActionCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x0B` | Dance | `DanceCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x0C` | DisarmTrap | `DisarmTrapCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x15` | Combat:Evasive Maneuver | `MovementRollCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x1F` | Limbo/Death | `LimboCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x2E` | SwapAlternateEquipment | `SwapAlternateEquipmentCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x2F` | Combat:Reactive | `ReactiveCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x08` | Combat:Shield Taunt | `ShieldTauntCommand_c` | `FUN_0114ace0` | 1 × u32 | 4 |
| `0x16` | UseCharacter | `UseCharacterCommand_c` | `FUN_0081fdb0` | 1 × u32 | 4 |
| `0x25` | Revive | `ReviveCommand_c` | `FUN_00834420` | 2 × u32 | 8 |
| `0x0D` | SearchTrap | `SearchTrapCommand_c` | `FUN_00831710` | 3 × u32 | 12 |
| `0x0E` | UseItem | `UseItemCommand_c` | `FUN_008328b0` | 3 × u32 | 12 |
| `0x12` | UseDynel | `UseDynelCommand` | `FUN_00832f80` | 3 × u32 | 12 |
| `0x0A` | Mount | `MountCommand_c` | `FUN_0082f9f0` | 3 × u32 + u8 | 13 |
| `0x01` | Transition | `TransitionCommand_c` | `FUN_0082cb50` | 5 × u32 | 20 |
| `0x24` | Climbing | `ClimbingCommand_c` | `FUN_00835b70` | 5 × u32 | 20 |
| `0x09` | Equip | `EquipItemCommand_c` | `FUN_00838820` | 2 × u32, item-identity sub-read, 3 × u32 | 88 |
| `0x1E` | Sneak/Hide | `HideCommand_c` | `FUN_00833860` | 1 **or** 2 × u32, chosen by a virtual | 4 or 8 |
| `0x20` | Search | `SearchCommand_c` | `FUN_00833c50` | `[u8][u32 n][n × 8]` | 5 + 8n |
| `0x04` | Magic:Attack | `MagicCommand_c` | `FUN_01141060` | `[u64][u8 f][3 × u32 if f≠1][3 × u32][u8]` | 22 or 34 |
| `0x1D` | PlayAnimation | `PlayAnimationCommand_c` | `FUN_0082b700` | `[u8][u8][u32][string]` | 8 + len |
| `0x2A` | ObjectInteraction | `ObjectInteractionCommand_c` | `FUN_00836b70` | `[u32][str][3 × u32][str][u32]` | 24 + both |
| `0x0F` | Combat:Melee | `AttackCommandMelee_c` | `FUN_01151170` | the hit-result block, below | varies |
| `0x11` | Combat:Unarmed | `AttackCommandUnarmed_c` | `FUN_01151170` | *(same reader)* | varies |
| `0x10` | Combat:Ranged | `AttackCommandRanged_c` | `FUN_01146af0` | the same + one trailing `0x00` | varies |

Run over the corpus, these account for **every** captured BCC record with **zero mismatches** — the last ten
came in with the delay phase below (#549). Re-derive the numbers with
`dotnet run --project Faolan.Importer -- --report bcc-census`, which is this census.

⚠️ `PlayAnimation`'s string is the animation name (`sad`, `approve`, `social_conversation_gesticulate`) and
`ObjectInteraction`'s two are the action name plus its remoteformat (`lootbag_open`,
`<remoteformat token="startobjectaction(18)|onuse(2)|Treasure…`) — which is why those two were the
variable-length outliers in the census.

Two results fall straight out of that table:

* **The empty tail is STRUCTURAL, not incidental.** The three commands the census shows with no tail share ONE
  reader, and it is a 3-byte `return;`. Those three are 55,443 of the 65,877 captured records.
* **Melee and Unarmed share a reader**, which is why `BehaviorAcks` measured exactly those two padding an echoed
  tail, and why the attack tail lengths form one family across `0x0F`/`0x10`/`0x11`.

### The attack tail — `Combat:Melee` / `:Ranged` / `:Unarmed`

The reader takes a base read, seven u32, then a COUNT and a per-record loop. Decoded against the corpus:

```
[u32 targetCount][targetCount x (u32 class, u32 handle)]   the entities the swing REACHED
[8 x u32]                                                  the middle block - decoded below
[u32 resultCount][resultCount x 41-byte result]            one per entity actually affected
[Combat:Ranged only: u8 0x00]
```

so `len = 40 + 8*targets + 41*results` (+1 for Ranged). That accounts for **every** captured s2c attack tail
(3,313 today; 3,687 when the split below was measured). ⚠️ The middle is **8 words usually and 9 sometimes**, selected by its own second word — `1` → eight
(3659 tails), `2` or `3` → nine (28). Missing that selector leaves 11 bodies stubbornly unexplained, because a
mis-sized middle puts the result count one word out and the length then fits nothing. The 41-byte result is
`[u32 class][u32 handle][u32 0][u8 0x06][u32 resultType][u32 0][u32 k1][u32 damage][u32 k2][u32 0][u32 0]` —
exactly what `BehaviorAcks.HitResultBlock` builds, which is 81 bytes (a 40-byte head plus one result), not the
"132-byte result block" the code called it for years.

⚠️ **The two counts move together in most captures** — one extra target produces one extra result — so the
lengths look like a single 49-byte stride. **49 is 8 + 41, not one record.** Reading it as one stride is what
made this tail look unmodellable, and it is why the `0x1b4` conditional in the decompiled loop never had to be
resolved: the arithmetic closes without it.

The AIR SWING is a real captured shape, not just our workaround for #228: 130 tails carry zero targets *and*
zero results.

### The middle block, word by word (#661)

The client's reader is what places these: `FUN_0084d5f0` reads the target list and then words 0–1 (plus the
extra word when the selector says so), and `FUN_01151170` — the reader `Combat:Melee` and `:Unarmed` share,
which `Combat:Ranged` wraps — reads the remaining six and then the result count. Each lands in a named slot on
the Command object, and `--report bcc-attack-tail` prints what 3,313 captured s2c tails put in it:

| word | Command slot | what the corpus shows |
|---:|---|---|
| 0 | `+0x164` | a **float**, 0.02–0.93, 322 distinct values, zero on 2,899 — the swing's charge |
| 1 | `+0x144` | the **selector**: `1` ×3285, `2` ×19, `3` ×9. `> 1` inserts one extra word → `+0x148` |
| 2 | bits → `+0x1A0` (b4), `+0x1A1` (b5), `+0x198` (b6) | `0x00` ×2692, `0x20` ×602, `0x30` ×18, `0x70` ×1 — **no other bit is ever set**, which is what confirms the split. **Bit 5 tracks the swing's ARC** — see below |
| 3 | `+0x19C` | zero on 3,312 of 3,313; one sighting of `0x6BE` |
| 4 | `+0x44` | `0xC9`/`0xCA`/`0xCB` on 292 tails — the alphabet below, but **independent** of both word 5 and the result type (all nine combinations occur) |
| 5 | `+0xD0` | ⭐ the swing's **DIRECTION**, as an `Abilities` id |
| 6 / 7 | `+0x188` / `+0x18C` | read back together by one accessor (`FUN_011457f0`), so **one pair**; four sightings of `(0x003C7D4B, 2)` |

The bit split is not inferred from the values: the c2s WRITER (`FUN_01151030`) builds the same word as
`+0x198 ? 0x40 : 0`, and `FUN_011516a0` (the copy) moves `+0x1A0`/`+0x1A1`/`+0x198` as three **bytes**.

#### ⭐ Word 2's bit 5 fires only on the three UPPER arcs (#661)

Cross-tabbed against everything else the tail states, bit 5 (`0x20`) is near-independent of the actor, the
target count, the result count, the charge float and whether a direction *ability* is present — all of those
sit in a flat 15–27 % band. Against the **result type**, which is the swing's arc, it is not:

| result type | swings | bit 5 set | rate |
|---|---:|---:|---:|
| `0xC9` Upper Left | 712 | 195 | **27.4 %** |
| `0xCA` Upper Right | 656 | 169 | **25.8 %** |
| `0xCB` Overhead | 1,171 | 234 | **20.0 %** |
| `0xCC` Lower Left | 373 | **1** | **0.3 %** |
| `0xCD` Lower Right | 384 | **3** | **0.8 %** |

Four sightings in 757 lower-arc swings against 598 in 2,539 upper-arc ones — a **30× split** on the one axis
that is not flat. So whatever bit 5 marks, it is something only an upper-arc swing can produce.

#### ⭐ …and WORD 4 does the same thing, absolutely (#661)

Word 4's alphabet is `0xC9`/`0xCA`/`0xCB` — the three UPPER arcs and nothing else — and it is not merely
*correlated* with the arc, it is **never present at all on a lower one**:

| result type | rows | word 4 present |
|---|---:|---:|
| `0xC9` / `0xCA` / `0xCB` | 2,539 | 297 |
| `0xCC` Lower Left | 393 | **0** |
| `0xCD` Lower Right | 399 | **0** |

⚠️ That is a real constraint on top of "independent of the result type" in the table above, which is true only
*within* the upper three: all nine `(word 4, result type)` combinations occur there, and none at all outside.

⭐ **So `0xCC` and `0xCD` are structurally apart from the upper three**, on two independent fields, and the
client says why: `FUN_005edd90` rewrites `0xCC → 0xC9` and `0xCD → 0xCA` whenever the combo definition's level
is below `0x28` (40). The lower arcs are the later addition, and the code paths that fill word 4 and bit 5
predate them.

⚠️ **What it IS remains unnamed.** The shape fits a defender-side outcome — a guard meeting the swing — because
the rose has three guards and the two lower codes are the two arcs the captured players never had unlocked; a
block flag could then only ever fire where a guard could be. That is a hypothesis the corpus is consistent with,
not a decode: nothing here separates "blocked" from "critical", "glancing", or any other per-hit outcome, and
the 1-in-3 rate does not by itself pick one. #661 wanted this block decoded precisely because it is where a
crit/block/evade distinction would live; this narrows *where* to look without closing it.

Reproduce with `--report bcc-attack-tail`, whose *"word 2 against everything else the tail states"* block prints
the whole cross-tab over all 3,313 captured tails.

### ⭐ The result's "result type" is the swing's DIRECTION

Word 5's ids resolve in the client's own `Abilities` catalog — 769094 `Upper Left Attack`, 769095
`Upper Right Attack`, 769099 `Overhead Attack` — and the 41-byte result's own type word, long carried as an
opaque capture-derived code, is a **perfect function of it**, with zero exceptions:

| direction ability | result type | agreement |
|---|---|---|
| 769094 `Upper Left Attack` | `0xC9` | 159 / 159 |
| 769095 `Upper Right Attack` | `0xCA` | 87 / 87 |
| 769099 `Overhead Attack` | `0xCB` | 253 / 253 |

The corpus carries `0xCC` and `0xCD` as well, on swings with no direction ability, and the client ships exactly
two more of these abilities — 769096 `Lower Left Attack` and 769097 `Lower Right Attack` — so the alphabet is the
five-zone rose.

Split by ACTOR — classified per capture by whether the actor handle is a known NPC spawn, since a retail
character id (~`0x0131xxxx`) sits *below* `EntityHandles.CharacterInstanceBase` and the handle number line cannot
answer it:

| | word 5 (the ability) | the result type |
|---|---|---|
| **NPC-actor** (2,691 swings) | **never present** | `0xCB` ×896, `0xC9` ×545, `0xCA` ×511, `0xCD` ×384, `0xCC` ×373 |
| **player-actor** (515 swings) | present on 515 | `0xCB` ×275, `0xC9` ×167, `0xCA` ×145 |

So a mob names no ability — it has none — but it **does** state its swing's direction, in the result type, and it
uses all five zones where the captured players use the three they had unlocked. The arc is therefore observable,
which is what `CombatShieldRose`'s "nothing observes it" note was wrong about (#673): the ack now states
the direction the mitigation charged for, via `BehaviorAcks.SwingDirectionCode`.

### ⭐ The whole five-zone alphabet, from the exe [V1: tools/ghidra/DecompileFns.java]

The corpus pins three of the five codes and can never pin the other two — no captured player swings them, so no
ability id is ever carried beside them. The client settles it three independent ways:

| | recovered from | says |
|---|---|---|
| the **direction → code switch** | `FUN_005e3b30` | `1 → 0xC9, 2 → 0xCA, default → 0xCB, 4 → 0xCC, 5 → 0xCD` over an internal 1..5 enum |
| the **config key block** | `FUN_0152f740` | registers `attack_dir_bm_upper_left`, `_upper_right`, `_head`, `_lower_left`, `_lower_right` — in that order |
| the **combo step parser** | `FUN_005edd90` | rewrites `0xCC → 0xC9` and `0xCD → 0xCA` when the definition's level is below `0x28` (40) |

The first two give the order — the same 1..5, and its first three members are exactly the codes the corpus pins,
so the remaining two follow. The third is the stronger argument and does not depend on the order at all: the fold
sends each lower code to the **upper code on its own side**, so `0xCC` is a LEFT zone and `0xCD` a RIGHT one.

That fold is retail's own progression rule — a character swings in three directions and unlocks the two lower
zones later — and it is why every captured player swing carries only `0xC9`/`0xCA`/`0xCB` while mobs, which are
not gated, use all five.

`FUN_005e3b30` also carries the client's "pick one" path: direction `10` means `rand() % 6 + 1`, and since cases
3 *and* 6 both fall to `0xCB`, **overhead draws double**. The captured mob distribution's overhead share is
33.1 %, which is that 2/6 to within noise.

### The mob distribution, and what it is uniform in

`--report bcc-attack-tail` collapses the five zones onto the three guard arcs by the client's own rule above:

| arc | swings | share | |
|---|---:|---:|---|
| Left | 918 | 33.9 % | Upper Left ×545 + Lower Left ×373 |
| Mid | 896 | 33.1 % | Overhead ×896 |
| Right | 895 | 33.0 % | Upper Right ×511 + Lower Right ×384 |

χ² = **0.37** on 2 df against a uniform arc pick. So the arc is uniform and the apparent skew in the raw five-way
counts is the *height* axis, which the three-digit rose does not model: within the two side arcs the corpus is
1,056 upper / 757 lower, i.e. **58.2 % upper** (n = 1,813, ~7σ off an even split). Why it leans upper is not
explained — it is measured, not derived.

⚠️ It is **not** a per-mob repertoire: 55 of the 56 templates with ≥ 8 captured swings use all five zones, so
there is no "this archetype only swings three" rule to key it on.

### The delay phase — behaviorType `0x0E` (#549)

`case 0xe` reads a u32 into `this+0x24` *before* the commandId, and the IIR's apply pass (`FUN_005e1c70`) spends
it like this:

```c
case 0xe:
  pct = (float)*(int *)(this + 0x24) * 0.01;
  if (*(double *)(command + 0x70) != 0.0) { /* FUN_00730930 */
      command->SetTime(3, command->time[3] + pct);   // vtable slot 0x8c; case 3 falls into case 2
      command->SetTime(4, command->time[4] + pct);
  }
```

`SetTime` (`FUN_007ddc60`) is an indexed setter over the Command's **six timeline doubles** at `+0x58`, `+0x60`,
`+0x68`, `+0x70`, `+0x78`, `+0x80`. So the word is a **hundredths-of-a-second delay applied to a command already
in flight** — it shifts indices 2, 3 and 4 later, and it is skipped outright when index 3 is zero.

Ten records carry it, all s2c, all on a command the client is mid-way through: seven `Magic:Attack` casts and
three treasure-chest `ObjectInteraction`s, at 50, 53, 57, 58 and 71 (0.50–0.71 s).
`--report bcc-census --report-arg 0E` prints all ten in full; `BehaviorDelayPhaseTests` locks two of them.

⚠️ **This is what the "five unregistered command ids" were.** Read without the extra word, the delay lands where
the commandId belongs, so the ten records showed up as commands `0x32`, `0x35`, `0x39`, `0x3A` and `0x47` —
values nothing in the client registers, which is the "third registration site" #549 was opened to find. There is
no third site. Re-split, all ten are `Magic:Attack` (a 22-byte `f == 1` tail) or `ObjectInteraction` (185 bytes),
both lengths the corpus already showed on the ordinary phases — the arithmetic closing on grammars decoded
*before* this was found is what makes the split right rather than merely plausible.

## Decode coverage

Grammar SHAPE decoded (type switch + optional delay + commandId + per-command reader); the commandId → COMMAND
map is enumerated with the client's own names and shown to be complete; and every per-command reader the corpus
exercises is walked — **100% of captured records, zero mismatches**. The attack block's eight middle
words are decoded too (#661) — each to the Command slot the client stores it in, with word 5 and the result type
resolving to the client's directional-attack abilities. What is left there is word 4's alphabet and the
`+0x188`/`+0x18C` pair, both of which are four sightings or fewer.

⚠️ **The SCOPE test does not apply to word 4, and the control is what says so.** The obvious next move is the
method that settled the MagicBuff payload's two level slots — *whose field is it? group the value by each
candidate owner and see which grouping it is stable within* — with word 4 as "the defender's guard arc" against
"a second attacker property". `--report bcc-attack-tail` runs it, **with word 5 (a known attacker-side swing
direction) as the control**, and word 5 comes out just as unstable per attacker (68.4% of 19) as per defender
(66.7% of 18). A control that cannot find its own answer measures nothing, so neither reading is supported or
refuted: both words are rolled **per SWING**, not held as a property of an entity, which is the shape scope
stability cannot see. The populations are also far too small for it either way.

**The client route was then run, and it narrows rather than answers.** `FindOffAccess` (`OFF=44 MODE=r`) over the
whole image gives 14,961 hits — `+0x44` is a ubiquitous struct offset — but restricted to the Command class's own
address neighbourhood (`0x0114xxxx`–`0x0115xxxx`) and to REGISTER-relative accesses (a stack local at `[ESP+0x44]`
is not this field), the only three functions that touch it on this object are:

| function | what it does with `+0x44` |
|---|---|
| `FUN_01151170` | the s2c tail READER — `MOV [ECX+0x44],EAX`, i.e. the only writer |
| `FUN_01151030` | the c2s WRITER — reads it back and puts it on the wire, third of four, between `+0x19c` and `+0xd0` |
| `FUN_011516a0` | the copy — `MOV EAX,[EDI+0x44]; MOV [ESI+0x44],EAX`, a plain dword |

So the field is **stored, transported both ways and copied, and nothing in the Command's own module reads it for
behaviour.** (The other `[reg+0x44]` hits in range belong to unrelated objects — `FUN_01150340` and `FUN_0115277x`
show the `MOV EDX,[EDX+0x44]` / `LEA ECX,[ESI+0x44]` pair that is a string or container member, not this.)

### ⭐ The class is `AttackCommandMelee_c`, and the by-class route ELIMINATES one of the two readings

The step above was taken. The tail reader `FUN_01151170` is referenced from exactly one data address —
`0x0204255c`, a vtable slot — and that vtable starts at `0x020424dc`, whose RTTI CompleteObjectLocator names
`.?AVAttackCommandMelee_c@@`. Its siblings, from the slot sweep below, are `AttackCommandBase_c`,
`AttackCommandBaseArmed_c`, `AttackCommandRanged_c` and `AttackCommandUnarmed_c`.

⚠️ **And the earlier restriction was the flaw in the previous pass.** `+0x44` is a field of the BASE, not of the
derived class — the Command base constructor `FUN_007dc7a0` zeroes it (`MOV [ESI+0x44],EBX`) alongside `+0x48`,
`+0xb4`, `+0xbc` and `+0xf4`. The base's methods live at `0x007dxxxx`–`0x007exxxx`, which the offset sweep above
had dismissed as "unrelated objects". So the negative result was measured over the wrong band.

Redone properly — **all 68 functions in the class's vtable decompiled**, plus every register-relative `[reg+0x44]`
access in *both* bands:

| what touches `Command + 0x44` | |
|---|---|
| `FUN_01151170` | the s2c tail READER — the only writer |
| `FUN_01151030` | the c2s writer — reads it back onto the wire |
| `FUN_011516a0` | the copy |
| everything else in range | a **virtual call at vtable offset `0x44`** (`(**(code **)(*obj + 0x44))(…)`, which is slot 17 and has nothing to do with the field), or a `LEA` of a sub-object on a different struct |

⭐ **So the "consumer behind a virtual call" reading is eliminated: the class's entire virtual surface has now been
read and none of it consumes the field.** What stands is that the client only ECHOES the word — it is a value the
SERVER decides and the client is a courier for.

That retro-explains every corpus test on this ticket at once. The scope-stability test failed even on its own
control because word 4 has no client-side owner to be stable within; "independent of word 5 and the result type"
is what a server-chosen value looks like from the client side.

### ⭐ Word 3 is echo-only too, and words 6/7 are an OPTIONAL two-word reference

The same sweep over the class settles the other two open slots:

* **word 3 (`+0x19C`)** — read by the c2s writer and by nothing else. Same answer as word 4.
* **words 6/7 (`+0x188` / `+0x18C`)** — the class exposes them through a matched accessor PAIR:

```c
slot 0x9C  FUN_011457e0:  return *(int *)(this + 0x188) != 0;              // a presence test
slot 0xA0  FUN_011457f0:  out[0] = this->0x188;  out[1] = this->0x18c;     // the pair getter
```

`DumpVirtualSlotAcrossClasses.java SLOT=0xa0` says what that slot IS across the Command hierarchy: `Command_c`
and 34 sibling command classes inherit `FUN_007de050`, which writes `(0, 0)` — *"I have no such reference"* — and
only the five Attack commands override it to return the field. So it is an **optional two-word reference that
only an attack carries**, which corroborates the `(class, handle)` shape the corpus's four `(0x003C7D4B, 2)`
sightings suggested, from the client's own structure rather than from the value.

⚠️ **And nothing calls it.** There are **eight** `CALL dword ptr [reg + 0xa0]` sites in the whole image and not
one is in the combat module. So the pair is written, exposed and never consumed either.

### The block, split by whether the client uses it

| words | |
|---|---|
| 0, 1, 2, 5 | **consumed** — the charge float, the selector, the three bit flags, the direction ability |
| 3, 4, 6, 7 | **stored, transported both ways, copied, exposed through accessors, and never read** |

That is as far as the client can take these four. A meaning for them is a SERVER-side fact, and the only evidence
that could carry it is a capture where one of them varies against something observable — the corpus has 1, 292
and 4 sightings respectively.

Three of the client's branches have no captured example, so nothing here is claimed about them beyond what
`ReadSubClass` shows: types 6, 8, 9, 0xD and 0x11 read nothing after the type byte, and 0xB/0xC read one u32 into
`+0x28` and no commandId. `BehaviorControlCenterCodec` does not special-case them — it would throw rather than
misread, because both shapes run out of bytes where it expects the args length.

## Registration form (the create-burst / world-entry BccIIR) — #234

Distinct from the combat BccAck above: the create-world cutscene and world-entry bursts REGISTER behaviors on
scene entities with a fixed-shape record (decoded by `tools/capture/decode_behaviorcc.py`, built by
`BehaviorAnimations.Registration`):

```
[u32 C350][u32 handle][u16 form][u32 slot][u32 msgLen][protobuf][optional tail]
  form     = 0x0004 (common) | 0x0007 (rarer subtype: adds protobuf field 4, a varint)
  protobuf = base varints  f1=behaviorId  f3=phase  f5=field5  f6=field6
             + OPTIONAL     f2, f11, f12        (wire order: 1,[2],3,5,6,[11],[12])
  tail     = 0 | 20-byte "bound" [u32 boundA][u32 boundB][12x 00] | 88-byte item-bound long form
```

Form census across the hoxxling create-world cutscene (90 records): 41x {1,3,5,6,12} no-tail, 34x {1,3,5,6}
no-tail, 7x {1,3,5,6,12}+bound20, 4x {1,3,5,6,11,12}+bound20, 1x {1,3,5,6}+bound20, 1x {1,2,3,5,6,12}+bound20,
1x {1,3,5,6}+long88, 1x form=0x0007. `BehaviorAnimations.Registration` constructs every form-0x0004 record (base + optional 2/4/11/12 + 20-byte bound
tail) AND the 0x0007 subtype (via `form: 0x0007` + `field4`); `BehaviorAnimations.ItemRegistration` builds the
88-byte item-bound form (its trailing 3 u32 parameterized for the cutscene's non-zero state). All 90 cutscene
records are now constructed byte-exact (the #234 golden harness locks them).

## The two AMBIENT commands — what makes a standing NPC look alive (#766 / #225)

`Emote` (0x05) and `BccAction` (0x07) are between them **63,743 of the corpus' 76,262 BCC records** — more than
everything else on this channel put together. Both carry a `CommandMessage` and, per the table above, **no tail**,
so a record is completely described by its command, its `bcc_action_id` and its timing. `--report
ambient-animation` measures both.

They are **not** two spellings of one thing, which is what this codebase assumed for a long time:

| | `BccAction` 0x07 | `Emote` 0x05 |
|---|---|---|
| records / actors | 37,965 over 7,969 | 25,778 over 2,909 |
| records per ACTOR | **p50 1**, p90 2 | p50 3, p90 15 |
| seconds between one actor's | p50 0.0 (they arrive in a burst) | **p50 9.4** (p10 3.6, p90 36.9) |
| `bcc_action_id` | 16 distinct, 41413 dominant | **126 distinct**, p50 3 per creature (max 21) |
| `activation_time` | on 84 of 37,965 | on **25,562** of 25,778 |
| while the actor is standing | 35,617 of 37,858 | 24,432 of 25,750 |

So **`BccAction` is a one-shot assert** — retail states the fixed idle behaviour once as an entity comes into view
and then leaves it alone. It was modelled here as a ~1/s *heartbeat* per visible NPC, which is a shape retail never
sends (#716 capped the resulting flood; this is why it existed).

**`Emote` is the recurring one**, and it is the ambient random animation: a citizen leaning on a wall, scratching,
looking round. Its cadence is stated in the data rather than chosen by the sender — over 22,696 consecutive pairs
of one actor's records, `gap / previous activation_time-as-centiseconds` has **p10 exactly 1.0** and falls below 0.9
on 121 (0.5%), i.e. the next animation never starts before the last has finished. The creature then STANDS for
p10 0.1s / **p50 2.2s** / p90 19.0s and plays another. `activation_time` is therefore the animation's own LENGTH,
and it belongs to the animation: of the 118 ids, 76 state exactly one value and none states more than two.

The animation ids are per creature and in the same space as `NpcStatCore.AnimIdleId` (41-42k, 62k, 126k, 216k).
`Citizen` 14522 plays six of them, weighted — 62171 and 62172 about 570 times each against 182 for 62188.

Built by `BehaviorAnimations.AmbientEmote` (byte-exact against frame 90 of `NecroL80_..._chat-annotated-3`), stored
per creature type as `NpcAmbientAnimation` (harvested by the `npc-ambient-animations` module), and driven by
`WorldState.Tick`.
