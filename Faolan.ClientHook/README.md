# Faolan.ClientHook — in-client verification via DLL injection

A small 32-bit DLL injected into `AgeOfConan.exe` (PE32 i386, runs under Wine/Proton) to surface live client
state — ground-truth in-client verification without human eyes (the alternative to the `autotest`/ScriptGroup
path, see `docs/client-autotest.md`). Addresses come from the Ghidra project (`/tmp/ghidra-aoc`,
`Captures/ghidra/`); the exe is NOT ASLR (ImageBase `0x400000`), so addresses are absolute.

## Build (pure LLVM — no mingw / Windows SDK)
```
./build.sh        # -> faolan_hook.dll (32-bit)
```
`llvm-dlltool` makes the kernel32 import lib from `kernel32.def`; `clang --target=i686-pc-windows-msvc
-fasm-blocks` compiles (freestanding, no CRT, inline kernel32 prototypes); `lld-link` links. Melvin injects
the DLL into the running client.

## What it does (on `DLL_PROCESS_ATTACH`)
1. **Enables the client's own debug logging** (the broad, bulletproof part — no code patching). Sets the two
   gate globals `DAT_021a2c29=1` + `DAT_029864f8=0` so the client writes its full internal debug trace to
   **`AgeOfConan.log`**: quest activates (`got quest [id]`), IIR version errors (`Wrong IIR_t version`),
   dispatch, inventory, combat, etc. This is the "test whatever we want" channel — the client's own
   instrumentation across every subsystem.
2. **Installs inline hooks** (prologue-verified — each is skipped, not forced, if the target bytes don't match
   the recovered Ghidra prologue, so a stale address can never corrupt the client):
   - `QuestFullUpdateIIR::Activate` (`0x00918080`) → logs `quest_activate=<id>` (the activated quest id,
     `*(*(this+0x2C)+0x0C)`).
3. Writes `faolan_hook.log` (client CWD) with the exe base, pid, and what installed.

## Adding hooks (the framework)
`install_hook(addr, expected_prologue, steal_len, detour, &trampoline)` is generic: add a `PROLOGUE_*` byte
signature (read the real bytes at the VA: `file_off = VA - 0x400C00`), a `__declspec(naked)` detour that reads
the field of interest and `jmp dword ptr [g_tramp_*]`, and one `install_hook(...)` line. Candidate targets
already located: `QuestFullUpdateIIR::ReadSubClass` (`0x00917E20`), the logger `FUN_004a8b90` (`0x004A8B90`,
`(level, channel, fmt, ...)`), `_dbShouldOutput` (`0x004B2CC0`).
