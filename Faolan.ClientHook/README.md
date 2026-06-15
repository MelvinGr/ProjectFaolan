# Faolan.ClientHook — in-client verification via DLL injection

A small 32-bit DLL injected into `AgeOfConan.exe` (PE32 i386, runs under Wine/Proton) to surface live client
state — ground-truth in-client verification without human eyes (the alternative to the `autotest`/ScriptGroup
path, see `docs/client-autotest.md`). Addresses come from the Ghidra project (`/tmp/ghidra-aoc`,
`Captures/ghidra/`); the exe is NOT ASLR (ImageBase `0x400000`), so addresses are absolute.

## Build (pure LLVM — no mingw / Windows SDK)
```
cmake -S Faolan.ClientHook -B Faolan.ClientHook/build
cmake --build Faolan.ClientHook/build        # -> build/faolan_hook.dll (32-bit)
```
`llvm-dlltool` makes the kernel32 import lib from `kernel32.def`; `clang --target=i686-pc-windows-msvc
-fasm-blocks` compiles (freestanding, no CRT, inline kernel32 prototypes); `lld-link` links. (The CMake
project is `LANGUAGES NONE` + custom commands — there's no CRT/SDK for CMake's normal toolchain detection to
link a test program against.) Melvin injects `build/faolan_hook.dll` into the running client.

> **Removed:** an earlier mechanism toggled the client's debug-log gate globals (`DAT_021a2c29=1` +
> `DAT_029864f8=0`) to surface the client's own internal trace in `AgeOfConan.log`. It was dropped — it
> produces **no extra output on this build** (the master flag is already on, the client clears the
> channel-filter itself by world-entry, and the verbose log sites don't fire). Full investigation:
> `docs/client-debug-logging.md`.

## What it does (on `DLL_PROCESS_ATTACH`)
1. **Hooks every interface dispatcher** → a full server→client PACKET FEED. Each
   `*InterfaceDispatch_t::DispatchMethod(opcode, …)` (thiscall; opcode = first stack arg) logs `disp <iface>
   <opcode>` to `faolan_hook.log`: **GameChar** (`0x004BC7B0` — snapshot/quest/inventory/combat),
   **Tradepost** (`0x01357130` — AH/mail), **CSPlayer** (`0x0133A6A0` — guild/team), **Player** (`0x00484270`),
   **Universe** (`0x00490970`), **Game** (`0x00491F30`), **GameCharProj** (`0x0115E430`). Shows exactly which
   packets the client received and dispatched.
2. **Field hook**: `QuestFullUpdateIIR::Activate` (`0x00918080`) → `quest_activate=<id>` (`*(*(this+0x2C)+0x0C)`).
3. All inline hooks are **prologue-verified** — installed only if the target bytes match the Ghidra prologue
   (`55 8B EC 83 E4 F8`), so a stale/wrong address is skipped, never patched (it can't corrupt the client).
4. Writes `faolan_hook.log` (client CWD) with the exe base, pid, per-hook OK/SKIP, and the live hook output.

## Adding hooks (the framework)
Add a row to `g_hooks[]`: `{ addr, PROLOGUE_STD (or a new signature read at `file_off = VA - 0x400C00`),
steal_len, detour, &trampoline, name }`, plus a `__declspec(naked)` detour that reads the field of interest and
`jmp dword ptr [g_tramp_*]`. (clang's `-fasm-blocks` won't expand `__asm{}` inside a macro, so detours are
written out explicitly.) Other located candidates: `QuestFullUpdateIIR::ReadSubClass` (`0x00917E20`), the logger
`FUN_004a8b90` (`0x004A8B90`, `(level, channel, fmt, …)`), `_dbShouldOutput` (`0x004B2CC0`).
