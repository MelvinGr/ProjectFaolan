# GM commands: `gm_level` is a BITMASK (#448/#449)

The retail client's GM surface is gated by one wire value — character stat `0x0D7`
(`CharacterStatId.GmLevel`), which rides along as protobuf `player_stats.gm_level`. It is **not a level**. It is a
**bitmask of privileges**, and a command is offered when

```
(gmLevel & command.requiredMask) != 0
```

with `requiredMask == 0` meaning unrestricted. Both halves come out of the client:

- **the bits** are registered by `FanaticGMLevelCommand_c`'s constructor (Ghidra `FUN_006336d0`) — the object behind
  the `/gm` command — as a literal name→bit table;
- **the AND** is the filter in the `/help` renderer (`FUN_006c86b0`), which reads `GetStat(0xD7, 0)` once and tests it
  against each registered command's mask word before listing it.

This retires the earlier "the command surface is tiered, `/kill` needs a higher level than 5" reading, which was an
artefact of reading a mask as a magnitude. At `gm_level = 10` (`PlayerResurrect | TeleportDynels`) `/kill` is refused
not because 10 is too low, but because 10 does not contain `NPCCommands`.

"Fanatic" is the client's own name for its GM/admin subsystem — it also names the `Fanatic` debug channel and the
tweak table's `TWK_karma_Fanatic*` entries. Nothing to do with player-facing content.

## The privileges

Modelled as `GmPrivileges` (`Faolan.Core/Enums/GmPrivileges.cs`).

| bit | name | | bit | name |
|---|---|---|---|---|
| `0x00000001` | Developer | | `0x00002000` | AssistSummon |
| `0x00000002` | PlayerResurrect | | `0x00004000` | ChangeName |
| `0x00000004` | TeleportSelf | | `0x00008000` | ChangeNick |
| `0x00000008` | TeleportDynels | | `0x00010000` | ShowGMTag |
| `0x00000010` | PlayerDamage | | `0x00020000` | EffectCommand |
| `0x00000020` | GetStats | | `0x00080000` | NPCCommands |
| `0x00000040` | SetStats | | `0x00100000` | CanCloak |
| `0x00000080` | InvisibleToMonsters | | `0x00400000` | CanKick |
| `0x00000100` | ImmuneToAOE | | `0x01000000` | GMChat |
| `0x00000200` | GodMode | | `0x02000000` | NoCriteriaCheck |
| `0x00000400` | Broadcast | | `0x04000000` | CityCheck |
| `0x00000800` | Sitecast | | `0x10000000` | HideFromCommunityServer |
| `0x00001000` | ACGspawn | | | |

Seven bits are used by the shipped roles below but never named by the client — `0x00040000`, `0x00200000`,
`0x00800000`, `0x08000000`, `0x20000000`, `0x40000000`, `0x80000000`. They are deliberately absent from the enum
rather than guessed at.

### ⛔ `GodMode` (`0x00000200`) is not a UI permission — it is client-side invulnerability

Every other bit gates which commands `/help` offers. This one changes what the client does with the WIRE. Its
`HealthCurrent` handler opens by testing it, and refuses any decrease while it is set:

```
FUN_010fd220 @010fde3a          the LOCAL PLAYER's stat setter (vtable 0x0203ec38 slot 3)
    GetStat(0xD7)               215 = GM Level, the privilege mask
    TEST EAX,0x200              GodMode
    JZ   normal
    CMP  EDI,[ESP+0x24]         new vs old
    JL   return                 new < old  ->  DISCARD
```

So a character carrying it takes damage server-side and **dies**, while its health bar never moves — the bar
keeps whatever the world-entry snapshot seeded. That was #902, and it cost three sessions of packet analysis
because nothing on the wire is wrong: the packets are byte-identical to retail, the handle and the ×100 scale
are right, and the client simply drops the value.

⚠️ It is in BOTH roles an account type can map to — `all` (`0xFFFFFFFF`) and `support2` (`0x0951FFEE`) — so every
GM had it, because the mask used to be DERIVED from the account type. It is now stored: `Account.GmLevel` is the
whole GM state - the mask, or NULL for an account that is not a GM - so `GodMode` is simply the bit it always was
and the client's invulnerability cannot disagree with the server's `.godmode`. There is no second copy to drift.

⭐ Worth knowing generally: **`GetStat` returns `-1` for a stat the client does not hold**, and `-1` has every bit
set — so *failing to send* stat 215 grants every privilege including this one. A missing GM level is not a
neutral default.

### Which bits do anything BEYOND gating `/help`

Naming a bit is not knowing what it does. Every site that reads the mask was scanned (`PUSH 0xd7` ahead of the
getter, then the bit it tests — both `TEST reg,imm` and `AND reg,imm` spellings):

| bit | privilege | sites | what it does there |
|---|---|---|---|
| `0x00000200` | `GodMode` | 5 | **3 decrease gates in the local player's stat setter** `FUN_010fd220` — health `@010fde41`, stamina `@010fe513`, mana `@010fe544`, each `TEST 0x200` then `CMP new,old; JL return`; plus 2 in `FUN_008d4820`, the damage-number path (it checks stat `0x1b` and a negative delta) |
| `0x02000000` | `NoCriteriaCheck` | 2 | `FUN_006dd970` |
| `0x00010000` | `ShowGmTag` | 2 | `FUN_01104080` |
| `0x00000001` | `Developer` | 4 | |

Every other read has no immediate bit test: that is the `/help` filter `FUN_006c86b0`, which reads the mask once
and ANDs it against each registered command's own required mask (the 133-command registry, already decoded).

⭐ **None of the seven unnamed bits appears in a hard-coded test anywhere** — they are role membership only, with no
special client behaviour. That is a useful negative: the unnamed bits can be carried verbatim in the role masks
without worrying what they switch on.

## The roles the client ships

Registered beside the bits, so `/gm support2` names one. Verbatim values — each set is Funcom's own policy, which is
why they are recorded as roles rather than rebuilt from flags. `GmRoles` in the same file.

| role | mask | |
|---|---|---|
| `all` | `0xFFFFFFFF` | every bit, including the seven unnamed |
| `dev` | `0x09011075` | |
| `internal` | `0x000BC0EF` | |
| `volunteers1` | `0x010123A4` | |
| `volunteers2` | `0x010123A4` | registered as a straight copy of `volunteers1` |
| `support1` | `0x0941F3A6` | |
| `support2` | `0x0951FFEE` | registered as `support1 \| 0x00100C48` |
| `operations` | `0x09410C20` | |

`Account.GmLevel` stores one of these verbatim, or any custom mask (the admin UI edits it a bit at a time). The
roles below are offered as presets — `support2` is the widest of the
shipped non-developer roles, carrying the moderation set without the `Developer` bit that gates the ~85 debug
commands.

## Server-side gating (#904)

The bits bite server-side, not only in the client's `/help`: every entry in our command table
(`GameServerListener.GmCommands.cs`) carries the gm_level bit it requires (`GmCommandMasks`), and the dispatcher
refuses a verb whose mask the caller's `Account.GmLevel` does not contain — `mask == None` means UNRESTRICTED
(any GM), the client's own convention. The ten verbs that are also retail verbs reuse retail's mask exactly
(pinned against [`gm-command-registry.tsv`](../re/gm-command-registry.tsv) by `GmCommandTableTests`); the rest
are our own verbs, so their bit is a family CHOICE, commented as such in the table, not a recovered fact. Our
`.help` lists only what the caller's mask passes, like the client's own filter.

## The command catalog

133 commands, all registered by one function (`FUN_0064e2d0`) as **16-byte** records — `[0] vftable, [4] 0,
[8] requiredMask, [0xC] u16 mode` — pushed into the interpreter's list. ⚠️ The record was long documented as 12
bytes, which is what the allocation (`PUSH 0x10`) refutes: the **u16** at `[0xC]` is a **variant selector**
and is the only thing telling apart verbs that share a class (see [the argument grammar](#the-argument-grammar)).
⚠️ It is a **WORD, not a byte** (#923) — the registration writes `MOV word ptr [EAX + 0xc], …`, and reading it a
byte wide truncates `/broadcast`'s `0x0100` to `0` and misses a word-write of `1` altogether. That failure is the
dangerous kind: it does not error, it produces a plausible value, and the value it produces is exactly what a
class with no selector legitimately has. Recovered with
`tools/ghidra/DumpCommandRegistry.java`; re-run it to regenerate this table. The raw dump, with each command's
implementing class and its registration address, is [`docs/re/gm-command-registry.tsv`](../re/gm-command-registry.tsv).

### The argument grammar

The registry gives verb, class, vftable and mask — and nothing about what each verb PARSES. That gap is what made
`/teleportself` refuse the client's own map UI: right-click a map → *teleport to location* emits
`/teleportself <X> <Z>`, and we answered `No map matching '534 657'` because we read the argument as a map name.

Every command class declares its parameters in one vtable slot, as a run of string appends —
`open literal, close literal, help text, parameter name`, **four per parameter**, after a two-append preamble that
wraps the command's own description. So the ordered append sequence IS the grammar, and the usage line rebuilds as
the concatenation of `open + name + close`. That reconstruction self-validates: a correctly read class comes out
with balanced brackets. `tools/ghidra/DumpCommandArgs.java` walks the same registry function as
`DumpCommandRegistry.java`, then walks each class's vtable, and writes
[`docs/re/gm-command-args.tsv`](../re/gm-command-args.tsv) — verb, mask, class, vftable, status, usage, short and
long description, and the parameter list.

All 133 verbs come out with something usable:

| status | verbs | meaning |
|---|---:|---|
| `ok` | 89 | every parameter read, brackets balance |
| `noargs` | 26 | the preamble and nothing else — the verb declares no parameters |
| `partial:n` | 6 | every complete parameter read, `n` trailing appends left over (the last parameter's name arrives through a different string helper) |
| `ok+unbalanced` | 4 | every parameter read, but retail put the closing `]` inside the HELP text (`"Clears 1 death effect]"`) — a data quirk, not a misread |
| `partial:n+unbalanced` | 2 | both of the above |
| `no-appends` | 6 | the class declares its usage some other way; retail's own long description documents these |

⚠️ **A `noargs` row means the verb declares no parameters, not that it accepts none** — `/lua` and
`/setlookspackage` plainly take an argument each and parse it ad hoc. The declaration is what is recovered.

⚠️ **`/lua`'s description is its LONG one.** `FanaticLuaCommand` declares no short description at all, so the
catalog falls back to the long form ("Run Lua script on both client and server."). It is the only class in the
133 where that fallback applies.

### ⭐ Three verbs carry another command's description — and it is retail's bug, not the dumper's

`/jumpspeed` documents itself as *"Command for overriding the jump height."*, with the parameter
`Integer: The new height cap for jumping`. That reads like a misattribution in our recovery. It is not: Funcom
cloned the class and left the strings pointing at the original.

The test that separates the two explanations is **how many code sites PUSH the string**. One command class's
constructor pushes its short and long description ~`0x33` apart, so a string pushed at two sites is used by two
different classes, while a string pushed once is shared by whatever verbs that ONE class registers under:

| string | push sites | reading |
|---|---:|---|
| `Command for overriding the jump height.` | 2 | `FanaticJumpHeightCommand_c` **and** `FanaticJumpSpeedCommand_c` — two classes |
| `Commands for pet control.` | 2 | `FanaticPetCommand_c` **and** `FanaticGameTweaksCommand_c` |
| `Gives the target a temporary ress point.` | 4 | `FanaticRessCommand_c` + `FanaticConsumeMemoryCommand_c`, each pushing it as short *and* long |
| `Teleports a dynel` | 1 | ONE class (`FanaticTeleportCommand_c`) under four verb names — an alias, not a clone |
| `Not allowed to change/set this stat with /set` | 1 | `FanaticSetCommand_c` → `/set` + `/setlocal` |
| `Reload some or all of the tables` | 1 | `FanaticReloadCommand_c` → `/reload` + `/lazyreload` |

Corroborating the clone reading: `Command for overriding the jump height.` occurs **once** in the whole binary and
there is no "jump speed" description anywhere; and `FanaticGameTweaksCommand_c` has its own long description
(`Commands for game tweaks `, with the `set`/`get`/`reload`/`list` lines) while borrowing the pet short one.

⛔ **Do not "correct" these three.** `CataloguedVerbs_MatchTheRecoveredGrammar` pins them, and it is mutation-checked
in both directions (editing a description or a usage line fails it).

### The reconstruction is complete, not truncated

Eight usage lines come out with unbalanced brackets, which invites the conclusion that the walk is dropping
appends. It is not. Two independent checks:

* **The append stream divides evenly.** For six of the eight, `(appends − 2) % 4 == 0` — every four-append
  parameter tuple is whole. Only `/spawn` (2 over) and `/token` (1 over) end ragged, which is what `partial:n`
  already reports.
* **The call sites are all accounted for.** Disassembling the two ragged classes and counting `CALL 0x40ec10`
  (the append helper) gives **44 sites for `/spawn` and 44 recovered; 27 for `/token` and 27 recovered.**

So the unbalanced lines are retail's grammar. Two are the `]`-inside-the-help-text typo above; the rest
(`/pet`, `/space`, `/token`, `/spawn`, `/dataset`, `/resurrectionpoint`) simply never append a closing literal —
`/dataset` renders `client|server|[setdataset]` because the `set` parameter's suffix and the `dataset`
parameter's prefix are both empty, so the two names abut.

⛔ **Do not add a "usage brackets balance" gate.** It would fail on a faithful recovery and reward corrupting it.

**Five classes serve more than one verb**, and the record's variant selector at `[0xC]` is what separates them —
`0` is the target/server form and `1` the self/local one:

| class | mode 0 | mode 1 |
|---|---|---|
| `FanaticTeleportCommand_c` | `/teleporttarget` | `/teleport` `/teleportself` `/tp` |
| `FanaticSetCommand_c` | `/set` | `/setlocal` |
| `FanaticSetnameCommand_c` | `/setname` | `/setnick` |
| `FanaticReloadCommand_c` | `/reload` | `/lazyreload` |
| `FanaticBroadcastCommand_c` | see below — a **word**, not a byte | |

### ⭐ The selector is a WORD, and that is why the broadcast trio looked identical (#919)

The trio was long recorded as "the one family the byte does not separate — all three are mode 0". They are
separated; the DUMP was reading a byte where the registration writes a **16-bit** value, which truncated the
discriminator away. ✅ **`DumpCommandArgs` reads it as a word since #923**, so
[`gm-command-args.tsv`](../re/gm-command-args.tsv) now carries `256 / 0 / 1` for the three and
`GmCommandArgumentGrammarTests` gates the whole catalog with no family excused. The other four multi-verb
classes were re-checked in the same regeneration and none moved, so `/sitecast` — whose `word`-write the byte
filter had missed entirely, leaving it at the previous record's 0 — was the only other casualty. The three
registration sites write `word [obj+0xC]` distinctly:

| verb | mask `[8]` | word `[0xC]` | `[0xC]` | `[0xD]` |
|---|---|---:|---:|---:|
| `/servcast` | `0x400` | `0x0000` | 0 | 0 |
| `/broadcast` | `0x400` | `0x0100` | 0 | 1 |
| `/sitecast` | `0x800` | `0x0001` | 1 | 0 |

And the class's own description getter (`FUN_00637270`) branches on the two bytes and **names each scope**:

```
[0xC]=0 [0xD]=0  ->  "Broadcasts a message to the server"
[0xC]=0 [0xD]=1  ->  "Broadcasts a message to the playfield"
[0xC]=1 [0xD]=0  ->  "Broadcasts a message to the dimension"
anything else    ->  "ERROR, impossible combination of flags"
```

So the hierarchy is **playfield ⊂ server ⊂ dimension**, and `/broadcast` — the one that sounds widest — is the
**narrowest**. A single-verb class writes nothing at `[0xC]` at all (checked on the knockback pair), so the
selector exists only where a class has verbs to tell apart, which is what the rule always said.

⚠️ `/sitecast`'s dimension is wider than one realm and a realm host holds only its own players (#324), so ours
reaches this realm and says so rather than implying a cross-realm fanout that does not exist.

The teleport family — one class, `FanaticTeleportCommand_c`, serving all four verbs:

```
/teleport posx posz [[y posy][playfield]] [proxy model type group subgroup running]
```

| parameter | type | help |
|---|---|---|
| `posx` | Integer | X position to teleport to |
| `posz` | Integer | Z position to teleport to |
| `y` | Integer | Spesifies a y coordinate as well *(sic)* |
| `posy` | Integer | Y position to teleport to |
| `playfield` | Integer | PlayfieldID to teleport to |
| `proxy` | Literal | Does a proxy teleport |
| `model` `type` `group` `subgroup` `running` | Integer | (no help text) |

So **X and Z are the mandatory pair and Y is optional**, supplied behind a literal `y` token, with the playfield
after it — which is exactly why the map UI sends two numbers and no height. Its long description adds: *"Teleports
self or another dynel. If playfield ID is greater than 65535 you have to use teleport proxy."*

Our `.teleport` accepts retail's forms (`<x> <z>`, `<x> <z> y <y>`, `<x> <z> y <y> <playfield>`) alongside our own
(`<map>`, `<x> <y> <z>`, `<map> <x> <y> <z>`); a three-token form is unambiguously ours, since retail's third token
is the literal `y`. On the two-token form the missing Y is ground-snapped from the baked terrain, and when a map has
no terrain loaded the caller's current height is kept and the reply says so. `GmCommandArgumentGrammarTests` pins
the recovered facts the parser is built on, as `GmCommandTableTests` pins the masks.

| privilege | bit | commands |
|---|---|---|
| `(unrestricted)` | `0x00000000` | `/system` |
| `Developer` | `0x00000001` | `/addbugcc` `/ai` `/andersdebug` `/banish` `/bccaction` `/bccself` `/brtest` `/cars` `/chaincast` `/char` `/clearcooldown` `/combo` `/commandqueue` `/consumememory` `/criteriatester` `/datamining` `/dataset` `/dbdebug` `/disablenpcautoreset` `/fakeevent` `/formation` `/fraglimit` `/gametime` `/gametweaks` `/getfaction` `/ghost` `/hatedbylist` `/hugomtest` `/jumpspeed` `/lazyreload` `/localcache` `/locktemplate` `/lua` `/luas` `/massive` `/MaterialInfo` `/memoryprofile` `/mentor` `/missiontimeout` `/mobs` `/morphvalues` `/mousetarget` `/move` `/net` `/normalset` `/npcsleep` `/observer` `/openurl` `/pet` `/pupil` `/quest` `/questmarkeron` `/RaidFinderRemove` `/reload` `/resetplayfield` `/resurrectionpoint` `/ruictest` `/save` `/scriptlist` `/setcontrolmode` `/setfaction` `/setlookspackage` `/setregionscript` `/setregionvar` `/settime` `/settimescale` `/showclassstatadjustments` `/showerrors` `/simpledynel` `/simpledyneldebug` `/simulatenpc` `/space` `/spawnacgoutdoorentrance` `/spawnpoints` `/substate` `/syncdisplay` `/tacticalmap` `/teleportcity` `/time` `/togglespellrecharge` `/tracescripts` `/tradeskill` `/update` |
| `TeleportSelf` | `0x00000004` | `/teleport` `/teleportself` `/tp` |
| `TeleportDynels` | `0x00000008` | `/grantress` `/playfieldinfo` `/playfieldinstancelock` `/reportcharoffline` `/resetacg` `/softreset` `/teleporttarget` `/unsummon` `/whereis` |
| `PlayerDamage` | `0x00000010` | `/areaknockback` `/falldamage` `/inflict` `/knockback` |
| `GetStats` | `0x00000020` | `/distance` `/feat` `/get` `/gethash` `/getscriptgroup` `/getskill` |
| `SetStats` | `0x00000040` | `/cash` `/feats` `/jumpheight` `/level` `/magic` `/pvplevel` `/set` `/setlocal` `/setscriptgroup` `/token` |
| `Broadcast` | `0x00000400` | `/broadcast` `/servcast` |
| `Sitecast` | `0x00000800` | `/sitecast` |
| `ACGspawn` | `0x00001000` | `/inventory` `/resurrect` `/spawn` `/spawnquest` |
| `AssistSummon` | `0x00002000` | `/assist` `/summon` |
| `ChangeName` | `0x00004000` | `/setname` |
| `ChangeNick` | `0x00008000` | `/setnick` |
| `NPCCommands` | `0x00080000` | `/kill` `/npc` `/respawn` |
| `CanCloak` | `0x00100000` | `/cloak` `/gasubscribe` |
| `CanKick` | `0x00400000` | `/kickban` |

`/gm` itself is `FanaticGMLevelCommand_c`, registered ahead of all of these and **without** a permission record — so
it is available to anyone. That is what makes it the way in: it is the command that reads and sets the mask.

### Which of them we implement

**Every one of the 133 answers.** 47 are implemented; the other 86 are *catalogued*
(`Faolan/GameServer/RetailCommandCatalog.cs`) — registered with retail's own mask and recovered usage line, and
answering one prints the grammar, the privilege it needs, and what it would take to back it:

```
'spawnpoints' is a retail client command this server does not implement yet.
  retail: Clear or respawn the guru spawnpoints of the current playfield
  usage:  .spawnpoints clear | respawn | overridetimer | time
  needs:  guru spawn points (#363)
```

⭐ **Why register a command we cannot perform.** The client's hidden GM affordances only ever emit verbs from its
own registry, and an unregistered verb comes back as "no such command" — which reads as *this build is broken*
rather than *that subsystem does not exist here*. ⚠️ These are not stubs that pretend to work: nothing is
toggled, nothing is written, and the reply says so. A verb graduates out of the catalogue the moment it gets a
real handler — `BuildGmCommands` throws if a verb is in both tables, since which one won would otherwise depend
on insertion order.

Three gates keep it honest: `EveryRetailVerb_IsEitherImplementedOrCatalogued` (completeness over the recovered
registry), `CataloguedVerbs_CarryRetailsOwnMask` (a placeholder reply is still gated on retail's real bit, and
every entry must say what it needs), and `NoVerb_IsBothImplementedAndCatalogued`.

Implementation is tracked in Gitea #914, wave by wave. Two rules govern it:

* **Retail's `/` verb wins.** Where one of our invented `.` verbs does the same job as a retail verb, ours is
  removed rather than kept as an alias — the client's own UI can only ever emit registry verbs, so a second name
  for the same job is a name nothing can call. `.help` still lists the whole table the caller's mask passes, so
  nothing becomes less discoverable.
* **Retail's grammar and retail's mask**, both pinned by tests (`GmCommandArgumentGrammarTests`,
  `GmCommandTableTests`).

⭐ **When retail ships two similarly-named verbs, the shorter is usually the READER and the longer the WRITER,
and their masks differ accordingly.** `/feat` lists your target's feats (`GetStats`); `/feats` manipulates them
(`SetStats`). Ours was the manipulator under the reader's name *and* the reader's lower mask, so it was gated one
privilege too loosely. A single verb of ours covering both jobs lands on the wrong one about half the time.

⭐ **Retail's description of a command describes retail's implementation.** `/unsummon` "dismisses a character to
the last saved location" — which works for them because their `/summon` presumably does not save. Ours does, so
following the sentence literally reproduced the words and lost the behaviour (the return point is now captured at
summon time). Where our internals differ, the sentence is a clue, not a specification.

⚠️ **A matching NAME does not mean a matching JOB.** Retail's `/time` manipulates *frame* times — "speeds up or
slows the deltatimer system on server and client". The time-of-day setter is `/settime`, and that is what our
`.time` actually was, so it was renamed; `reset` moved to `/gametime`, where retail puts it. We do not implement
retail's `/time`. Check the recovered long description before assuming two same-named verbs agree.

⚠️ **`.invisible` is now `/mobs on | off [id | name]`** — the same `InvisibleToMonsters` flag with the same
account persistence, but explicit rather than a toggle and aimable at another character. (Retail's note that it
"only works on debugservers" describes *their* deployment, not a limit here.)

⚠️ **`.setstat` is now `/setlocal`.** It pushed a raw stat to the CLIENT without changing server state, which is
exactly what retail's `/setlocal` does — retail's `/set` is the one that changes the stat on the target. Older
notes in `docs/re/` that say a value was "measured with `.setstat`" are accurate history and are left alone; the
command to type today is `.setlocal`.

`/gametime * <factor> | / <factor> | reset` rides a real wire field: `GameTimeIIR`'s trailing f32 `rate` feeds the
client clock's own multiplier (`GameTime_t` +0xD4), so the client speeds its day/night cycle to match instead of
drifting against the server's.

⚠️ `/system` is registered by retail at mask `0x0` — **unrestricted** — and we match that rather than inventing a
bit for it. It and `.help` are the only two unmasked verbs.

## What the client sends

Executing a GM command emits `FanaticCommandIIR_t` (`0xEE4AA85C`) — `[u32 command][u32 targetA][u32 targetB][string
argument]`, decoded from its `ReadSubClass` (`FUN_00663860`) and parsed by `FanaticCommandCodec`. ⚠️ The server's
REPLY is still undecoded: no capture in the corpus contains one, because the retail traces are ordinary play rather
than GM sessions. The client's own `Fanatic` debug channel (level 2) logs its view of the exchange
(`--debug-channels Fanatic`).

## The two other `player_stats` GM fields

Both are in the protobuf schema, both unset by us, and both now have a meaning:

- **`gm_ldb_name`** (field 6) — the LDB string id of the tag rendered over a GM's head. Paired with the `ShowGMTag`
  privilege (`0x00010000`).
- **`gm_cloak_level`** (field 7) — the cloak degree. Paired with `CanCloak` (`0x00100000`) and the `/cloak` command,
  whose own help text is *"Turns on/off cloaking. off turns it off, everyting else turns it on"*.

Their VALUE semantics are not decoded — what LDB category `gm_ldb_name` indexes, and what the cloak level's magnitude
means, would be guesses. Open work: Gitea #449.

## Flying

`DebugToggleFlyingMode` is **not** a config key and not a chat command (the #448 backlog listed it as a candidate
UniversalConfig key — that is now settled). It is an input ACTION, bound in the client's own
`Data/Gui/Default/Hotkeys.xml` to **ALT + F7**, labelled "Toggle Character Flyingmode". Its whole group is GM-gated by
the same mask:

```xml
<HotKeyGroup label="GM/Debug" gmlevel="-1">
  ...
  <HotKey id="DebugToggleFlyingMode" key1="ALT + F7" label="Toggle Character Flyingmode"/>
```

`gmlevel="-1"` is `0xFFFFFFFF` — under the `&`-test that reads as "any GM bit at all", so a non-zero `gm_level`
enables the group. It is the only group in the file that carries the attribute.

### The chat command that actually flies: `/luke`

ALT+F7 is not the only way in, and it is not the reliable one — the whole GM/Debug group is a *debug* input path.
The client also ships a plain chat command for it, `SkywalkingCommand`, and it is **entirely client-local**: its
execute (`FUN_013f6130`) sends no packet at all, so **it works against our server today with no server support**.

The verb is **`/luke`** — Luke *Skywalker*. **Confirmed working in-client 2026-07-29.** It is an LDB string, which is why grepping the exe for "fly" or
"skywalk" never finds it: `SkywalkingCommand_Name` ELF-hashes to id 47110773 in category 10002, whose value is
`luke` (see [`../rdb/ldb-string-index.md`](../rdb/ldb-string-index.md) for the hash rule). Its description, id
97813470, reads *"This command enables skywalking."*, and it reports `Skywalking enabled` / `Skywalking disabled`.

It is a **toggle**, and what it toggles is client-side movement state, not anything on the wire:

```c
obj = <local player>;
toggle = (obj->[0x1BC] == 0);      // the flying flag
obj->[0x1BC] = toggle;
if (toggle) { SetAlignmentMode(obj->[0x100]); feedback("SkywalkingEnabled"); }
else        { if (mode == 5) SetAlignmentMode(obj->[0x100]); feedback("SkywalkingDisabled"); }
```

Flying is **alignment mode 5** in the client's movement state machine (`vehiclealignment.cpp`; the factory
`FUN_00a3cbc0` maps 0=Normal 1=Climbing 2=Dead 3=Diving 4=Falling **5=Flying** 6=Knockback 7=Swimming). Nothing
about it rides `CharDCMove`, whose MovingType is a layout bitfield with no movement-mode enum
([`../re/chardcmove-grammar.md`](../re/chardcmove-grammar.md)) — so there is no server-side "make them fly" field,
only the client's own state.

The client also exposes a server-driven lever, the GameAction `e_Type_GC_GameAction_StartFlymode` (`0xCF92`) -
but sending it does **nothing** (tested in-client 2026-07-29), so we do not ship a command for it. Its parameter
list lives in a runtime-filled descriptor table and is still unknown, which is the likeliest reason. ⚠️ An earlier
attempt sent `0xD12D`, outside the range the client asserts (`0xCF09..0xD026`, `SpellData.cpp:1577`), and **crashed
the client**; see [`../re/gameactionlist-grammar.md`](../re/gameactionlist-grammar.md) for the stride trap behind
that. `/luke` is the route that works.

### The rest of the GM/Debug group

All 18 entries, every one enabled by a non-zero `gm_level`:

| key | action | what it is |
|---|---|---|
| `CTRL + ALT + V` | `Debug_ToggleCameramode` | **Godcam** — detached free camera |
| `ALT + F7` | `DebugToggleFlyingMode` | the flying toggle (see `/luke` above) |
| `CTRL + ALT + X` | `Debug_MouseWorldPos` | world position under the cursor |
| `SHIFT + O` | `Debug_CopyMeshID` | copies the mesh id of what you're looking at |
| `SHIFT + CTRL + F7` | `Open_TweakGUIWindow` | the **tweak-table GUI** (RDB 1000516) |
| `CTRL + ALT + F2` | `Debug_LoadTweak` | reload the tweak file |
| `SHIFT + CTRL + F8` | `Open_ServerStatWindow` | server stat window |
| `CTRL + ALT + D` | `Toggle_DamageMeterWindow` | damage meter |
| `ALT + SHIFT + R` | `ToggleResManWindow` | resource-manager window |
| `ALT + SHIFT + S` | `ToggleSoundWindow` | sound window |
| `CTRL + ALT + P` | `Toggle_PerformanceLogger` | performance logger |
| `F10` | `Toggle_Options` | options |
| `ALT + ENTER` | `ToggleFullscreen` | fullscreen |
| `ALT+SHIFT+CTRL+1..5` | `Debug_CrashConan1..5` | ⚠️ deliberate crashes — do not press |

### Registered input actions with no default binding

The client registers **190** input actions but `Hotkeys.xml` binds only 163. The 28 unbound ones are reachable by
adding a `<HotKey>` entry for them, and several are real features rather than debug leftovers:
`Open_TradeSkillWindow`, `Toggle_PvPStatWindow`, `Toggle_QuestSharingWindow`, `Toggle_SP_Inventory`,
`ShowMagicCone`, `Use_Perception`, `LookAtTarget`, `CycleVicinityTagModes`, `SwitchLayout`, `TakePrintScreenShot`,
`Movement_ToggleSitStand`, `NextFriendlyTarget` / `PrevFriendlyTarget` / `NextHostileTarget` / `PrevHostileTarget`,
`Combat_TabWeapons` / `Combat_TabSpellFriendly` / `Combat_TabSpellHostile`, `SelectTeammember7..10`,
`Action_Reload`, `Debug_ToggleCPUAffinity`, `Use_Perception`, and the mouse-button pseudo-actions.
(`ToggleControlCenter__UNUSED` names itself.)

## Reproducing

```zsh
GH=<data-repo>/Ghidra
# the privilege bits + the shipped roles (FanaticGMLevelCommand_c's constructor)
CLASSRX='^FanaticGMLevelCommand.*::vftable$' /opt/ghidra/support/analyzeHeadless "$GH" aoc \
  -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpFanaticCommands.java
# the 133-command catalog with each command's required mask
/opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript DumpCommandRegistry.java
# the /help filter that ANDs them
FNS=006c86b0 /opt/ghidra/support/analyzeHeadless "$GH" aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath tools/ghidra -postScript DecompileFns.java
```
