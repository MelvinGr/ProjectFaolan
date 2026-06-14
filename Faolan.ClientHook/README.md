# Faolan.ClientHook — in-client verification via DLL injection

A tiny 32-bit DLL injected into `AgeOfConan.exe` (PE32 i386, runs under Wine/Proton) to read live client
state and log it — ground-truth in-client verification without human eyes (the alternative to the
`autotest`/ScriptGroup path, see `docs/client-autotest.md`). We hook client methods at addresses recovered
from Ghidra (`/tmp/ghidra-aoc`, `Captures/ghidra/`).

## Build (pure LLVM — no mingw / Windows SDK)
```
./build.sh        # -> faolan_hook.dll (32-bit)
```
Uses `llvm-dlltool` (kernel32 import lib from `kernel32.def`), `clang --target=i686-pc-windows-msvc`,
`lld-link`. Freestanding, no CRT; kernel32 prototypes are declared inline (no `windows.h`).

## Load
Melvin injects `faolan_hook.dll` into the running client (his side).

## Stages
1. **PoC (current `faolan_hook.c`)** — on `DLL_PROCESS_ATTACH`, write `faolan_hook.log` (in the client's
   CWD) with the `AgeOfConan.exe` module base + pid. Proves injection + output + gives the runtime base to
   rebase Ghidra addresses against (in case of ASLR; the exe's preferred base is 0x400000).
2. **State hooks (next)** — inline-detour known functions (quest-update / journal processing, the
   `DistributedValue` getter, the FullChar snapshot parser) and log the values, e.g. confirm quest 13479
   lands in the journal after login, or that the snapshot was accepted with no IIR error.
