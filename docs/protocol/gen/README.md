# `docs/protocol/gen/` — GENERATED, do not hand-edit

Everything in this directory is produced from the repo's own sources of truth:

| File | Generated from |
|------|----------------|
| `opcodes.md` | `Faolan.Core.Network/Constants/Opcodes.cs` |
| `iir-types.md` | `Opcodes.cs` (BKDR-31 hash recomputed from each type name as proof) |
| `protobuf.md` | `Faolan.Core.Network/Protobuf/aoc-client-protos.proto` |
| `coverage.md` | the repo (codecs, golden tests, dissectors) |

A regeneration test in `Faolan.Tests` fails if these drift from their sources, so a change to
`Opcodes.cs` must be accompanied by a regenerated page in the same commit.

Edit the generator, never the output. See `../PLAN.md`.
