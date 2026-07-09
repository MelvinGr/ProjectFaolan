# 16-bit RPC method opcodes — Ghidra handoff (2026-06-13)

> **RESOLVED 2026-06-13.** Tables recovered: `docs/aoc-rpc-method-opcodes.md`; summary in
> `docs/aoc-opcode-ids.md` (Stage 3b); scripts/outputs in `Captures/ghidra/`. `wire = 0x2000 + registration
> index`, base 0x2000 for all interfaces except PlayerAgent/PlayerInterface (32-bit hash dispatch — still
> open). c2s order == the catalog (proven); s2c drift resolved via the dispatcher switches (the live build
> inserts mostly-no-arg dispatch methods the string catalog missed). In-client anchors all match. This file
> is kept for the methodology; the live result lives in the docs above.


Recover the **16-bit interface method opcodes** (`0x2000..0x20FF`) from `AgeOfConan.exe`. This is the one
explicitly-PARKED opcode gap (`docs/archive/opcode-re-plan.md` Stage 3b) — the cheap angles all failed because
the id is **framework-assigned by RPC registration ORDER**, not a literal / hash / jump table. A full Ghidra
analysis (now in hand) is exactly what the plan said this needs.

## Goal
For each of the 14 RPC interfaces, recover the ordered list of methods so that:
```
wire opcode = 0x2000 + (method's registration index within its interface)
```
This yields the real numeric id ↔ method-name table for THIS client build — including the **server→client
("Dispatch") tables that DRIFT** (CLAUDE.md: we currently name s2c downloads by content because we never had
the build's true order). Output: replace the invented/`[?]` names in
`Faolan.Core/Network/Packets/Opcodes.cs` with the recovered ids, and extend `docs/aoc-opcode-ids.md`.

## What is ALREADY done — do NOT redo
- **Method names + signatures** (Stage 1): 14 interfaces, 376 c2s (`*Proxy_t::<Method>` strings) + **401 s2c**
  (`stream bad after '<field>'` strings) → `docs/aoc-client-rpc-methods.json` / `.md`. We already know WHAT every
  method is and its field order; we only lack the numeric 16-bit id for each.
- **32-bit sub-opcodes (IIR type ids) + endpoint ids** (Stage 2/3a): BKDR-31 hash of class names →
  `docs/aoc-opcode-ids.md`. (`h=0; for c in name: h=(h*31+c)&0xFFFFFFFF`.)
- **A handful of 16-bit control opcodes hand-identified** from captures: `SpawnCheck 0x2008`,
  `ReadyForPlayScreen 0x2024`, `Pong 0x2009`, the `0x201C` handshake, `CreateCharacter 0x20DE`,
  `EnterWorld 0x20B6`. **Use these as anchors** to validate the recovered registration order.

## The Ghidra environment is READY (reuse it — analysis already done)
- Project: `/home/melvin/Development/Ghidra/Aoc` (name `aoc`, program `AgeOfConan.exe`, 12.1.2). **NOTE: `/tmp` is ephemeral** —
  if it's gone, re-import (`analyzeHeadless <projDir> aoc -import "<exe>"`, ~10–30 min one-time; the exe is at
  `/var/games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe`, PE32 i386, **stripped, no PDB**).
- Scripts: `/home/melvin/Development/Ghidra/Aoc/scripts/{DumpReadSubClass,SurveyStrings}.java` (Java post-scripts; clone one).
- Fast re-run (NO re-analysis):
  ```
  /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
    -scriptPath /home/melvin/Development/Ghidra/Aoc/scripts -postScript YourScript.java > /home/melvin/Development/Ghidra/Aoc/out.log 2>&1
  ```
- **Gotchas learned this session (important):**
  - Ghidra did NOT auto-create string xrefs. To find who references a string: find the string, walk back to the
    **C-string start** (byte after the preceding `0x00`), then `Memory.findBytes` for the **little-endian 4-byte
    pointer** to that start — each hit's containing function is a referencer. (See `DumpReadSubClass.stringStart`
    + `le32` + the pointer-search loop — copy them.)
  - `GhidraScript` has **no `find(Address,String)`** overload — use `Memory.findBytes(start,bytes,null,true,monitor)`.
  - Java post-scripts that fail to compile report "could not be found / OSGi bundle" — it's a compile error, fix
    the Java.
  - Decompile via `DecompInterface` (`decompileFunction(f,90,monitor).getDecompiledFunction().getC()`).
  - Kill stray processes between runs (`pkill -9 -f ghidra`) and clear `proj/*.lock` if a run is interrupted; two
    `analyzeHeadless` on one project conflict on the lock.

## The plan (recover registration order)
1. **Locate each interface's method-registration site.** The dispatch is registration-based (no static pointer
   table). Anchor on the per-interface strings already in the exe: `GameCharInterface` (×360), `…Proxy_t::…`
   (×13 distinct), `DispatchMethod(` (×8). Find the interface's constructor / init function and read the
   **ordered sequence** of register calls (e.g. `RegisterMethod(nameHash_or_str, &handler)` — there is NO
   `RegisterMethod` string, so identify the register helper by its repeated-call shape, then the Nth call = index N).
   Alternatively the init may build a **method-descriptor array** in `.data`/`.rdata` (ordered) — dump it.
2. **Map handler → method name.** Each registered handler is a `*Proxy_t::<Method>` (c2s) or a `Dispatch<Method>`
   (s2c); they log their own name (the Stage-1 strings). Decompile/peek each handler to read its name string → the
   index↔name pairing.
3. **Validate against the anchors** before trusting it: in `GameCharInterface`/`GameInterface`, `CreateCharacter`
   must land on `0x20DE` and `EnterWorld` on `0x20B6`. If the ordinals match, dump all 14 interfaces.
4. **Write the tables**: update `Opcodes.cs` (replace invented names with verified ids) and `docs/aoc-opcode-ids.md`;
   re-validate any opcode our server already emits/handles didn't change meaning.

The 14 interfaces (Stage 1): `UniverseAgent/Interface`, `PlayerAgent/Interface`, `CSPlayerAgent/Interface`,
`GameCharAgent/Interface` (main game, 169 c2s / 157 s2c), `GameAgent/Interface`, **`TradepostAgent/Interface`**
(the queued mail/auction work — recovering these ids directly unblocks it), plus the `*Project` variants.

## Known function map (from the 2026-06-13 FullChar/Ghidra session — reuse as landmarks)
- `n3InfoItemRemote::Construct = FUN_005b9d30` — the generic IIR reader (reads KEY hash, identity, flag, BE32
  blob length, parses protobuf, calls the virtual ReadSubClass). The IIR class lookup is `FUN_005bb600` (by hash).
- `CoreFullPlayerCharacterIIR::ReadSubClass = FUN_00797440`; ReadUInt32(BE) = `FUN_0048f900`;
  ReadBytes = `FUN_00cba1b0`; ReadByte = `FUN_004e0340`; read-length-delimited-protobuf = `FUN_00cbfb80`.
- Decompiles saved: `docs/re/aoc-iir-decompile.txt`. Subsystem string→function index (partial, ~43 keywords incl.
  the crypto/login set): `docs/re/aoc-string-index.txt` (regenerate fuller via `SurveyStrings.java`).

## Secondary Ghidra targets (same analyzed project, cheap; do after opcodes)
- **Per-IIR `ReadSubClass` framing** — walk the IIR registry (`FUN_005bb600` + its factory table) to decompile
  every IIR type's reader → byte-exact wire grammar for spawn/vitals/quest/etc. (the construct-don't-replay effort).
- **Snapshot tails / progression block** — `FUN_00797440` reads the quests + `PFAccessWindow`/`UnknownBefore`
  region that `snapshot_tails_construction_scope` couldn't decode → progressed-character snapshots from scratch.
- **Login encryption** (`login_encryption`) — confirm/complete the DH (N, g=5) + TEA-CBC path + the validation stub.

## Acceptance gate
The recovered `GameInterface`/`GameCharInterface` ordinals reproduce the known anchors (`CreateCharacter 0x20DE`,
`EnterWorld 0x20B6`, and any others we already use). Only then commit the full 14-interface tables to `Opcodes.cs`
+ `docs/aoc-opcode-ids.md`. Keep the recovery script under `Captures/` (alongside `decode_opcode_ids.py`,
`extract_rpc_catalog.py`) so it's reproducible.

## Paste-ready prompt
> Recover the 16-bit RPC method opcodes from AgeOfConan.exe (read `docs/archive/opcode-16bit-handoff.md`). This is the
> parked Stage 3b gap (`docs/archive/opcode-re-plan.md`): the wire opcode `0x2000..0x20FF` = `0x2000 +` the
> method's RPC registration index, which is assigned by registration ORDER (not a literal/hash/jump-table), so it
> needs the Ghidra analysis already sitting at `/home/melvin/Development/Ghidra/Aoc` (reuse with `-process -noanalysis`; re-import
> if `/tmp` was cleared). Names + signatures are already recovered (`docs/aoc-client-rpc-methods.json`, 376 c2s +
> 401 s2c); you only need the numeric ids. Find each interface's method-registration site (anchor on the
> `*Proxy_t::` / `DispatchMethod(` / `GameCharInterface` strings — note Ghidra didn't auto-create string xrefs, so
> walk to the C-string start and pointer-search its LE32; see `DumpReadSubClass.java`), read the ordered register
> calls / method-descriptor array, map handler→name, and VALIDATE against `CreateCharacter=0x20DE` /
> `EnterWorld=0x20B6` before dumping all 14 interfaces. Then replace the invented names in `Opcodes.cs` and extend
> `docs/aoc-opcode-ids.md`; this also unblocks the TradepostAgent (mail/auction) opcodes. Save the recovery script
> under `Captures/`.
