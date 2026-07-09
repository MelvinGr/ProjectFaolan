# Plan — the verified protocol reference (`docs/protocol/`)

**Goal:** one authoritative reference for the Age of Conan wire protocol — framing, opcodes, message
layouts, encodings — in which **every stated fact carries a verification tag and a citation**, and in which
the machine-derivable parts are *generated* rather than hand-written.

**Why the constraint matters.** Hand-written protocol docs rot. On 2026-07-09 we found five docs that
actively contradicted the code: `create-editor-bursts.md` described `WorldPrepBurst` as "decoded + built +
wired" months after it was deleted; `guilds.md` stated the guild leader's wire rank is 6 when the capture
proves it is 4. A tidy tree of wrong documents is worse than a messy one — it *looks* authoritative. So the
plan's spine is: **generate what's derivable, cite what isn't, and let a test enforce both.**

---

## 1. The verification ladder

Nothing goes in untagged. Every fact is one of:

| Tag | Means | Cites | We have |
|-----|-------|-------|---------|
| **V3** | Byte-exact: a builder reproduces captured bytes | golden test method name | 114 tests |
| **V2** | Capture-observed: a dissector parses the grammar to packet end | capture file + frame | 36 dissectors |
| **V1** | Client-RE: recovered from the exe (reader vtables, dispatch tables) | Ghidra script | 67 scripts |
| **V0** | Assumed / single observation / not proven | — must appear in `gaps.md` | ? |

Citation format, inline, immediately after the claim:

```
The create-world entry burst is byte-stable.               [V3: BurstGoldenTests.CreateWorldEntryBurst_IsByteStable]
Guild leader wire rank is 4.                               [V2: DemoL80_mage-demonologist_login_create-guild.tshark.json frame 366]
16-bit opcodes are method indices (0x2000 + registration). [V1: Captures/ghidra/DumpRpcTables.java]
```

These three are linted, not decorative: `ProtocolCitationTests` resolves each against the repo. They are the
format's own proof — when first written they cited a test deleted with `SendWorldPrepBurst`, a capture whose
name was literally `DemoL80_..._create-guild`, and a Ghidra script (`DumpRpcMethods.java`) that never existed.

**The distinction that must never blur:** we can enumerate **100% of the 791 opcodes at V1** — they come from
the client's method tables. But a *message body layout* only exists where a capture does. "The opcode exists"
must never be presented as "the message is decoded." Today that difference is invisible in our docs.

---

## 2. Generate the mechanical parts

`Opcodes.cs` already generates the Wireshark dissector's names. Same source, new targets — all under
`docs/protocol/gen/` and **never hand-edited**:

| File | Generated from | Content |
|------|----------------|---------|
| `gen/opcodes.md` | `Faolan.Core.Network/Constants/Opcodes.cs` | 17 interfaces × 791 entries, direction + class |
| `gen/iir-types.md` | same | 74 32-bit sub-opcodes, each with its **BKDR-31 hash recomputed from the type name as proof** |
| `gen/protobuf.md` | `Protobuf/aoc-client-protos.proto` | 56 messages, **with `ZigZag`/`Default` field flags surfaced** |
| `gen/coverage.md` | the repo | the coverage matrix (below) |

Surfacing the ZigZag flags is not cosmetic: field 3 of `ProtoScryScriptIIR` is ZigZag, and reading it as a
plain varint silently halves every identity value in the character editor.

These three hand-written docs are **drift factories** and are superseded by `gen/opcodes.md`; delete them
once it lands: `aoc-opcode-ids.md`, `aoc-rpc-method-opcodes.md`, `aoc-client-rpc-methods.md`.

---

## 3. The coverage matrix — how "every piece of info" becomes measurable

For every opcode, generated from the repo: *has a codec? has a golden? has a dissector? highest verification
level?* This converts the goal from a hope into a burndown — `X of 791 at V3, Y at V2, Z at V1-only, N unverified` —
and makes gaps **counted, not hand-waved**.

It also drives the migration order (§6): document the V3 areas first, because those are provably correct.

---

## 4. Hand-written layer pages

Small in number, high in value — currently scattered across `CLAUDE.md` and agent memory:

| Page | Covers |
|------|--------|
| `00-transport.md` | TCP framing, length, CRC32, the compression flag + zlib |
| `01-crypto.md` | login: Diffie–Hellman + TEA-CBC |
| `02-rpc.md` | `RpcHeader`, `ProtoEndpoint`, class-pair multiplexing, 16-bit method index vs 32-bit hash dispatch |
| `03-iir-carrier.md` | the 0x2000 carrier + BKDR-31 sub-opcodes |
| `04-encoding.md` | endianness (BE ints, **LE** protobuf fixed32, **BE** raw floats), varint, **zigzag**, string/array framing |
| `05-identity.md` | wire codes (Sex 2/3, Race, Class, EntityClass); session-volatile vs stable keys |

## 5. Per-message layouts — the long tail

85 codecs. Skeleton generated from the codec + its golden; semantics hand-filled. **Ordered by verification
level, V3 first.**

---

## 6. Anti-drift: two tests that make "Verified" a property, not a promise

Both live in `Faolan.Tests`:

1. **Regeneration test** — regenerate the `gen/` pages; fail if they differ from what's committed. Changing
   `Opcodes.cs` then *forces* a doc update in the same commit.
2. **Citation linter** — every `[V3: …]` must name a real test method; every `[V2: …]` a real capture file;
   every `[V1: …]` a real Ghidra script. A bogus citation fails the build.

The linter is the whole difference between a doc that *claims* to be verified and one that *is*.

---

## 7. `docs/` structure — migrate by verification, not by category

**Do not** pre-sort the 56 flat docs into a taxonomy. Reasons: (a) moving stale files does not make them true;
(b) the grammar docs, the four `iir-readers-*` catalogs and `fa015807-spawn-reader.md` **are** this reference's
raw material — sorting them into `grammars/` + `readers/` while also creating `protocol/` splits the reference
across three directories, which is the problem we are solving.

Instead: as each area earns a verified page, its old scattered doc is **absorbed and deleted**, or reduced to a
one-line pointer. The tree then reflects what has actually been verified.

**The boundary rule** (this is what stops the pile re-forming):

> A doc that describes **the wire** lives in `protocol/` and carries verification tags.
> A doc that describes **our implementation** or **workflow** lives elsewhere.

**The archive rule** (mechanical, checkable):

> Archive a `handoff-*.md` / issue-scoped writeup when its Gitea issue closes. Fix its referrers.

Converged shape:

```
docs/
  protocol/      the verified wire reference (this plan)
    gen/         GENERATED - never hand-edit
  subsystems/    how OUR server implements a domain (combat, chat, guilds, inventory, ...)
  rdb/           client asset formats (rdb-type-catalog, gear-rendering, body morphs, CDN)
  tooling/       launcher, importer, capture workflow
  re/            Ghidra / RE methodology + raw decompile artifacts
  archive/       resolved plans + handoffs whose issue has closed
```

Grouping the leftovers happens **last**, once migration shrinks them and the natural grouping is obvious
rather than guessed.

---

## 8. Phases

- [x] **P0 — Runway.** Archive resolved handoffs; delete dead docs; create `protocol/` + `gen/`.
- [x] **P1 — Generators.** `gen/opcodes.md` (819 entries), `gen/iir-types.md` (75, 73 BKDR-31-self-verified),
      `gen/protobuf.md` (60 msgs / 348 fields, zigzag + fixed32 surfaced), all from the COMPILED types by
      reflection, + the `ProtocolDocsTests` regeneration guard (proven to fail on a one-char drift).
- [x] **P2 — Coverage matrix.** `gen/coverage.md`: 819 RPC opcodes (V1), 75 IIR types (48 V3 / 16 V2 / 11 V1).
      Attribution is **exact**, not heuristic: every codec declares its `Opcode`/`HandledTypes` (#317), and
      `CodecConventionTests` forbids a bare opcode hex literal in `Codecs/`, so nothing can emit an IIR
      unattributed. The 11 V1s are genuinely undecoded. Migration order (P5) is driven by this — V3 areas first.
- [x] **P3 — Citation linter.** `ProtocolCitationTests` resolves every tag in `docs/` (minus `archive/`) against the
      repo: `[V3:]` → a real `[Fact]`/`[Theory]` method, `[V2:]` → a real file under `Captures/`, `[V1:]` → a real
      path, `[V0:]` → an entry in `gaps.md`. Proven to fail on a bogus citation at each level. Prose can now claim
      verification. (It caught its first three on day one — PLAN.md's own examples were all stale.)
- [x] **P4 — Layer pages** (§4), each fact tagged. `00-transport` … `05-identity` + `README.md` (index) +
      `gaps.md` (the V0 register, pulled forward from P6 so unproven facts have a home). Every tag resolves through
      the P3 linter — ~30 citations across the six pages, all V3/V2/V1 backed or listed in `gaps.md`.
- [ ] **P5 — Per-message layouts** (§5), V3 first; absorb + delete the superseded docs as we go.
- [ ] **P6 — `gaps.md`.** Every V0 gets a Gitea issue.
- [ ] **P7 — Regroup the residue** into `subsystems/` / `rdb/` / `tooling/`.

**Expectation to set now:** the finished reference contains *every piece of information we actually have, each
tagged with how we know it, plus a complete and explicit list of what we do not.* Some of the 791 opcodes will
never rise above V1 without new captures — the doc must say so **per opcode**, rather than leave a silent hole.
