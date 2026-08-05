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
below. Every other type the corpus carries (2, 3, 4, 5, 7, 0xA, 0xF, 0x10) goes straight to the commandId, which
is why reading them all one way looked right for so long.

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
[8 x u32]                                                  one float + two ids on some hits; else zero
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
exercises is walked — **100% of captured records, zero mismatches**. The one region decoded structurally but not
semantically is the attack block's eight middle words (open work tracked in Gitea #661).

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
