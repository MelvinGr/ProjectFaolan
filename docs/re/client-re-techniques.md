# Client RE techniques

How to name a wire field, an enum value or a message from the client (`AgeOfConan.exe`, its RDB and its LDB). Each
technique below settled something that offset scans and guessing had not. The scripts are in `tools/ghidra/`; see
`tools/ghidra/README.md` for the headless invocation, and every script writes to `$FAOLAN_GHIDRA_OUT`.

**Image facts.** `AgeOfConan.exe` is PE32 with ImageBase `0x400000` and no `DYNAMIC_BASE`, so Ghidra's addresses
are the runtime addresses. For `.rdata`, VA = file offset + `0x401200` (what `strings -t d` prints is the file
offset). Ghidra prints small displacements in decimal (`+ 200` is `+0xc8`): match both forms when grepping
decompiles. Ghidra prints a script's `println` as `INFO <Script>.java> ...`, so a filter on `^INFO` eats the output.

## Before Ghidra

Most "opaque" values are already named somewhere cheaper. Try these first.

- **Read the code that already parses the region.** Grep the constant itself across the repo before decoding bytes.
  A `53xxx` word in RDB data is a GameAction type id with a name (`Faolan.Core.Rdb/GameActionTypes.cs`,
  `docs/re/gameaction-type-ids.tsv`). When a model comment says a source is undecoded, check whether the parser or
  only the mapping is missing.
- **Look an id up in the data we hold.** `SELECT` it out of every catalog table in `faolan.db`, and look it up in the
  RDB index: `le.idx` is a flat `(type, id) -> (file, offset, size)` table, so "which types contain record N" is one
  pass. Group a field's captured values and look them up together: the SET of RDB types they land in names the
  field even when no single value is recognisable. Assets exported together get adjacent ids, so an id one away
  from a known one is worth a lookup, not a conclusion.
- **Read the client's text first.** Retail describes its own rules to the player (preconditions, timers, party
  sizes, refusal reasons), and a sweep of the wire or the RDB data tables never sees it:
  `--report ldb-search --report-arg "<phrase>"`, then the tuning sheet (`--report rdb-tweaks`) and the stat-name
  table (`docs/re/aoc-client-stat-names.tsv`). A string that is not in `docs/re/aoc-string-index.txt` is in the LDB.
- **Check for a criterion run.** Every GameAction element carries `[id][value][operator]` criteria, and a condition
  that looks arbitrary from everywhere else is usually one: `--report spell-template --report-arg <id>` prints them,
  and `docs/re/criteria-operators.tsv` names the operators.
- **Search by kind, not by value.** A table whose ids are the unknown cannot be found by searching for an id:
  `--report spell-refs --report-arg kind:<typeId>` lists every element of one GameAction kind.
- **Our name for a field is a hypothesis.** Some names in the tree were invented to get a packet moving. Grep the exe
  for the word: no hit in the whole image means the name is ours, and it should be labelled or renamed.
- **The RPC catalogs carve bodies.** `docs/re/aoc-client-rpc-methods.json` has a `client_to_server` half with typed
  `params` and a `server_to_client` half with `fields` lists; a field count against a body length carves a body that
  is identical in every capture. An uncaptured RPC's body is derivable this way (parameters concatenated big-endian,
  ID32 = 8 bytes, int = 4, u64 = 8); its RpcHeader endpoints are not, and they are the half that breaks in-client.
  A field's name never decides its width: only the dispatch reader does. The carve gives the layout, not the meaning.
- **Grep the harvested tables before opening Ghidra.** `docs/re/gameaction-keylists.tsv` and
  `docs/re/gameaction-executors.tsv` already name every GameAction kind's keys and its executor.

## The client as an oracle

- **If the client renders something we never send, the data is client-side.** Check what we actually send in that
  field (a hardcoded field cannot be driving the display), then look at the client on our server.
- **Drive the field and look.** Set a value in `faolan.db`, relog the client against the local server and read the
  UI. It costs about a minute per value and names fields that inference from neighbouring data cannot.
- **Scales come from what the client draws.** The client keeps stats in wire units and divides only when it draws,
  so neither the wire nor its debug log shows a display scale: `.setstat <id> <raw>` and a look at the number does.
  `/logcombat on` writes `CombatLog-<date>_<time>.txt` in the install directory, the only source that labels crits
  and names damage schools in words (turn it off before quitting, or the file can be left truncated).
- **The item-tooltip builder renders every GameAction modifier slot**, so its format string and suffix name the
  slot's unit. A record's own description text is an independent check on a tooltip binding: it owes nothing to
  the disassembly the binding came from. Render through the client's own strings and scales and refuse to half-fill
  a line; a renderer that shows the client's text beside our rows exposes import losses no per-record test sees.
- **Read a rendered number carefully.** Tooltips are semi-transparent, so read the pixels twice. The client keeps
  UI state (a tree's expanded nodes, a scroll position) across relogs: read only the pane under test. Of the
  character sheet only the Rating / Score / Value column is a scale, never the Effect column; probe with a value
  that is not a multiple of the base, never infer a scale from a neighbouring stat, and do not assume a sheet row is
  driven by the stat id its label matches. A client `GameActionStat_e` id is not an LDB 615 stat id, the tooltip
  fetches a slot's keys in an order that is not the slot order, and a slot the tooltip never reads carries no
  rendered magnitude.
- **`AgeOfConan.log` is overwritten on every client launch**, while combat logs are written per run: copy both next to
  their capture in the data repo (`<capture-basename>.combatlog.txt`). A crit ratio compares hits on the same target
  in the same buff state.

## Naming things in Ghidra

### Strings

- **Dump the neighbourhood of a string hit** (`DumpStrWin.java AT=<hex>`). The exe stores related names contiguously,
  in declaration order, so one hit is usually a member of a table, and the table names the mechanism.
- **A contiguous name block is an enum's order.** `strings -a -t x` the block and do not sort it. Pin the base from
  capture anchors, then prove the base is unique by sliding the block over the plausible range; put that proof in a
  test.
- **A switch that assigns strings decompiles as identical argument-less calls.** Read the disassembly
  (`DisasmFn.java`: each branch is `MOV EDX,<string>; CALL <assign>`), the jump table (`ReadVals.java` on the
  `JMP [EAX*4 + table]` operand) and the strings (`DumpStringsAt.java`), and walk shared branch tails from the
  assembly.
- **An LDB string whose id appears nowhere in the exe is looked up by token name**, hashed at runtime, so `ConstScan`
  on the id finds nothing by construction. Hash the exe's strings instead (`--report ldb-resolve`). Elf28 is masked
  to 28 bits, so a hash match is a candidate, not a proof.
- **Two small integers pushed at a call site are often an LDB `(category, id)` pair.** Read the `PUSH` immediates in
  the disassembly (`DisasmWin.java`; the decompiler hides them) and resolve them with
  `--report ldb-strings --report-arg "id:#<id>"`. They are small integers, not 28-bit hashes, and only some of a
  helper's call sites push a literal pair (the rest compute the id), so a miss at one site says nothing.
- **Assert and log text names the client's own fields.** Before more RE, find the code path that complains about the
  unknown field. The dispatch readers fail with "stream bad after '<field>'", which names every field a dispatch
  fills, and each interface's dispatch constructor registers `index -> name` for every method, no-argument ones
  included. When two readings of a field both fit, the unread word next to it is usually the discriminator.

### Classes and vtables

- **RTTI the class, then sweep by class.** A reader referenced from a data address is a vtable slot:
  `Xrefs.java ADDR=<reader>`, `DumpVtableAround.java SLOT=<that address>`, then `WhatClass.java` on the vtable
  start names the class from its CompleteObjectLocator. An image-wide offset sweep is noise (tens of thousands of
  hits); a class has a handful of methods.
- **A reader gives the grammar, never the meaning.** The executor or applier is usually a vtable sibling of the
  reader (`DumpClassVtable.java CLASS=<Class>`). An applier that switches on a kind word logs each case's own enum
  name before acting; an apply function pushes each slot under an explicit id (`SetStat(statId, value)`), and a
  different setter slot (`+0x1c` instead of `+0xc`) marks a float.
- **An unknown virtual slot is named by the set of classes that override it**
  (`DumpVirtualSlotAcrossClasses.java SLOT=0xNN`, `FindVtableForClass.java CLASS=<name>`): a base class returning a
  stored flag beside siblings returning a constant says what the guard asks.
- **A no-argument RPC method is addressable from its id**: on the GameChar / GameInterface family the interface
  vtable slot is `(method id + 1) * 4`. Read a one-line override in the disassembly, not the decompile.
- **A client override that only fires a signal is named by its subscriber.** Sweep the singleton getter's callers
  (`Callers.java FN=<getter>`) and read each whole body: the subscriber calls the getter once and uses the offsets a
  hundred instructions later. The UI is Scaleform, so the handler is registered by name.
- **A command's argument grammar is one vtable slot** of four string appends per parameter (open, close, help,
  name); the usage line rebuilt as open + name + close comes out with balanced brackets when it was read correctly.
- **Traps in a vtable sweep.** A base class's fields and slots live in a different address band from the derived
  class's, so a negative from one band says nothing about the other. A hit on `[reg + 0xNN]` may be a virtual call
  at vtable offset `0xNN`, not a field access. An empty override is an answer: the client ignores that message.
  `GameCharInterface`'s stubs hop through a secondary vtable, and two overrides that decompile identically can
  differ: read the disassembly. Decompiled x87 code loses the FPU stack, so a `float10` local shown as `0.0` may
  not be one.
- **A command grammar that comes out unbalanced is usually retail's data** (a `]` inside a help string); keep it
  verbatim rather than adding a well-formedness gate, and read `noargs` as "declares no parameters", not "accepts
  none".

### Fields

- **Find a field on a fixed-size record by its stride.** `FindOffAndStride.java OFF=<off> STRIDE=<size>` finds the
  functions that read the field and step by the record size; `FindTinyReaders.java` finds the getters an offset
  scan never sees.
- **Scope offset scans to a GUI band.** Find the view's widget-name table (`StrXref` on the domain word), then
  `FindOffInRange.java` over that band finds the filler, and the filler's branch predicate is the semantics.
  `DecompileXrefsTo.java ADDR=<global>` decompiles every function touching the singleton that holds the struct;
  `DecompileRange.java` decompiles a whole view module. A debug view often labels its columns with the struct's
  internal names.
- **A server-owned enum is named by the function that maps it onto an asset.** Look for the switch from the value to a
  particle effect, an animation or a texture: the asset names are the value names, including values the server
  never sends.
- **A field's missing consumer may be a sort comparator**: it compares the field against another instance's same
  field and never formats or branches on it.
- **A field whose consumer is unreachable is often named by a sibling producer**: another message or GameAction kind
  that fills the same field, whose own reader names it.
- **Check the reader extracts the field at all.** A length-prefixed sub-stream handed to a generic copier is passed
  through, not parsed, and then nothing can correlate with it.
- **Two accessors in one function can apply different scales**; check the getter's own tail before binding a number.
- **A fixed offset into a record's head is right only if the head is fixed-length.** Measure that first: the wrong
  word usually reads 0, which looks like a legitimate "no flags set". Anchor on something the format guarantees.
- **Small displacements are useless as anchors** (`+0x28` alone matches tens of thousands of instructions): anchor on
  a vtable, a stride or a GUI band. A shifted base defeats an offset scan, so a field that gates read but nothing
  writes may be written through another struct view at a different base.
- **A byte-exact parse proves the structure, not the semantics**, and an effect switch gives a value's name, not the
  rule that decides when the server sends it.
- **The wire order of a list the client displays is not derivable from the catalog**: harvest it rather than sort.

## Proving a negative

- **"Nothing reads this field" is only as strong as the scan's reach.** Say what was searched: the whole `.text`
  with an instruction-pattern scan, or the containing function to its end, never a fixed window after a call site.
  Zero callers of an accessor proves nothing where the client inlines the lookup.
- **Dead code is proven by a raw `E8`/`E9` scan over every block** (`ScanRelCalls.java ALL=1`), not by Ghidra's
  references; a registry is proven complete from the constructor side (every caller of the base constructor that
  takes the id).
- **A bit test has two spellings**: `TEST reg,mask` (`DumpQuestLineFlagBits.java`) and `SHR reg,n; TEST CL,1`
  (`FindBitShiftAtOffset.java`). Run both, with a known bit as a control in the same sweep; most `SHR reg,3` hits are
  divide-by-8 arithmetic, not bit tests.
- **Two bits tested as an OR are separable** when the function emits a message guarded on one bit alone; count the
  bits it tests against the names it emits, and state which half rests on the solo guard and which on elimination.
- **A message's direction is decided by who constructs it**, not by which direction the corpus shows: anything
  reaching `n3EngineClient::SendIIRToServer` (`FUN_00595f90`, identified by "Unable to write the IIR to the stream")
  is a client upload.
- **A message that names a number is not evidence a check compared it.** Find the branch that emits the message and
  read its sibling messages in the same switch: they say what the evaluator distinguishes. A long fruitless probe
  sweep is evidence the premise is wrong.
- **A client log line next to a packet is not that packet's mechanism**: the log interleaves threads. Count the
  emitting function's callers and read the apply.
- **A record the client's registry does not register is usually our split being wrong**: group the anomalous records
  by every neighbouring field before hunting for another registration site.
- **Same name, different job.** A retail GM verb can share a name with ours and do something else, and its
  description describes retail's implementation. Two entries sharing one description string are either our
  extractor mis-attributing or retail cloning a class: count the code sites that push the string.
- **A hand-transcribed Ghidra table needs two independent corroborations and a gate.** After recovering a catalog
  under the client's names, delete the enum we invented for the same ids in the same commit (search by value as well
  as by name), and carry its behavioural knowledge across.

## Live debugging

- **gdb attaches** (`gdb -p <pid>`, the process whose maps contain `AgeOfConan.exe` at `00400000`). Pass Wine's
  signals through or gdb stops and detaches:
  `handle SIGUSR1 SIGUSR2 SIGPWR SIG32 SIG33 SIG34 SIG35 SIG36 SIGSYS nostop noprint pass`, plus
  `handle SIGSTOP nostop noprint`, since Wine stops the main thread during loading. Use logging breakpoints that
  `continue`, so the client never freezes; `watch` (write) rather than `rwatch` on a hot field. Resolve an object's
  class live through MSVC RTTI: `vtable - 4` is the CompleteObjectLocator, `COL + 0xc` the TypeDescriptor, and the
  name is at `TD + 8`. `__thiscall`: at the first instruction `this` is in `ecx` and stack arguments start at
  `[esp+4]`.
- **Frida attaches only through a Windows `frida-server` run inside the game's Wine prefix** with the game's own
  wine; attaching to the Linux Wine process fails. Hook function entries only, one attach at a time. The scripts in
  `tools/frida/` show the API in use.
- **An assert writes `MiniDump.dmp`** next to `AgeOfConan.exe`. The assert handler is `FUN_004a6a30(msg, file,
  line)`, so the dump's stack names the asserting function.
