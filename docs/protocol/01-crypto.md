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
  challenge check, and the caller falls back to accept-any.
  [V3: LoginEncryptionTests.Decrypt_TamperedBlock_DoesNotRecoverChallenge]

The server's public key is exactly `g^ServerPrivate mod N` for the baked-in keypair — this is the value the
launcher patches into the client. [V3: LoginEncryptionTests.ServerPublic_IsCellaoPublicKey]

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
