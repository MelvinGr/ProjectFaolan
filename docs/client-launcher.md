# Faolan.ClientLauncher + automated client testing

How to drive the retail AoC client against a local Faolan server without manual clicks — the basis for
automated, capture-backed integration testing. Proven end-to-end 2026-06-10 (login → EnterWorld, unattended).

Pieces: **`Faolan.ClientLauncher`** (launches + drives the client), the **server** (`Faolan` project), **loopback
packet capture** (`tshark`/`dumpcap` on `lo`), and the existing **analyzer/diff** tooling (`Captures/*.py`,
`Faolan.PacketAnalyzer`). Related: [login-decryption-plan.md](login-decryption-plan.md),
README "Client launch flags".

---

## 1. The launch gate (RE of ConanPatcher.exe)

Running `AgeOfConan.exe` with no flags **bounces to `ConanPatcher.exe`** — the client requires a valid, fresh
`-key`. The patcher launches it as:

```
AgeOfConan.exe -noconsole -clienthash <md5> -key <hex>
```

Both gate flags were reverse-engineered from `ConanPatcher.exe` (capstone disasm) and are reproduced in C#:

- **`-clienthash` = `MD5(AgeOfConan.exe)`** (lowercase hex). The patcher's only validation is `cHash.size()==32`.
  Verified: `md5(AgeOfConan.exe) = cce43eafcf5af877e825ebda6c9ea65a`, matching a captured launch.
- **`-key` = `TEA-encrypt(t‖t)`** where `t = time(NULL)` (unix seconds): set a 64-bit block to `{t, t}`,
  TEA-encrypt it (32 rounds, delta `0x9E3779B9`, key `[0x5BCE568E, 0xFB2E8CBC, 0xA324E6D8, 0xF22BF865]`),
  format `%08x%08x`. It's a **launch-freshness token**: decrypting yields `{t, t}` (equal halves) and the time
  must be recent. (`ConanPatcher.exe`: `sub_4bcf93` = `time()` via GetSystemTimeAsFileTime → epoch → /10^7;
  `sub_439140` = TEA.) Verified: captured `495cd3b8c7047b47` decrypts to `t = 1781038901` and re-encrypts exactly.
  - C#: `Faolan.Core.Crypt.ConanLaunchToken` (`ForUnixTime`/`Now`/`GetUnixTime`), on the shared
    `Faolan.Core.Crypt.Tea` cipher. Tests in `Faolan.Tests/ConanLaunchTokenTests.cs`.

### Other launch flags (UTF-16 flag table in AgeOfConan.exe)

Booleans: `-noconsole`, `-no3dlauncher`, `-novideo`, `-nautotest`, `-openautomate`, `-32bit`, `-mce`.
Value: `-username` / `-password` (**auto-login**), `-charslot` / `-charname` / `-charpf` (auto-enter a
character/playfield), `-clienthash`, `-key`, `-loginkey`, `-logindata`, `-statindex`, `-processidpath`.

- **No `-server`/`-host`/`-port`** — the endpoint comes from `LocalConfig.xml` (see §3), not the command line.
- **`-noconsole`** sets internal flag bit `0x40000`, but the exe is **GUI-subsystem and never calls AllocConsole**
  (imports `FreeConsole`/`WriteConsoleW`, not `AllocConsole`) — so there's no console and the flag is a visible
  no-op. The client's console output also goes to `AgeOfConan.log` (which `--console` tails — see §2).
- **`-openautomate`** = NVIDIA's benchmark framework (perf plugins; not functional testing).
  **`-autotest`** = the client's built-in **XML ScriptCommand test engine** (control flow, GUI/chat/
  distributed-value commands, reads `autotest.txt`) — powerful but the tests are RDB/script-authored, a much
  bigger lift than the `-username`/`-password`/`-charname` auto-login.

---

## 2. The launcher (`Faolan.ClientLauncher`)

A drop-in stand-in for ConanPatcher: computes the two gate flags and launches the client. `Program.cs` +
`LoginKeyPatcher.cs`.

```
Faolan.ClientLauncher [options]
  --exe <path>        AgeOfConan.exe (or AOC_EXE env; default = the Steam path)
  --dx10              launch AgeOfConanDX10.exe instead
  -u, --username <s>  auto-login account
  -p, --password <s>  auto-login password
  -c, --charname <s>  auto-enter this character
  --universeaddr [a]  point LocalConfig.xml <UniverseAddr> at `a` for the session (bare = 127.0.0.1:7010),
                      reverted on exit (and on Ctrl+C / process exit). Replaces an /etc/hosts override. The
                      original is recorded in a sidecar first, so a hard-killed run self-heals on the next launch.
  --detach            with --universeaddr: revert + return once the client has read the config (fire-and-forget),
                      instead of holding the foreground until the client exits
  --restore           heal a UniverseAddr left redirected by a hard-killed run (via the sidecar), then exit
  --console           foreground; tail AgeOfConan.log into THIS terminal (same content as the client console,
                      no separate wineconsole window); also drops -noconsole
  --patch-login       patch the client's DH key (Funcom -> CELLAO) in memory after launch (password validation;
                      needs ptrace permission — sudo, or works as the launching ancestor)
  --proton <path>     Proton 'proton' script (or PROTON env; default = Steam Proton-Experimental)
  --wine <cmd>        use a plain wine runner instead of Proton
  -n, --dry-run       print computed clienthash/key/args without launching
```

Behaviors:

- **Kills any running client first** (clean repeated launches).
- **Launches via Proton** off Windows — `proton run <exe> <args>` with the Steam compat env from `launchClient.sh`
  (`STEAM_COMPAT_DATA_PATH`, `STEAM_COMPAT_CLIENT_INSTALL_PATH`, `SteamAppId/SteamGameId=217750`, `WINEDEBUG=-all`).
  `--wine` for a plain wine runner; direct on Windows.
- **Keeps the exe GUI-subsystem** (a CUI flip would spawn a separate wineconsole window) and computes `-clienthash`
  from the actual launched file.

---

## 3. Connecting to Faolan instead of retail (no /etc/hosts)

The client reads its login server from **`LocalConfig.xml`** → `<UniverseAddr>dm09-nj4.ageofconan.com:7010</UniverseAddr>`
(confirmed via RE — AgeOfConan.exe references both `LocalConfig.xml` and `UniverseAddr`; it is **not** in the exe).
So instead of an `/etc/hosts` line (`127.0.0.1 dm09-nj4.ageofconan.com`), `--universeaddr` rewrites that field to
`127.0.0.1:7010` for the session and reverts it afterward (byte-identical round-trip verified). `--detach` reverts
after the client has read it rather than holding for the whole session.

---

## 4. Login password validation (optional)

AoC login is **Diffie-Hellman (g=5, 1024-bit prime) + TEA-CBC** (see [login-decryption-plan.md](login-decryption-plan.md)).
By default any password is accepted. To enforce the real password, the client must use **our** DH public key:

- **`--patch-login`** (or `tools/patch_aoc_login_key.py`) overwrites the client's embedded Funcom DH pubkey with
  CELLAO_PUB in `/proc/<pid>/mem` (copy-on-write — the game file is untouched). Needs ptrace permission.
- Server side: `Faolan.Core/Network/LoginEncryption.cs` decrypts the block and, for a patched client, enforces
  `password == Account.Password` (else falls back to accept-any). Set a real `Account.Password` to test rejection.

---

## 5. Automated testing workflow

### Run the server
Config (`faolan.json`) is loaded as `../../../faolan.json` relative to CWD, so run from the build output dir:
```
( cd Faolan/bin/Debug && dotnet Faolan.dll )
```
Listens: UniverseAgent 7010, PlayerAgent 7011, AgentServer 7012, CSPlayerAgent 7001, GameServer 7014, World 7015.

### Capture the loopback session (no root needed)
`dumpcap` carries `cap_net_raw,cap_net_admin` (file caps), so loopback capture works without sudo:
```
tshark -i lo -f "tcp portrange 7001-7015" -w /tmp/session.pcap &
```
The result is a normal pcap the analyzer/diff tools read (`Faolan.PacketAnalyzer`, `Captures/*.py`) — diff it
against a retail capture for regressions, or `--dump` it.

### Launch + drive the client
```
Faolan.ClientLauncher --universeaddr --detach -u faolan -p aa -c tester [--patch-login]
# or foreground with live log:
Faolan.ClientLauncher --universeaddr --console -u faolan -p aa -c tester
```

### Watch for success markers
- **Server log:** `UniverseAgent login accepted for user <u>` → `RequestAuth` → **`EnterWorld (0x20B6)`** →
  CSPlayerAgent `Authenticate` → GameServer `LatencyCheck` traffic (in world).
- **Client log (`<game>/AgeOfConan.log`):** good = `Playfield.Loading`, `ApplyDynamicGameTweaks: Applying 19
  gametweaks`; bad = `invalid camera path`, `stream bad`, `Dispatcher call ... failed`. (RDB-miss /
  `does not exist in any RDB` is benign.)

### Teardown
Stop the client (`pkill -9 -f AgeOfConan` + the proton process) and the server; `--universeaddr` reverts
`LocalConfig.xml` automatically on the launcher's exit.

### Harness (`tools/client-smoke-test.sh`)
A ready-made version of the above: starts the server, captures loopback, launches the client unattended, asserts
the battery of markers (login → EnterWorld → GameServer traffic, gametweaks, no Dispatcher failures, pcap on
7010+7014), and tears down (reverting `UniverseAddr`). Writes a PASS/FAIL report to `/tmp/faolan-test-report.txt`.

### Proven run (2026-06-10)
`tools/client-smoke-test.sh` → **14/14 PASS**: Proton launched the client → server logged `login accepted` →
`RequestAuth` → `EnterWorld (0x20B6)` → CSPlayerAgent auth → in-world GameServer traffic; client applied the
gametweaks with no Dispatcher failures; the loopback pcap held 360 packets across 7010+7014; the launcher reverted
`UniverseAddr` and cleaned up its sidecar — all with **no manual interaction**. This unblocks the **TODO #7**
create-world loopback-capture diff (its pinned next step needed exactly this repeatable live run).

---

## File map
- `Faolan.ClientLauncher/Program.cs`, `LoginKeyPatcher.cs` — the launcher.
- `Faolan.Core/Crypt/ConanLaunchToken.cs` (the `-key` generator), `Tea.cs` (shared cipher).
- `Faolan.Core/Network/LoginEncryption.cs` — server-side DH+TEA login decrypt.
- `tools/patch_aoc_login_key.py` — standalone in-memory DH-key patcher (the launcher ports this).
- `launchClient.sh` — the Proton launch env the launcher mirrors.
