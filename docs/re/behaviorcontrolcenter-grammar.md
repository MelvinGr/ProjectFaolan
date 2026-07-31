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
  0xE                        : [u32 a (+0x24)] ; if a != 0 -> [u32 commandId] -> Command reader
  default                    : (nothing)
```

So a BccIIR = `[u8 type][u32 commandId?][command-specific data]`, where the command data is read by the
looked-up Command object's reader (another vtable hop). The combat-relevant forms (the attack/interact ack)
are already built byte-exact by `CombatService`/`BehaviorControlCenterCodec` (`BehaviorAcksTests`).

## The Command catalog — `InstantiateCommand::classmap` (#536)

`FUN_007dcd60` is `InstantiateCommand` (named by its own error string, *"InstantiateCommand::classmap is NOT
initialized!"*, log channel `Control`). The `commandId` above is a key into that map, and **two registration
functions populate it, pushing each command's NAME STRING next to its id** — so the whole catalog is readable
off the binary rather than inferred from behaviour:

* `FUN_007375b0` — 22 commands, the interaction/animation set.
* `FUN_01134510` — 12 commands, the combat/movement set.

All 34 are enumerated in `BehaviorId`. The names corroborate the corpus independently: `UseDynel` (0x12) carries
a 12-byte tail (a class+handle reference), `UseCharacter` (0x16) four (a handle), `PlayAnimation` (0x1D) a
*variable* 10–39 (an animation name), and `Emote` (0x05), `BccAction` (0x07) and `Limbo/Death` (0x1F) none at
all — those three alone are 55 699 of the 67 307 captured records.

⚠️ Two names this codebase had invented are corrected by it: `0x0F` is **Combat:Melee**, not "NormalAttack", and
`0x07` is **BccAction**, not "the idle pulse". `0x12` is **UseDynel**, not "an object-hold state".

## Tail census (all 67 307 captured records)

Most commands have a FIXED tail length: `Transition` (0x01) 20, `UseCharacter` 4, `UseDynel` 12, `Equip` (0x09)
88, and the three above 0. What varies is `Magic:Attack` (0x04), `PlayAnimation`, `ObjectInteraction` (0x2A) and
the three attack commands.

The three attack commands — `Combat:Melee` (0x0F), `Combat:Ranged` (0x10), `Combat:Unarmed` (0x11) — share one
tail family whose first word is a COUNT, and the stride differs by direction:

```
c2s request : base 28|32  + 8  x count      (counts 0..2 observed)
s2c ack     : base 40|41|44|81|82|85 + 49 x count
```

Subtracting `49 x count` collapses every one of the 3 700 s2c attack tails onto those six bases, and the bases
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
| `0x1F` | Limbo/Death | `LimboCommand_c` | `FUN_007dd5b0` | *nothing* | 0 |
| `0x16` | UseCharacter | `UseCharacterCommand_c` | `FUN_0081fdb0` | 1 × u32 | 4 |
| `0x12` | UseDynel | `UseDynelCommand` | `FUN_00832f80` | 3 × u32 | 12 |
| `0x09` | Equip | `EquipItemCommand_c` | `FUN_00838820` | 2 × u32, an item-identity sub-read (`FUN_005d5150`), 3 × u32 | 88 |
| `0x0F` | Combat:Melee | `AttackCommandMelee_c` | `FUN_01151170` | base + 7 × u32 + a counted record loop | varies |
| `0x11` | Combat:Unarmed | `AttackCommandUnarmed_c` | `FUN_01151170` | *(same reader)* | varies |

Two results fall straight out of that table and settle 83% of the traffic:

* **The empty tail is STRUCTURAL, not incidental.** The three commands the census shows with no tail share ONE
  reader, and it is a 3-byte `return;`. Those three are 55 699 of the 67 307 captured records.
* **Melee and Unarmed share a reader**, which is why `BehaviorAcks` measured exactly those two padding an echoed
  tail, and why the attack tail lengths form one family across `0x0F`/`0x10`/`0x11`.

The attack reader is the residue: `FUN_0084d5f0` (its base) reads 6 × u32, then 7 × u32 inline, then a COUNT
and a per-record loop. Sizing the record exactly needs the loop's conditional (`0x1b4`) resolved — it is skipped
on one path — so the count→length arithmetic is not closed yet.

## Status

Grammar SHAPE decoded (type switch + commandId + per-command reader); the commandId → COMMAND map is enumerated
with the client's own names; and the per-Command readers are walked for every FIXED-tail command (above), which
is 83% of the captured traffic. The residue is the attack family's counted record loop. Open work tracked in
Gitea #536.

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
