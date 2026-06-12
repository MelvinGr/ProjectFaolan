# Login decryption plan (Diffie-Hellman + TEA-CBC)

Status: **investigated & fully understood, not yet implemented.** Algorithm proven end-to-end with a
round-trip test (2026-06-08). This doc has everything needed to implement real password validation later.

Related memory: `login_encryption.md`. Sibling: `docs/archive/rdb-extraction-plan.md` (the RDB uses the *same* TEA cipher).

---

## TL;DR

AoC login is **Diffie-Hellman key agreement + TEA-CBC**, not RSA. The client and server derive a shared secret
via DH; the low 128 bits become a TEA key; the client TEA-CBC-encrypts `username|challenge|password` and the
server decrypts it.

We currently **accept any password** because the decryption is commented out
(`Faolan/UniverseAgent/UniverseAgentListener.cs`, `AnswerChallenge`) and we call `CheckLogin(username, "-")`.

The old `Archive/.../Common/LoginEncryption.cpp` is **correct**, but it only works against a **launcher-patched
client**: the client must use *our* DH public key instead of Funcom's, because we don't have Funcom's DH private
exponent (it lived on Funcom's servers). The retail captures are from the **unpatched** client, so they cannot
be decrypted by us — decrypting them yields garbage, which is expected, not a bug.

To make password validation real, two pieces are needed:
1. **Patch the client's embedded DH public key** → ours (static binary patch of `AgeOfConan.exe`).
2. **Port the DH+TEA decryption to C#** and verify in `AnswerChallenge`.

---

## The handshake (UniverseAgent, port 7010)

Opcodes: `Faolan.Core/Network/Packets/Opcodes.cs` → `UniverseAgentOpcodes` / `UniverseAgentResponseOpcodes`.

1. **C→S `InitiateAuthentication` (0x2000):** body = account name (length-prefixed string) + `uint32`.
   - Handler: `UniverseAgentListener.cs` → `GetAccount(name)` → `InitAuth(client)`.
2. **S→C `InitiateAuthentication` (0x2000):** body = the **challenge** string (32 hex chars / 16 bytes).
   - Sent by `UniverseAgent/Packets.cs::InitAuth` → `WriteString(client.Session.Account.AuthChallenge)`.
   - `Account.AuthChallenge` is `MaxLength(32)`, currently a stored per-account value
     (`Guid.NewGuid().ToString("N")` on creation). In a real capture it was `64653811119f501db0812b357ed19694`.
3. **C→S `AnswerChallenge` (0x2001):** body = one string `"<clientPub hex>-<encryptedBlock hex>"`.
   - `clientPub` = 1024-bit DH ephemeral public key (256 hex chars).
   - `encryptedBlock` = TEA-CBC ciphertext (in the observed capture: 64 bytes = 128 hex chars).
   - Handler: `UniverseAgentListener.cs` → currently reads `cAnswerChallenge` and ignores it.
4. **S→C** on success: `SetRegion` (0x2005) then `SendPlayerAgentRealm` (0x2001) — already implemented.

Observed shapes (captures in `Captures/account-faolan-*-login-*.json`): clientPub = 1024-bit, encryptedBlock =
64 bytes, dash-separated. Consistent across all login captures.

---

## The cryptography

### Diffie-Hellman parameters (baked into the client; also in the Archive source)
- **prime** `N` = `eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f` (1024-bit, prime).
- **generator** `g` = `5`.
- **Funcom server public** `9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f260a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92`
  = `g^funcomServerPriv mod N`. **`funcomServerPriv` is unknown / unrecoverable.**

### Our (CellAO/Faolan) keypair — already exists, reuse it
- **private** `CELLAO_PRIV` = `7ad852c6494f664e8df21446285ecd6f400cf20e1d872ee96136d7744887424b` (256-bit).
- **public** `CELLAO_PUB` = `26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be26516053030b65d5afb60cef6f49de539958ba0b7922a099319b8016a8673cb27a696ae4b60fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70`
  = `g^CELLAO_PRIV mod N`. **Verified:** `5^CELLAO_PRIV mod N == CELLAO_PUB` (exact). N is prime, g=5.
- (We may instead generate a fresh keypair: pick a random 256-bit `priv`, compute `pub = 5^priv mod N`.
  Reusing CELLAO's is simplest and matches the existing `FaolanLauncher` patch bytes.)

### Server-side derivation (what the server does on `AnswerChallenge`)
1. Split the body on `'-'` → `clientPubHex`, `encryptedBlockHex`. Reject if either is empty.
2. `shared = modexp(clientPub, serverPriv, N)` where `serverPriv = CELLAO_PRIV`.
3. TEA key = the shared secret as lowercase hex, **left-padded with '0' to 32 chars, or truncated to the first
   32 chars** (= 16 bytes = four uint32). Each `uint32 key[i]` is read from 8 hex chars as **little-endian**
   (the original `popHexInt`: bytes `b0 b1 b2 b3` → `b0 + (b1<<8) + (b2<<16) + (b3<<24)`).
4. **TEA-CBC decrypt** `encryptedBlock` (16 hex chars = one 8-byte block at a time):
   - Standard TEA decrypt: 32 rounds, `delta = 0x9e3779b9`, `sum` starts at `0xc6ef3720`, decremented by delta.
   - CBC: `prev` starts `{0,0}`; after the TEA round XOR the block with the previous **ciphertext** block;
     then set `prev` = this block's ciphertext.
   - Each 8-byte block = two little-endian uint32; emit them as little-endian bytes.
5. Plaintext layout: `[8 bytes header/nonce][uint32 length BIG-endian @ offset 8][string @ offset 12]`.
   The string is `username|challenge|password` (pipe-separated). Take `length` bytes from offset 12.
6. Verify: `challenge == Account.AuthChallenge` (what we sent) **and** `password == Account.Password`
   (and `username == Account.UserName`). Then proceed; else `SendInvalidLogin(InvalidLogin)`.

The cipher being TEA (not OpenSSL) is confirmed by the client string `tea_unexpand` in `AgeOfConan.exe`.
`libeay32.dll`/`ssleay32.dll` (full OpenSSL) ship with the client but are not used for this.

---

## Why "any password" works today / why the old code looked broken

- `UniverseAgentListener.cs::AnswerChallenge` has the decrypt block commented out and calls
  `Database.CheckLogin(username, "-")` → `Accounts.Any(UserName==username && Password=="-")`... in practice it
  resolves to accept (see the actual method — it currently does not gate on the real password). Net effect:
  password is never checked.
- `LoginEncryption.cpp` uses `serverPrivateKey = 7ad852c6...` (= CELLAO_PRIV). That only yields the correct
  shared secret if the client's `clientPub` was generated against **CELLAO_PUB**. The retail client uses
  **Funcom's** public key, so `clientPub^CELLAO_PRIV mod N` ≠ the client's actual shared secret → TEA key wrong
  → garbage. **This is why decrypting our retail capture fails.** It is not a code bug.
- Fix: make the client use CELLAO_PUB (or our own pub). That is what `FaolanLauncher` does.

---

## Work item 1 — patch the client's embedded DH public key

**For testing there's now a no-file-touch variant: `tools/patch_aoc_login_key.py`** (run as root). It finds the
running `AgeOfConan*.exe`, locates the Funcom pubkey string in `/proc/<pid>/mem`, and overwrites it with
CELLAO_PUB. The client maps its PE `MAP_PRIVATE`, so the write copies-on-write — only the live process changes,
the game file on disk is untouched (no backup, no Steam "verify integrity" fallout). Run after launch / before
login; `--restore` reverses it, `--dry-run` just locates it. Verified 2026-06-09: the key sits in an `r--p` anon
region (Wine copies the PE data section to private anonymous memory), found at one address — so the COW write
applies cleanly. **NOTE: this is a no-op until work item 2 is wired in** (the server still ignores the encrypted
block today). The static binary-patch variant below is the permanent (non-test) alternative.

### Static binary patch (permanent variant)

The client stores Funcom's public key as an ASCII hex C-string. Replace it with our public key (same length:
256 hex chars). `N` and `g` are unchanged (the launcher leaves the prime alone; it overwrites only the public).

- **Targets (found in this install):**
  - `AgeOfConan.exe` — Funcom public `9c32cc23...b9f2e92` at file offset **0x1bf6c18 (29322264)**; the prime
    `eca2e8c8...` at offset **29322528** (leave it). The string is NUL-padded, so a same-length overwrite is safe.
  - `AgeOfConanDX10.exe` — same constants present (patch both; the user may launch either).
- **Method:** static binary patch (back up the originals first). The two exes are signed, but AoC doesn't
  verify its own Authenticode at runtime, so an in-place hex overwrite of the key string is sufficient.
- **Do NOT reuse the old `FaolanLauncher` injector as-is:** it targets an obsolete build — hooks
  `msvcr80.dll` `_execv`/`_spawnv` and looks for `Client.exe`/`GUIFunc.dll`. The current Steam client is
  `AgeOfConan.exe` + `msvcr100.dll`. Its generic `findkeys()` memory-scan-and-replace logic is still valid as a
  reference, but a static patch is simpler and needs no injection. (`Archive/FaolanLauncher/FaolanLauncherDLL.c`.)
- ⚠️ This modifies the user's game binary — back up `AgeOfConan.exe`/`AgeOfConanDX10.exe` and only do it on
  explicit go-ahead. A Steam "verify integrity" will revert the patch (re-apply after).

---

## Work item 2 — port LoginEncryption to C# ✅ DONE (2026-06-09)

Implemented `Faolan.Core/Network/LoginEncryption.cs` (`Decrypt(loginKey)` → `LoginCredentials`, plus
`BuildClientLoginKey(...)` simulating the patched client) on top of the shared `Faolan.Core.Crypt.Tea` (moved
there from `Faolan.Importer`, +`EncryptBlock`). Wired into `UniverseAgentListener.AnswerChallenge` with the
auto-detect / non-breaking fallback below. Tests in `Faolan.Tests/LoginEncryptionTests.cs` (12): self round-trip,
the `faolan|…|hunter2` fixture, **a cross-impl fixture decoding a key produced by the proven Python reference**,
`ServerPublic == CELLAO_PUB`, malformed-input + tamper cases. Build + 106 tests green. **Still pending: nothing in
code — to actually enforce a password, set `Account.Password` to a real value and run `tools/patch_aoc_login_key.py`
(work item 1) on the running client; until then login stays accept-any.**

Original notes:

- New class, e.g. `Faolan.Core/Network/LoginEncryption.cs`:
  - `static (string user, string challenge, string password)? Decrypt(string loginKey)`.
  - DH via `System.Numerics.BigInteger.ModPow(clientPub, serverPriv, N)`. **Watch BigInteger sign/endianness:**
    parse the hex as unsigned (prefix `0` or use `BigInteger.Parse("0"+hex, NumberStyles.HexNumber)` so the top
    bit isn't read as negative); format back to lowercase hex without a sign nibble.
  - TEA-CBC decrypt exactly as in the cpp (32-bit wrap with `& 0xffffffff`, little-endian uint32 packing).
  - Keep `serverPriv = CELLAO_PRIV`, `N`, `g` as constants (or config, see below).
- Wire into `UniverseAgentListener.cs::AnswerChallenge`:
  - **Auto-detecting / non-breaking:** decrypt; if it parses and `challenge == Account.AuthChallenge`, the
    client is patched → enforce `password == Account.Password` (reject on mismatch). If decrypt fails or the
    challenge doesn't match, the client is unpatched → log a warning and **fall back to current accept-any**.
    This means enabling the code never breaks login on an unpatched client, and real validation switches on
    automatically once the client is patched.
  - Alternatively gate strict enforcement behind a config flag (e.g. `appsettings` `EnforceLogin=false`).
- Password storage: `Account.Password` (`MaxLength(128)`) is currently plaintext-compared in
  `DatabaseRepository.CheckLogin`. The decrypted password is plaintext, so a direct compare works; consider
  hashing later (store a hash, compare against `H(decryptedPassword)`).

### Reference: validated algorithm (Python, `/tmp/roundtrip_login.py` at investigation time)
Round-trip (simulated patched client → server) recovered `faolan|<challenge>|hunter2` exactly. Key points the
C# must match: TEA decrypt constants (`delta 0x9e3779b9`, `sum0 0xc6ef3720`, 32 rounds); little-endian uint32
packing in `popHexInt`; CBC with IV `{0,0}` chaining on ciphertext blocks; big-endian `length` at offset 8;
string at offset 12. (The cpp's `substr(12, 8+4+length)` over-reads by 12 bytes — just take `length` bytes from
offset 12.)

---

## Verification path

1. **Unit test the C# port** with a self-contained round-trip (encrypt with a simulated client priv against
   CELLAO_PUB, decrypt server-side with CELLAO_PRIV) — no client needed. Must recover the exact triple.
2. **Patch the client** (work item 1), launch, log in, and confirm the server decrypts a real `AnswerChallenge`
   to `username|challenge|password` with the challenge we sent.
3. Confirm a wrong password is now rejected, and a right one accepted.
4. (Optional) capture a patched-client login and add it as a fixture for regression.

---

## Open questions / notes

- **Generate our own keypair vs reuse CELLAO's?** Reuse is easiest and matches the documented patch bytes. If we
  generate a fresh `priv`, recompute `pub = 5^priv mod N` and patch *that* into the client; keep priv server-side.
- **Is the 8-byte plaintext header meaningful?** It looked like a nonce/IV; we don't need it for validation.
- **The challenge** must be the value we actually sent this session (replay protection). `Account.AuthChallenge`
  is currently static per account; consider regenerating it per `InitiateAuthentication` and storing it on the
  session so each login uses a fresh challenge.
- **RDB tie-in:** the item-template `RDBH` blobs use the same TEA cipher (`tea_unexpand`); their key is a
  per-resource `DecryptionKey_c` the server streams to the client (`RequestRecipeInfo(... key1, key2)`), so
  cracking login does not directly unlock RDB items, but the TEA implementation is shared and reusable.

## File references
- Old algorithm: `Archive/.../trunk/src/Common/LoginEncryption.{cpp,h}` (CellAO, 2015).
- Client key-patch reference: `Archive/FaolanLauncher/FaolanLauncherDLL.c` (`FUNCOM_KEY_*`, `CELLAO_KEY_*`, `findkeys`).
- Our login handler: `Faolan/UniverseAgent/UniverseAgentListener.cs`, `Faolan/UniverseAgent/Packets.cs`.
- DB: `Faolan.Core/Database/Tables/Account.cs` (`AuthChallenge`, `Password`),
  `Faolan.Core/Database/DatabaseRepository.cs` (`CheckLogin`, `GetAccount`).
- Captures: `Captures/account-faolan-character-demol80-login-randomActions.json` (and other `*-login-*`).
- Client binaries: `<AoC>/AgeOfConan.exe` (key string @ 0x1bf6c18), `AgeOfConanDX10.exe`.
