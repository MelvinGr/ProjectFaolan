# Client `-autotest` / `autotest.txt`

RE'd from `AgeOfConan.exe` (`tools/ghidra/DumpAutotest.java`: anchor on the `"autotest.txt"` / `"Autotest"`
string literals, decompile the referencing functions). `-autotest` is not a single "XML ScriptCommand test
engine"; it is **two separate mechanisms**:

## 1. `autotest.txt` = login automation (NOT a test script)
Read by the **`LauncherWindow_c` constructor** (`FUN_011cf290`, which references `"autotest.txt"`, `"Server"`,
`"Login"`, `"Character"`, `"LoginProgressTitle"`). It is a line/record file the launcher parses (tokenizer
`FUN_010d0c60`, up to 100 records of stride 0x20); each record's first field is matched against **`Server`**,
**`Login`**, **`Character`** and drives the auto-login → character-enter flow (the `Character` value is stashed
at `*(DAT_0297d210 + 0x60)`). So `autotest.txt` does the same job as `Faolan.ClientLauncher`'s
`--username`/`--password`/`--charname` flags — get the client into the world unattended — just via a
file instead of command-line flags. (`-nautotest` disables it.) **It is not a place to put assertions.**

## 2. The TEST-RESULT channel: `<testreportscriptcommand>`
The real "test" output is a **ScriptCommand**, not `autotest.txt`. `scriptcommands\testreportscriptcommand.cpp`
emits:

```
<testreportscriptcommand test="%05d" testresult="%s" testmessage="%s" />
```

`FUN_0099b010` formats that and writes it to the **`"Autotest"` log channel** (`Client side: %s` →
`AgeOfConan.log`, gated on `DAT_021a2c29`). ScriptCommands run inside **ScriptGroups** (the Scry/cinematic
system: `ScriptGroup_t`, XML `<scriptcommand name=".." id="..">`, RDB-stored by `rdbid`) — the SAME engine as
the destiny steps (`ScryScriptIIR`). The server can start a scriptgroup on the client via
`ScryScriptIIR ... StartScriptGroupOnClient` (the `0xA36D3B74` driver built by `ScryScriptCodec`).
Related ScriptCommands in the binary: `GetStatScriptCommand`, `statcheckscriptcommand`, `IsInStateScriptCommand`,
`statcheckintervalscriptcommand`, plus GUI/chat/`DistributedValue` commands.

## What this means for automated in-client verification
A hands-free assert-in-client harness is buildable from three pieces:
1. **Drive in:** `autotest.txt` (or the launcher flags) → auto-login → world.
2. **Assert:** a ScriptGroup running `statcheck`/`GetStat`/`IsInState` (+ reads of the relevant
   `DistributedValue`s / quest state) that ends in `<testreportscriptcommand>`. The scriptgroup content lives
   in the client **RDB** and is started server-side via `ScryScriptIIR` (StartScriptGroup).
3. **Read result:** tail `AgeOfConan.log` for the `Autotest` `<testreportscriptcommand ... testresult=...>`
   line (`--console` already tails the log).

Without a scriptgroup, the achievable check is the coarse one: drive the client in and grep `AgeOfConan.log` for
FullChar/IIR errors (`stream bad`, `Wrong IIR_t version`, `Dispatcher call ... failed`) to confirm a world packet
is *accepted*. World-entry packet processing happens before the visual render, so this works even when the render
itself stalls.

Related: `docs/tooling/client-launcher.md`, `Faolan.Core.Network/Codecs/Dialogue/ScryScriptCodec.cs`.
