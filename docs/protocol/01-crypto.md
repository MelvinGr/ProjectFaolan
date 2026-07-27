# 01 — Login crypto

Only the login handshake is encrypted. Once the client reaches the world, packets are plaintext (framed per
[`00-transport.md`](00-transport.md)); there is no session cipher.

---

## The scheme

Diffie–Hellman key agreement over a 1024-bit safe prime `N` with generator `g = 5`, then TEA in CBC mode.
[V1: tools/ghidra/DumpFns.java]

The client sends, as one ASCII field, `"<clientPub hex>-<TEA-CBC ciphertext hex>"`. The low 128 bits of the DH
shared secret are the TEA key. The plaintext block is three fields: `username | challenge | password`.

TEA itself: 64-bit block, 128-bit key (four little-endian u32 words), 32 rounds, decrypt sum starting at
`delta*32 = 0xC6EF3720`. The same cipher decrypts RDB item templates, there in ECB mode.

## Why "any password" works

The server reproduces the shared secret with **its own** private key. That only yields the right TEA key if the
client encrypted against the server's public key (`CELLAO_PUB`) — i.e. a client whose login key was patched to
ours.

- A **patched** client → the challenge decrypts correctly and real credential checking is possible.
  [V3: LoginEncryptionTests.RoundTrip_RecoversCredentials]
- An **unpatched** retail client uses Funcom's public key, so the block decrypts to garbage that fails the
  challenge check, and it has proved nothing but the account NAME.
  [V3: LoginEncryptionTests.Decrypt_TamperedBlock_DoesNotRecoverChallenge]

What happens to that unpatched client is `Login:AllowUnpatchedClients` (`UniverseAgentListener`'s
`AnswerChallenge` handler). **True by default** — the accept-any fallback, so enabling password validation never
locks out a client that predates it. Set it false for a deployment that hands out patched clients and wants the
password to actually be required; the refusal is the same `InvalidLogin` reason a wrong password gets, and the
server log names the real cause. Only an explicit `false` denies: an unset or unparseable value allows, because
a typo must not read as "deny every login".
[V3: LoginEncryptionTests.LoginAllowUnpatchedClients_DefaultsToAllowAndOnlyAnExplicitFalseDenies]

The server's public key is exactly `g^ServerPrivate mod N` for the baked-in keypair — this is the value the
launcher patches into the client. [V3: LoginEncryptionTests.ServerPublic_IsCellaoPublicKey]

## Configuring the keypair

All three keys live in `Faolan.Core/Crypt/DhKeys.cs` and come from the shared config (`faolan.json` < command
line < environment), one setting per role:

| key | who reads it | default |
|---|---|---|
| `Login:PrivateKey` | the **server** — the only half it needs | the historical CellAO key |
| `Login:PublicKey` | the **launcher**, as what to patch IN | `g^PrivateKey mod N`, i.e. derived |
| `Login:RetailPublicKey` | the **launcher**, as what to search FOR | the key retail's exe ships |
| `Login:AllowUnpatchedClients` | the **server** — may a client it cannot decrypt log in? | `true` (accept-any) |

The launcher reads the same `faolan.json` the server does, so overriding `Login:PrivateKey` is enough — it
patches the matching public key by derivation and the pair cannot drift.
[V3: LoginEncryptionTests.LauncherPublicKey_IsDerivedFromTheServerPrivateKey]

`Login:PublicKey` exists for the one case where the two genuinely differ: patching a client to talk to somebody
else's server, whose private key you must not need. Setting it does not touch `ServerPublicHex`, which the server
side derives. [V3: LoginEncryptionTests.ConfigurePublic_OverridesOnlyWhatTheLauncherPatchesIn]

⚠️ The default private key is **not a secret** — it is in this repo's history and in every patched client. It is
a default, not a credential. A malformed value throws at startup rather than falling back, because a silent
fallback looks exactly like a working server whose every login is accept-any.
[V3: LoginEncryptionTests.Configure_RejectsJunk_AndEmptyKeepsTheDefault]

The whole exchange is validated end-to-end against a reference Python implementation and a recorded fixture.
[V3: LoginEncryptionTests.Decrypt_MatchesKeyProducedByReferencePython]

## Practical notes

- `LoginEncryption.BuildClientLoginKey` simulates the patched client (DH + TEA-CBC encrypt) to produce the
  exact `AnswerChallenge` body. The **server never calls it** — it exists for tests and launcher work.
- Static-initializer order matters: `N`, `g`, `ServerPrivate` must initialize before anything calls `ModPow`, or
  it sees `N = 0` and throws. The public key is therefore computed per access, not via a field initializer.
- Patch tooling: the launcher's `--patch-login` (`Faolan.ClientLauncher/ClientExePatcher.cs`).

## See also

- [`00-transport.md`](00-transport.md) — the framing the login packets live in.
- [`05-identity.md`](05-identity.md) — what the recovered credentials key into.
