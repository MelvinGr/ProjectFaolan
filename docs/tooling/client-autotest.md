# Client `-autotest` / `autotest.txt` — what it actually is (Ghidra, 2026-06-15)

RE'd from `AgeOfConan.exe` (`Captures/ghidra/DumpAutotest.java` → anchor on the `"autotest.txt"` / `"Autotest"`
string literals, decompile the referencing functions). Corrects the earlier guess in `client-launcher.md`
that `-autotest` was a single "XML ScriptCommand test engine". It is **two separate mechanisms**:

## 1. `autotest.txt` = login automation (NOT a test script)
Read by the **`LauncherWindow_c` constructor** (`FUN_011cf290`, which references `"autotest.txt"`, `"Server"`,
`"Login"`, `"Character"`, `"LoginProgressTitle"`). It is a line/record file the launcher parses (tokenizer
`FUN_010d0c60`, up to 100 records of stride 0x20); each record's first field is matched against **`Server`**,
**`Login`**, **`Character`** and drives the auto-login → character-enter flow (the `Character` value is stashed
at `*(DAT_0297d210 + 0x60)`). So `autotest.txt` does the same job as `Faolan.ClientLauncher`'s
`-username`/`-password`/`-charname`/`-charpf` flags — get the client into the world unattended — just via a
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
`ScryScriptIIR ... StartScriptGroupOnClient` (the `0xA36D3B74` driver we already build, `ScryScript.cs`).
Related ScriptCommands in the binary: `GetStatScriptCommand`, `statcheckscriptcommand`, `IsInStateScriptCommand`,
`statcheckintervalscriptcommand`, plus GUI/chat/`DistributedValue` commands.

## What this means for automated in-client verification
A hands-free assert-in-client harness is buildable, but it is a **project**, not a flag:
1. **Drive in:** `autotest.txt` (or the launcher flags) → auto-login → world. ✅ available today.
2. **Assert:** a ScriptGroup running `statcheck`/`GetStat`/`IsInState` (+ reads of the relevant
   `DistributedValue`s / quest state) that ends in `<testreportscriptcommand>`. The scriptgroup content lives
   in the client **RDB** (author/locate one) and is started server-side via `ScryScriptIIR` (StartScriptGroup).
3. **Read result:** tail `AgeOfConan.log` for the `Autotest` `<testreportscriptcommand ... testresult=...>`
   line (`--console` already tails the log).

Open work to make it real: (a) find/author an RDB scriptgroup that checks the value we care about (e.g. a
quest in the journal, a stat), (b) wire a server-side "start test scriptgroup" path (extend `ScryScript`),
(c) parse the testreport line out of the log. Until then, the achievable check is the coarse one: drive the
client in and grep `AgeOfConan.log` for FullChar/IIR errors (`stream bad`, `Wrong IIR_t version`, `Dispatcher
call ... failed`) to confirm a world packet (e.g. the snapshot-quest injection, `FAOLAN_SNAPSHOT_QUESTS`) is
*accepted* — note the headless visual render itself has been env-blocked (RDB thrash), but world-entry packet
processing happens before that.

Related: `docs/tooling/client-launcher.md`, [[lua_vm]] (the RDB Lua/script layer), `Faolan/GameServer/World/ScryScript.cs`,
[[protocol_quest_state]].
