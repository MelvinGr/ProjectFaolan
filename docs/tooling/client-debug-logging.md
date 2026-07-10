# Client debug logging — why the log gate is a dead end on this build

**Status:** investigated & closed 2026-06-15. The retail AoC client (`AgeOfConan.exe`, the build under
`steamapps/common/Age of Conan/`) **cannot be made to emit more than it already does** by toggling its
internal debug-log gate. Both the `Faolan.ClientHook` "mechanism #1" and a launcher port of it were removed
as no-ops. This doc records the evidence so the dead end isn't re-explored.

## What we tried

The hook DLL's mechanism #1 (and a short-lived `Faolan.ClientLauncher --debug-log` flag that did the same via
`/proc/<pid>/mem`, the way `LoginKeyPatcher` patches the DH key) set two globals to open the client's internal
`LogBase` trace:

- `DAT_021a2c29 = 1` (master log-enable, 1 byte)
- `DAT_029864f8 = 0` (per-channel filter bypass, 4 bytes)

The premise (from the hook README) was the gate `DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || perChannel(channel))`
— so forcing the second operand true would make every channel log its full debug trace
(`got quest [id]`, `Wrong IIR_t version`, dispatch/inventory/combat, …) to `AgeOfConan.log`.

## What the predicate actually is

`FUN_004a8c50(level, channel)` is the per-line "should output" predicate (disassembled from the exe;
`file_off = VA − 0x400C00`, i.e. `0x004A8C50 → 0xA8050`):

```asm
push ebp ; mov ebp,esp
cmp byte [0x21a2c29], 0     ; master enable
je  ret_false
mov eax,[ebp+8]            ; level
test eax,eax / js ret_false ; level < 0 -> false
cmp dword [0x29864f8], 0    ; the filter bypass
jne per_channel_lookup      ; != 0 -> consult per-channel table (FUN_004a8070)
mov al,1 / pop ebp / ret    ; == 0 -> return TRUE for everything
per_channel_lookup: ...     ; cdecl, returns the table result
ret_false: xor al,al / pop ebp / ret
```

So the README formula was **correct**: `DAT_029864f8 == 0` does force the predicate true. The mechanism wasn't
wrong about the gate. It's wrong about the *outcome*.

## Why it produces nothing — three measured facts

Verified live against a running client (`ptrace_scope=0`, reads/writes via `/proc/<pid>/mem`):

1. **`DAT_021a2c29` is already `1` by default**, in every run, patched or not. Writing `1` is a no-op.
2. **The client zeroes `DAT_029864f8` itself by world-entry.** Read at early startup = `1`; read in-world on an
   **unpatched** run = `0`. So `FUN_004a8c50` already returns true for everything once you're in the world —
   without any patch.
3. **With the predicate wide open, the log does not flood.** In-world idle: `+0 lines / 15 s`, `0` `RECEIVED IIR`.
   The saved patched run that reached playfield-load (571 lines) contained `0` `RECEIVED IIR` / `DEBUG:` /
   `VERBOSE:` / `TRACE:` lines. The verbose `LogBase` sites simply don't fire on this build — they're release-
   compiled out or guarded further — so opening the gate changes nothing.

The lines `AgeOfConan.log` *does* carry (`ERROR` / `WARNING` / `IMPORTANT`, RDB-miss, `Playfield.Loading`
timings) are emitted by the stock client regardless of the gate. That's the same content
`Faolan.ClientLauncher --console` already tails into the terminal.

## Takeaways

- Don't re-add a "enable client debug logging" gate patch — it's a confirmed no-op on this client build.
- For in-client ground truth the `Faolan.ClientHook` project's other mechanisms were used — the **dispatcher
  hooks** (per-interface server→client packet feed) and **field hooks** (e.g. `QuestFullUpdateIIR::Activate`),
  logging to `faolan_hook.log` via inline detours — but that project is no longer in the repo; Frida-under-Wine
  ([[frida_under_wine_client_re]]) is now the live-RE tool.
- The patch *mechanism* (find the client pid, write+verify `/proc/<pid>/mem`) is sound and still lives in
  `LoginKeyPatcher` for the DH-key patch; it was only the debug-log *target* that was useless.
