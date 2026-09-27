# The verified protocol reference (`docs/protocol/`) — method & conventions

**Goal:** one authoritative reference for the Age of Conan wire protocol — framing, opcodes, message
layouts, encodings — in which **every stated fact carries a verification tag and a citation**, and in which
the machine-derivable parts are *generated* rather than hand-written.

**Why the constraint matters.** Hand-written protocol docs rot: a page can go on describing a builder as
"decoded + built + wired" long after it was deleted, or state a wire value the capture contradicts. A tidy tree of
wrong documents is worse than a messy one, because it *looks* authoritative. So the method's spine is: **generate
what's derivable, cite what isn't, and let a test enforce both.**

---

## 1. The verification ladder

Nothing goes in untagged. Every fact is one of:

| Tag | Means | Cites |
|-----|-------|-------|
| **V3** | Byte-exact: a builder reproduces captured bytes | golden test method name |
| **V2** | Capture-observed: a dissector parses the grammar to packet end | capture file + frame |
| **V1** | Client-RE: recovered from the exe (reader vtables, dispatch tables) | repo path (usually a Ghidra script) |
| **V0** | Assumed / single observation / not proven | an entry in `gaps.md` |

Citation format, inline, immediately after the claim:

```
The create-world entry burst is byte-stable.               [V3: BurstGoldenTests.CreationScriptBurst_IsByteStable]
Guild leader wire rank is 4.                               [V2: DemoL80_mage-demonologist_login_create-guild.tshark.json frame 366]
RPC methods are registration indices; a dump renders one as `0x2000 + index`. [V1: tools/ghidra/DumpRpcTables.java]
```

These are linted, not decorative: `ProtocolCitationTests` resolves each against the repo, so a citation of a
deleted test or a non-existent script fails the build.

**The distinction that must never blur:** every opcode is enumerable at V1 — they come from the client's method
tables. But a *message body layout* only exists where a capture does. "The opcode exists" must never be presented
as "the message is decoded"; [`gen/coverage.md`](gen/coverage.md) keeps the two apart.

---

## 2. Generate the mechanical parts

`Opcodes.cs` generates the Wireshark dissector's names. The same source generates these pages, all under
`docs/protocol/gen/` and **never hand-edited**:

| File | Generated from | Content |
|------|----------------|---------|
| `gen/opcodes.md` | `Faolan.Core/Enums/Opcodes.cs` | every interface's methods, direction + class |
| `gen/iir-types.md` | same | the 32-bit sub-opcodes, each with its **BKDR-31 hash recomputed from the type name as proof** |
| `gen/protobuf.md` | `Faolan.Core/Protobuf/aoc-client-protos.proto` | every message, **with `ZigZag`/`Default` field flags surfaced** |
| `gen/coverage.md` | the repo | the coverage matrix (below) |
| `gen/messages.md` | the repo | per-message index: each IIR type → its codec + a cited golden |

Surfacing the ZigZag flags is not cosmetic: field 3 of `ProtoScryScriptIIR` is ZigZag, and reading it as a
plain varint silently halves every identity value in the character editor.

`docs/re/aoc-opcode-ids.md`, `docs/re/aoc-rpc-method-opcodes.md` and `docs/re/aoc-client-rpc-methods.md` are the
RE-*derivation* record — *how* the opcode scheme was recovered, which the generated catalog does not carry — and are
referenced from code (`Opcodes.cs`, `Endpoints.cs`, `IirTypesPage.cs`). `gen/opcodes.md` is the catalog.

---

## 3. The coverage matrix — how "every piece of info" becomes measurable

For every opcode, generated from the repo: *has a codec? has a golden? has a dissector? highest verification
level?* That turns the goal into a burndown — `X at V3, Y at V2, Z at V1-only, N unverified` — and makes gaps
**counted, not hand-waved**.

---

## 4. Hand-written layer pages

Small in number, high in value:

| Page | Covers |
|------|--------|
| `00-transport.md` | TCP framing, length, CRC32, the compression flag + zlib |
| `01-crypto.md` | login: Diffie–Hellman + TEA-CBC |
| `02-rpc.md` | `RpcHeader`, `ProtoEndpoint`, class-pair multiplexing, 16-bit method index vs 32-bit hash dispatch |
| `03-iir-carrier.md` | the 0x2000 carrier + BKDR-31 sub-opcodes |
| `04-encoding.md` | endianness (BE ints, **LE** protobuf fixed32, **BE** raw floats), varint, **zigzag**, string/array framing |
| `05-identity.md` | wire codes (Sex 2/3, Race, Class, EntityClass); session-volatile vs stable keys |

## 5. Per-message layouts — the long tail

Each IIR type's layout spec is its codec, indexed with its golden test in [`gen/messages.md`](gen/messages.md).
Semantics that the codec cannot carry are hand-written in the relevant `docs/subsystems/` page.

---

## 6. Anti-drift: two tests that make "Verified" a property, not a promise

Both live in `Faolan.Tests`:

1. **Regeneration test** — regenerate the `gen/` pages; fail if they differ from what's committed. Changing
   `Opcodes.cs` then *forces* a doc update in the same commit.
2. **Citation linter** — every `[V3: …]` must name a real test method; every `[V1: …]` a real Ghidra script;
   every `[V2: …]` must be a well-formed capture file name. A bogus citation fails the build.

   V2 is the weaker check of the three, on purpose: the corpus lives in the private data repo, so the suite has
   nothing to resolve a capture name *against*, and a name index in the committed fixture archive would ship a
   listing of the private corpus in every clone. Whether a named capture exists is checked by whoever holds it; the
   linter catches a payload that is not a capture file name at all.

The linter is the whole difference between a doc that *claims* to be verified and one that *is*.

---

## 7. `docs/` structure — organised by kind

A doc that describes the wire is verified before it lands in `protocol/`; an older scattered doc on the same area
is absorbed into the verified page, or reduced to a one-line pointer, rather than sorted into a parallel taxonomy.

**The boundary rule** (this is what stops the pile re-forming):

> A doc that describes **the wire** lives in `protocol/` and carries verification tags.
> A doc that describes **our implementation** or **workflow** lives elsewhere.

**The confirmed-info rule:** docs carry confirmed information only. Progress, TODOs, next-steps, and
issue-scoped writeups live on the Gitea issue, not in `docs/` — there are no `*-plan.md`/`handoff-*.md` docs.

Shape:

```
docs/
  protocol/      the verified wire reference (this methodology)
    gen/         GENERATED - never hand-edit
  subsystems/    how OUR server implements a domain (combat, chat, guilds, inventory, ...)
  rdb/           client asset formats (rdb-type-catalog, gear-rendering, body morphs, CDN)
  tooling/       launcher, importer, capture workflow
  re/            Ghidra / RE methodology + raw decompile artifacts
```

