# The verified protocol reference (`docs/protocol/`) — method & conventions

**Goal:** one authoritative reference for the Age of Conan wire protocol — framing, opcodes, message
layouts, encodings — in which **every stated fact carries a verification tag and a citation**, and in which
the machine-derivable parts are *generated* rather than hand-written.

**Why the constraint matters.** Hand-written protocol docs rot. On 2026-07-09 we found five docs that
actively contradicted the code: `create-editor-bursts.md` described `WorldPrepBurst` as "decoded + built +
wired" months after it was deleted; `guilds.md` stated the guild leader's wire rank is 6 when the capture
proves it is 4. A tidy tree of wrong documents is worse than a messy one — it *looks* authoritative. So the
method's spine is: **generate what's derivable, cite what isn't, and let a test enforce both.**

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
The create-world entry burst is byte-stable.               [V3: BurstGoldenTests.CreationScriptBurst_IsByteStable]
Guild leader wire rank is 4.                               [V2: DemoL80_mage-demonologist_login_create-guild.tshark.json frame 366]
16-bit opcodes are method indices (0x2000 + registration). [V1: tools/ghidra/DumpRpcTables.java]
```

These three are linted, not decorative: `ProtocolCitationTests` resolves each against the repo. They are the
format's own proof — when first written they cited a test deleted with `SendWorldPrepBurst`, a capture whose
name was literally `DemoL80_..._create-guild`, and a Ghidra script (`DumpRpcMethods.java`) that never existed.

**The distinction that must never blur:** we can enumerate **100% of the 819 opcodes at V1** — they come from
the client's method tables. But a *message body layout* only exists where a capture does. "The opcode exists"
must never be presented as "the message is decoded." Today that difference is invisible in our docs.

---

## 2. Generate the mechanical parts

`Opcodes.cs` already generates the Wireshark dissector's names. Same source, new targets — all under
`docs/protocol/gen/` and **never hand-edited**:

| File | Generated from | Content |
|------|----------------|---------|
| `gen/opcodes.md` | `Faolan.Core/Enums/Opcodes.cs` | 17 interfaces × 819 entries, direction + class |
| `gen/iir-types.md` | same | 75 32-bit sub-opcodes, 74 with their **BKDR-31 hash recomputed from the type name as proof** |
| `gen/protobuf.md` | `Faolan.Core/Protobuf/aoc-client-protos.proto` | 60 messages, **with `ZigZag`/`Default` field flags surfaced** |
| `gen/coverage.md` | the repo | the coverage matrix (below) |
| `gen/messages.md` | the repo | per-message index: each IIR type → its codec + a cited golden (P5) |

Surfacing the ZigZag flags is not cosmetic: field 3 of `ProtoScryScriptIIR` is ZigZag, and reading it as a
plain varint silently halves every identity value in the character editor.

This plan originally called `aoc-opcode-ids.md`, `aoc-rpc-method-opcodes.md` and `aoc-client-rpc-methods.md`
"drift factories" to delete once `gen/opcodes.md` landed. **P7 reversed that:** they are referenced from CODE
(`Opcodes.cs`, `Endpoints.cs`, `IirTypesPage.cs`) as the RE-*derivation* source — the staged writeup of *how*
the opcode scheme was recovered, which the generated catalog doesn't carry. So they were moved to `re/` (RE
methodology), not deleted. The generated `gen/opcodes.md` supersedes their *catalog* role only.

---

## 3. The coverage matrix — how "every piece of info" becomes measurable

For every opcode, generated from the repo: *has a codec? has a golden? has a dissector? highest verification
level?* This converts the goal from a hope into a burndown — `X of 819 at V3, Y at V2, Z at V1-only, N unverified` —
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

40 codec classes. Skeleton generated from the codec + its golden; semantics hand-filled. **Ordered by verification
level, V3 first.**

---

## 6. Anti-drift: two tests that make "Verified" a property, not a promise

Both live in `Faolan.Tests`:

1. **Regeneration test** — regenerate the `gen/` pages; fail if they differ from what's committed. Changing
   `Opcodes.cs` then *forces* a doc update in the same commit.
2. **Citation linter** — every `[V3: …]` must name a real test method; every `[V1: …]` a real Ghidra script;
   every `[V2: …]` must be a well-formed capture file name. A bogus citation fails the build.

   V2 is the weaker check of the three, on purpose: the corpus lives in the private data repo, so the suite has
   nothing to resolve a capture name *against*. It used to resolve them against a name index carried in
   `corpus-extract.bin`, which meant shipping a listing of the private corpus in every clone. Whether a named
   capture exists is checked by whoever holds it; the linter catches the shape that actually rotted — a payload
   that is not a capture file name at all.

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

