# Faolan.ClientLauncher + automated client testing

How to drive the retail AoC client against a local Faolan server without manual clicks — the basis for
automated, capture-backed integration testing. Proven end-to-end 2026-06-10 (login → EnterWorld, unattended).

Pieces: **`Faolan.ClientLauncher`** (launches + drives the client), the **server** (`Faolan` project), **loopback
packet capture** (`tshark`/`dumpcap` on `lo`), and the capture tooling (`tools/capture/*.py` + the Wireshark plugin
`Faolan.Wireshark`). Related: [01-crypto.md](../protocol/01-crypto.md),
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
  **`autotest.txt`** (gated by `-nautotest`) = a LOGIN-AUTOMATION file (`Server`/`Login`/`Character` records,
  parsed in the LauncherWindow) — same job as `-username`/`-password`/`-charname`, via a file, NOT a test
  script (corrected 2026-06-15). The actual test-result channel is the `<testreportscriptcommand>` ScriptCommand
  (RDB scriptgroups → the `Autotest` log line in `AgeOfConan.log`). Both decoded in **`docs/tooling/client-autotest.md`**
  (with the harness sketch for hands-free in-client asserts).

---

## 2. The launcher (`Faolan.ClientLauncher`)

A drop-in stand-in for ConanPatcher: computes the two gate flags and launches the client. `Program.cs` +
`ClientExePatcher.cs`.

```
Faolan.ClientLauncher [options]
  --exe <path>        AgeOfConan.exe (default: auto-detected from the Steam install, app 217750)
  --dx10              launch AgeOfConanDX10.exe instead
  -u, --username <s>  auto-login account
  -p, --password <s>  auto-login password
  -c, --charname <s>  auto-enter this character
  --universeaddr <a>  point LocalConfig.xml <UniverseAddr> (login server) at `a`, e.g. 127.0.0.1:7010 (host:port).
                      Required value. Replaces an /etc/hosts override; persists (undo with --restore)
  --cdn <url>         point the three LocalConfig.xml CDN keys at <url> (each -> <url>/<tag>/upm), e.g.
                      http://127.0.0.1:7081 (the local Faolan.Web.Content). Required value; persists
  --launcher-notes <url>  redirect the client's dead hardcoded launcher-notes URL to <url> (keep the two %s;
                      http:// only - the client's libcurl has no SSL). Required value; on-disk exe patch,
                      covered by the .bak
  --patch-login       patch the DH key (retail's -> ours) on disk before launch so the server can validate the
                      real password. Cross-platform; the exe is backed up to .bak. The key comes from the same
                      faolan.json/environment the server reads (see below)
  --Login:PrivateKey <hex>       one-off override of that key; the patched public half is derived from it
  --Login:PublicKey <hex>        patch THIS public key in instead of our derived one - for a client that should
                      talk to somebody else's server
  --Login:RetailPublicKey <hex>  the key to search for in an unpatched exe (default: retail's)
  --restore           restore AgeOfConan.exe / AgeOfConanDX10.exe / LocalConfig.xml from their .bak, then exit
  --console           foreground; tail AgeOfConan.log into THIS terminal (no separate wineconsole window); also
                      drops -noconsole
  --proton <path>     (Linux only) Proton 'proton' script or PROTON env (default: auto-detected - the build Steam
                      runs AoC with). Ignored on Windows, which runs the exe directly
  --wine <cmd>        (Linux only) use a plain wine runner instead of Proton. Ignored on Windows
  -n, --dry-run       print computed clienthash/key/args without launching
```

Behaviors:

- **Kills any running client first** (clean repeated launches).
- **Launches via Proton** off Windows — `proton run <exe> <args>` with the Steam compat env
  (`STEAM_COMPAT_DATA_PATH` from the auto-detected compat-data, `STEAM_COMPAT_CLIENT_INSTALL_PATH`,
  `SteamAppId/SteamGameId=217750`, `PROTON_USE_WINED3D`/`PROTON_NO_ESYNC`/`PROTON_NO_FSYNC`).
  `--wine` for a plain wine runner; direct on Windows. The exe, compat-data, and Proton build are all auto-detected
  from Steam (see `SteamPaths.cs`).
- **Computes `-clienthash`** from the actual launched file (after any on-disk patch, so the hash matches).

---

## 3. Connecting to Faolan instead of retail (no /etc/hosts)

The client reads its login server from **`LocalConfig.xml`** → `<UniverseAddr>dm09-nj4.ageofconan.com:7010</UniverseAddr>`
(confirmed via RE — AgeOfConan.exe references both `LocalConfig.xml` and `UniverseAddr`; it is **not** in the exe).
So instead of an `/etc/hosts` line (`127.0.0.1 dm09-nj4.ageofconan.com`), `--universeaddr` rewrites that field to
`127.0.0.1:7010`. It PERSISTS (LocalConfig.xml is backed up to `.bak` first; `--restore` puts it back), so nothing
races the client's read of the config.

---

## 4. Login password validation (optional)

AoC login is **Diffie-Hellman (g=5, 1024-bit prime) + TEA-CBC** (see [01-crypto.md](../protocol/01-crypto.md)).
By default any password is accepted. To enforce the real password, the client must use **our** DH public key:

- **`--patch-login`** overwrites the client's embedded retail DH pubkey with **ours** in the `AgeOfConan.exe`
  file — a plain byte swap, cross-platform (no ptrace/process memory). The exe is
  backed up to `.bak` first, so `--restore` puts it back. `--launcher-notes <url>` similarly redirects the client's
  dead hardcoded launcher-notes URL (keep the two `%s`; **http:// only** — the exe's statically-linked
  libcurl 7.19.7 has no SSL backend, so an https URL fails in-client with cURL error 1
  `CURLE_UNSUPPORTED_PROTOCOL`; front https content with a plain-http proxy such as `Faolan.Web.Content`).
- Server side: `Faolan.Core.Network/LoginEncryption.cs` decrypts the block and, for a patched client, enforces
  `password == Account.Password` (else falls back to accept-any). Set a real `Account.Password` to test rejection.
- **The launcher reads the same `faolan.json` (and the same environment) the server does**, so a deployment that
  overrides `Login:PrivateKey` gets the matching public key patched in by derivation — there is no second setting
  to keep in step. `Login:PublicKey` / `Login:RetailPublicKey` cover the two cases where the launcher's key is not
  ours; see [01-crypto.md](../protocol/01-crypto.md#configuring-the-keypair).

---

## 5. Automated testing workflow

### Run the server
`Program.cs` loads `faolan.json` + `faolan.db` from the CWD, and both live at the repo root, so run from the
repo root (running from `bin/Debug` fails with "faolan.json not found"):
```
dotnet run --project Faolan
```
Listens: UniverseAgent 7010, PlayerAgent 7011, ChatServer 7012, CSPlayerAgent 7001, GameServer 7014 (from the
realm row; the world connection shares this same port — the handoff advertises it, it is not port+1), MCP 7099.

### Capture the loopback session (no root needed)
`dumpcap` carries `cap_net_raw,cap_net_admin` (file caps), so loopback capture works without sudo:
```
tshark -i lo -f "tcp portrange 7001-7014" -w /tmp/session.pcap &
```
The result is a normal pcap the capture tools read (`tools/capture/*.py`, the Wireshark plugin) — diff it
against a retail capture for regressions, or `--dump` it.

### Launch + drive the client
```
Faolan.ClientLauncher --universeaddr 127.0.0.1:7010 --cdn http://127.0.0.1:7081 -u faolan -p aa -c tester [--patch-login]
# or foreground with live log:
Faolan.ClientLauncher --universeaddr 127.0.0.1:7010 --console -u faolan -p aa -c tester
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

### Harness (a local `client-smoke-test.sh` — not committed to the repo)
A ready-made version of the above: starts the server, captures loopback, launches the client unattended, asserts
the battery of markers (login → EnterWorld → GameServer traffic, gametweaks, no Dispatcher failures, pcap on
7010+7014), **grabs a frame of the game window once in-world** (the game is an X11/XWayland window, so
`import -window $(xdotool search --name "Age of Conan")`, full-screen fallback) →
`/tmp/faolan-session-screenshot.png`, and tears down (reverting `UniverseAddr`). Writes a PASS/FAIL report to
`/tmp/faolan-test-report.txt`. The screenshot is the visual-assertion layer — confirm the client actually rendered
(world/character-select/cutscene), which logs+packets can only hint at.

### Limits
Auto-login enters an **existing** character only. **New-character creation** (and thus the create-world cutscene)
needs interactive editor input, which the launcher flags can't provide — it'd require input injection
(only `xdotool`/XWayland is present; no Wayland injector, `/dev/uinput` is root-only).

### Proven run (2026-06-10)
The smoke-test script → **14/14 PASS**: Proton launched the client → server logged `login accepted` →
`RequestAuth` → `EnterWorld (0x20B6)` → CSPlayerAgent auth → in-world GameServer traffic; client applied the
gametweaks with no Dispatcher failures; the loopback pcap held 360 packets across 7010+7014; the launcher applied
the `UniverseAddr` redirect — all with **no manual interaction**.

---

## File map
- `Faolan.ClientLauncher/Program.cs`, `ClientExePatcher.cs` — the launcher.
- `Faolan.Core/Crypt/ConanLaunchToken.cs` (the `-key` generator), `Tea.cs` (shared cipher).
- `Faolan.Core.Network/LoginEncryption.cs` — server-side DH+TEA login decrypt.
- `Faolan.ClientLauncher/ClientExePatcher.cs` — the on-disk DH-key + launcher-notes patcher, invoked by `--patch-login` / `--launcher-notes`.
- `Faolan.ClientLauncher/SteamPaths.cs` — Steam auto-detection of the exe / compat-data / Proton build; `run.sh --client` is the convenience wrapper.
