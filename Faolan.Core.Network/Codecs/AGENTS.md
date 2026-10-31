# Codec conventions

These rules are binding for every codec under `Faolan.Core.Network/Codecs/`: `Faolan.Tests/CodecConventionTests.cs`
and the gates in `Faolan.Tests/CodecSurfaceConventionTests.cs` run over every folder under `Codecs/` with no skips, and
every allowlist is empty - every codec with a standalone body parses and re-builds every captured body byte-exact. An
allowlist is not a place to silence a gate; adding an entry means naming the decode issue that removes it.
- **One codec shape: two public method names.** A codec is a `static class` exposing exactly `byte[] Build(...)` and
  `T Parse(in ReadOnlySpan<byte> data)`, plus its `Opcode` / `HandledTypes` declaration. No
  `Encode`/`Decode`/`TryParse`/`BuildBody`/`ToArray`/public `Read`/`Write`, and no `ICodec<T>`.
  - **`Build(T value)` is canonical and is the only method that writes bytes.** `Build` may be overloaded in any shape
    (loose scalars, a DB row - whatever the caller holds), but every other overload delegates to `Build(T)`. `Parse` is
    never overloaded. One encoding path per codec is what makes the round-trip gate binding.
  - **`Parse` is strict: it throws `CodecParseException`** on a short or overlong body, a wrong type word, or a field it
    cannot account for - never a null, a `false` or a default. Callers scanning a mixed stream dispatch on the type
    word at offset 0 first.
  - **The value `T` is a plain record** named for the client IIR verbatim (`MagicBuffCodec.Parse` -> `MagicBuffIIR`).
    It does not serialize itself.
  - **Composition is `internal`:** `internal static T Read(ref SpanReader)` / `internal static void Write(FaolanStream,
    T)`. `InternalsVisibleTo Faolan.Tests` is global, so these stay testable.
- **String codecs follow the same rules with `string` in place of `byte[]`.** The `remoteformat` XML layer inside a
  byte codec and the row<->XML mappers around it (`InnerElementCodec`, `RemoteFormatScreenCodec`, `NpcChatterCodec`,
  `ConversationScreenCodec`, `AttributeChangedCodec`, ...) expose `string Build(T)` / `T Parse(string)`. The `byte[]`
  form stays as a delegating overload wherever the text is the whole wire payload
  (`=> Encoding.Latin1.GetBytes(Build(value))`; Latin1 is the wire's string encoding). A codec is one shape or the
  other, never both `Parse` forms.
- **Read sequentially through `SpanReader` (`Faolan.Core/Extensions`); no const offsets; `Parse` ends with
  `EnsureAtEnd()`.** A `private const int FooOffset = 17` means the bytes before 17 were never decoded, so the codec
  cannot tell a correct body from one that merely has the right word at 17. `SpanReader` owns the bounds checks. The
  rule is about reading, so the gate covers files that declare a `Parse`: a build/patch-only neighbour
  (`SpawnTransformPatch`, `SpawnPacketBuilder`) may address a fixed-width head by offset.
- **`Build` returns the body without the length prefix.** The u32 length belongs to the framing sender.
- **Dispose the stream:** `using var s = new FaolanStream(); ... return s.ToArray();`, never
  `return new FaolanStream().Write(...).ToArray();` (`FaolanStream : MemoryStream` is `IDisposable`).
- **Every codec with a standalone body declares its identity in one uniform place:**
  `public const uint Opcode = (uint)IirTypes.<Type>;` (single type) or `public static readonly IirTypes[] HandledTypes`
  (a genuine family). An IIR opcode never appears as a bare hex literal in `Codecs/`; the test fails on it. This is what
  makes `grep IirTypes.<Type>` find every emitter, and what `docs/protocol/gen/coverage.md` reads to attribute each
  type to its builder. One IIR, one codec: two codecs declaring the same opcode must be merged.
  **RPC-message codecs** (whole method bodies: `GuildSearchResultCodec`, `MailMessageCodec`, ...) declare
  `public const uint Method = (uint)<Interface>Methods.<Method>;` on every interface, hash-registered or not, and are
  named for the client's RPC method. When several methods share one body (`DispatchTeamJoin` / `DispatchTeamLeave` /
  `DispatchSetNewTeamLeader`), declare `public static readonly <Interface>Methods[] HandledMethods` instead. There is no
  `*Opcodes` enum outside the two `ChatServer*Opcodes` (chat carries no RpcHeader, so its numbers really are opcodes).
  **Sub-record codecs** (`CharacterStatBlock`, `StatList`, `MoveTaskNode`, ...) are composed inside a larger body,
  never sent alone, and declare nothing.
  A c2s IIR whose sub-opcode travels in the RPC frame has a body that starts at the entity class; such a codec declares
  `public const bool OpcodeIsInTheFrame = true` (`DeleteItemCodec`, `MoveInventoryItemCodec`), which the surface gate
  reads.
- **Count-framed lists come from `ListFrame`, never a hand-written `0x3F1`.** A list is prefixed by a u32
  `(count + 1) * 0x3F1` (guilds, AH, quests, spell book, shortcut bar, stat lists, spawn scaffold, resurrection spots).
  A one-element list frames as `0x7E2` and an empty one as `0x3F1`; because single-element and empty lists dominate
  the captures, both read as magic constants. They never are: of the 136 functions in `AgeOfConan.exe` that touch the
  word, 77 divide by `0x3F1` to read a count and 59 multiply to write one, and none compares it to a constant
  (`tools/ghidra/DumpListFraming.java`). A value equal to `n * 0x3F1` is a list count; distrust any grammar pinned only
  by single-element or empty captures. `CodecConventionTests` enforces this across `Codecs/` and `Faolan/`.
- **Convenience forms are value factories, not `Build` overloads,** where several semantic forms share a parameter
  list (`MoveTaskCodec`'s path and patrol are both "a waypoint list plus a float", so they are `MoveTaskNodes.Path` /
  `.Patrol`). Everything next to a codec that is not encode/decode is named for what it is and stays off the codec's
  surface: row<->column mappers (`WorldObjectColumns`, `ConversationScreenColumns`), live-value policy
  (`RemoteFormatSession`, `GameClock`), route/geometry math (`PatrolRoute`). A DB-typed `Build` overload is legal (it is
  one of the shapes `Build` may take); it either delegates to a named mapper (`MailColumns.ToMessage`,
  `NpcChatterColumns.ToBark`, `ConversationScreenColumns.ToScreen`) or is a single-expression value construction.
- Files are one type per file, named after that type (a tightly cohesive group may share one file, e.g.
  `FullCharSnapshot/FullCharSnapshotSubClass.cs`), filed under their domain folder.
