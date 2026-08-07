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
client encrypted against the server's public key — i.e. a client whose login key was patched to ours.

- A **patched** client → the challenge decrypts correctly and real credential checking is possible.
  [V3: LoginEncryptionTests.RoundTrip_RecoversCredentials]
- An **unpatched** retail client uses Funcom's public key, so the block decrypts to garbage that fails the
  challenge check, and it has proved nothing but the account NAME.
  [V3: LoginEncryptionTests.Decrypt_TamperedBlock_DoesNotRecoverChallenge]

What happens to that unpatched client is `Login:AllowUnpatchedClients` (`UniverseAgentListener`'s
`AnswerChallenge` handler). **True by default** — the accept-any fallback, so enabling password validation never
locks out a client that predates it. Set it false for a deployment that hands out patched clients and wants the
password to actually be required; the refusal is the same `InvalidLogin` reason a wrong password gets, and the
server log names the real cause. Only an explicit `false` denies — a typo must not read as "deny every login" —
and a value that is not a bool at all (`0`, `no`) is REFUSED rather than guessed. It used to silently mean
"allow", which is a setting that reads as applied and is not.
[V3: LoginEncryptionTests.LoginAllowUnpatchedClients_DefaultsToAllowAndOnlyAnExplicitFalseDenies]
[V3: LoginEncryptionTests.AValueThatIsNotABool_IsRefusedRatherThanGuessed]

⚠️ **Without a `Login:PrivateKey` it is forced on, explicit `false` included.** There is nothing to decrypt with,
so every client reads as unpatched; a deny would refuse the lot — a total lockout whose cause sits in a different
setting from the one an operator would go and look at. Only a deployment that actually holds a key can turn
password checking on. [V3: LoginEncryptionTests.WithNoPrivateKey_UnpatchedClientsAreAllowedWhateverTheSettingSays]

The server's public key is exactly `g^serverPrivate mod N` — this is the value the launcher patches into the
client. [V3: LoginEncryptionTests.ThePublicKey_IsDerivedFromThePrivateOne]

## Configuring the keypair

Both keys come from the shared config (`faolan.json` < command line < environment), one setting per role, and
their defaults live in the settings catalog (`Faolan.Core/Cli/FaolanSettings.cs`) with every other default:

| key | who reads it | default |
|---|---|---|
| `Login:PrivateKey` | the **server** — the only half it needs | **none** — a server has no key unless given one |
| `Login:PublicKey` | the **launcher**, as what to patch IN | `g^PrivateKey mod N`, i.e. derived |
| `Login:AllowUnpatchedClients` | the **server** — may a client it cannot decrypt log in? | `true` (accept-any) |

⚠️ **`Login:PrivateKey` has no default, and that is the point.** It used to default to the historical CellAO key,
which made the shipped configuration and the "secure" one the same keypair — a key everybody has is not a
credential, and nothing at startup distinguished a server running on it from one running on its own. Unset now
means *no server identity*: nothing decrypts, no password is ever checked, and every client is accepted on its
account name. The startup line says which of the three states the server came up in.
[V3: LoginEncryptionTests.WithNoPrivateKey_EveryBlockReadsAsUndecryptable]

There is deliberately no setting for the key the launcher SEARCHES for. It finds the 256-hex slot in the exe by
shape — the NUL-delimited run of that width which is not the DH prime — so it can re-patch a client it already
patched, which searching for retail's value could not do.
[V3: ClientExePatcherTests.ItRePatchesAnAlreadyPatchedExe]

The launcher reads the same `faolan.json` the server does, so overriding `Login:PrivateKey` is enough — it
patches the matching public key by derivation (`LoginEncryption.PublicHexFor`) and the pair cannot drift.
[V3: LoginEncryptionTests.ThePublicKey_IsDerivedFromThePrivateOne]

`Login:PublicKey` exists for the one case where the two genuinely differ: patching a client to talk to somebody
else's server, whose private key you must not need. It is read only by the launcher, at the one call site that
patches an exe (`Faolan.ClientLauncher/Program.cs`); the server side never consults it, and derives its own public
half from `Login:PrivateKey` via `LoginEncryption.PublicHexFor`.

A malformed value throws at startup rather than falling back to keyless, because a silent fallback looks exactly
like a working server whose every login is accept-any — which is the state the operator was trying to leave.

A deployment that wants password checking generates a key:

```sh
dotnet run --project Faolan -- --generate-login-key            # prints a keypair; writes nothing
```

It prints both halves and writes nothing: the private one goes under `Login:PrivateKey` in `faolan.user.json` by
hand (gitignored — a keypair is per-deployment, and `faolan.json` is committed), and the **public** one is derived
from it rather than stored, so the two cannot drift. Generating is half the job: every client carries the public
key it was patched with, so each must be re-patched (`Faolan.ClientLauncher --patch-login`) or its login block
stops decrypting — which, again, does not fail loudly.
[V3: LoginEncryptionTests.AGeneratedKey_ParsesBackAsAnExponent]

A malformed key is refused where it is READ, not where it is used: the `Login:PrivateKey` / `Login:PublicKey`
config accessors hand back a parsed `BigInteger?` rather than hex text (`LoginEncryption.ParseKey`), so junk throws
once, naming its setting, and an unset key is a plain null — the keyless deployment, which is a running state.
[V3: LoginEncryptionTests.ParseKey_RejectsJunk_AndEmptyMeansNoKeyAtAll]

The whole exchange is validated end-to-end by round-trip: the client half is simulated against the server's
derived public key, and the server half recovers the exact `username|challenge|password` triple back out.
[V3: LoginEncryptionTests.RoundTrip_RecoversCredentials]

## Practical notes

- `LoginEncryption.Encrypt` simulates the patched client (DH + TEA-CBC encrypt) to produce the
  exact `AnswerChallenge` body. The **server never calls it** — it exists for tests and launcher work.
- The DH parameters and the key handling live on `LoginEncryption` itself, and it holds **no state**: the private
  key is a parameter of `Decrypt`, and the public half is derived on demand by `PublicHexFor`. They used to be a
  separate `DhKeys` class whose configured key was static, applied by a `Configure(IConfiguration)` every
  executable had to remember to call — forget it and every login silently degraded to accept-any.
- Static-initializer order still matters within the class: `N` and `G` must initialize before anything calls
  `ModPow`, or it sees `N = 0` and throws. Nothing derived from them is a field initializer for that reason.
- Patch tooling: the launcher's `--patch-login` (`Faolan.ClientLauncher/ClientExePatcher.cs`).

## See also

- [`00-transport.md`](00-transport.md) — the framing the login packets live in.
- [`05-identity.md`](05-identity.md) — what the recovered credentials key into.
