# Client debug logging — the 2026-06-15 "dead end" was the patch pointing the wrong way

**Status:** REOPENED 2026-07-26. The 2026-06-15 investigation below is accurate about the gate and about
`AgeOfConan.log` staying quiet — but its conclusion ("the verbose sites don't fire on this build") is **wrong**.
The sites do fire; setting `DAT_029864f8 = 0` routes their output to **stderr**, which a GUI-subsystem process
under Wine discards. The patch was pointing the wrong way. See "What the sink actually does" below.

## What we tried

The hook DLL's mechanism #1 (and a short-lived `Faolan.ClientLauncher --debug-log` flag that did the same via a
live `/proc/<pid>/mem` write) set two globals to open the client's internal
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

## What the sink actually does (2026-07-26 — this is the correction)

Decompiling the log CALL and the SINK, rather than only the predicate, shows why the log file stayed quiet. Every
channel site looks like this (here `FUN_0113ba00`, the armour-mitigation computation):

```c
if (DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || FUN_004b2cc0(&DAT_02984ac0, 2, "GameCode/ArmorMitigation")))
    ... build the message ...
    FUN_004a8b90(2, "GameCode/ArmorMitigation", "%s", msg);
```

and the sink `FUN_004a8b90(level, channel, fmt, ...)` is:

```c
if (level >= 0) {
    if (DAT_029864f8 == 0)
        vfprintf((FILE *)(__iob_func() + 0x40), fmt, args);   // <-- STDERR
    else {
        FUN_004a8910(level, fmt, args);                       // <-- the AgeOfConan.log writer
        if (DAT_029864e0 != 0) fflush(NULL);
    }
}
```

`__iob_func() + 0x40` is `_iob[2]` = **stderr**. So `DAT_029864f8` is not just a filter bypass, it is also the
**output ROUTE**: zero sends every line to stderr, non-zero sends it to the log file. The 2026-06-15 patch forced
it to zero and then measured `AgeOfConan.log` — which is exactly the combination that shows nothing. Fact 2 above
(the client zeroes it itself by world-entry) means the stock in-world client is *already* logging these channels,
to a stream nothing captures.

### The configuration that should actually work

1. `DAT_029864f8` **non-zero** in-world (the opposite of the old patch), so the sink takes the file-writer branch.
   The client zeroes it at world entry, so it has to be re-written *after* that — periodically, or by hooking the
   write.
2. With it non-zero the per-channel predicate `FUN_004b2cc0(&DAT_02984ac0, level, channel)` now decides. That
   object is `[0]` = minimum level, `[7]` = output mode, `[8]…` = a channel-name table (up to 0x80 entries, 0xC
   words apart), and mode 2 additionally reads per-channel thresholds at `[idx + 0x608]`:
   - **mode 0 = whitelist** — logs a channel only if its name is IN the table. The low-noise choice: put
     `GameCode/ArmorMitigation`, `GameCode/Stats`, `GameCode/ItemData` in it and get only those.
   - **mode 1 = blacklist** — logs unless the name is in the table, so an all-zero table logs everything.
   - Either way `[0]` must be `<= level`, and these sites log at level 2.
3. `DAT_029864e0` non-zero additionally forces an `fflush` per line, which is what makes tailing usable.

The alternative route — flipping the PE subsystem GUI→CUI so Wine gives the process a real console and stderr
becomes valid — also works in principle and needs no live memory writes, but it changes the exe (and so the
`-clienthash` md5, which `Faolan.ClientLauncher` already recomputes after patching).

**Not yet verified in-client.** Open work tracked in Gitea #443.

## Takeaways

- The gate formula in the original README was right, the measurement was right, and the CONCLUSION was wrong:
  don't cite this doc as "client logging is impossible". Cite it as "`DAT_029864f8 = 0` sends it to stderr".
- What these channels would give us is real ground truth: `GameCode/ArmorMitigation` prints
  `TotalArmorValue`, `ApprenticeLevel Adjusted ArmorValue`, `Mitigation Modification`, `MinMitigation`/
  `MaxMitigation` and `Final Mitigation` per hit — which is exactly what #401 needs, and the same trick applies
  to `GameCode/Stats` (#3) and `GameCode/ItemData` (#441).
- For in-client ground truth the `Faolan.ClientHook` project's other mechanisms were used — the **dispatcher
  hooks** (per-interface server→client packet feed) and **field hooks** (e.g. `QuestFullUpdateIIR::Activate`),
  logging to `faolan_hook.log` via inline detours — but that project is no longer in the repo; Frida-under-Wine
  ([[frida_under_wine_client_re]]) is now the live-RE tool.
- The patch *mechanism* (find the client pid, write+verify `/proc/<pid>/mem`) is sound and still lives in
  `ClientExePatcher` for the DH-key patch; it was only the debug-log *target* that was useless.
