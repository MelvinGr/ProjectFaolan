# The fixture archive

**The test suite does not need the data repo.** Every test runs from a plain clone, off one committed file:
`Faolan.Tests/Assets/fixtures.bin` (~2.8 MB, `FixtureArchive`), which holds every byte of capture-derived data the suite
reads. It has two halves, and the difference decides where a new gate belongs:
- **Named fixtures** (`assets/recipe-book-reply.bin`, `assets/mount-spawns.bin`, `golden/SpawnNpcCasilda.bin`, ...): a
  specific packet or one capture's worth, read via `CapturedFixture` (whole Conan packets concatenated; a packet
  self-frames), `TestPaths.AssetBytes`/`TestPaths.Asset` for inputs, or `Golden.Bytes` for expected outputs. Use these
  for a gate that only needs particular packets. They are hand-carved and cannot be re-derived from the captures, so a
  regeneration carries them over untouched.
  - Add or replace one with `--report fixture-set --report-arg <key>=<file>`; remove one with an empty source
    (`<key>=`). Once a gate's expectation has become a SHA-256 pin, remove its retail bytes from the archive
    (`EditorAvatarSnapshotTests` / `GuildPacketTests` are the pattern). A digest only substitutes for a golden the gate
    never reads: a parse input or a partial-span compare still needs the bytes.
  - Re-key one with `--report fixture-rename --report-arg <oldKey>=<newKey>`, in that single operation rather than
    set+remove, so the archive never holds the fixture under both keys or neither. Fixture keys must not carry
    character names.
- **The corpus slice** (~45k packet bodies), read via `CorpusExtract`, is for gates whose assertion is breadth: the
  snapshot round-trips, the protobuf re-encode oracle, the world-object column lift. They would be vacuous on a fixture.
  It is derived: rebuild it with the data repo present (the named half is preserved):

  ```zsh
  dotnet run --project Faolan.Importer -- --report fixtures --captures <data-repo>/Captures
  ```

  A regeneration against a partial corpus would silently weaken every whole-corpus gate while they all still pass, so
  `CorpusExtract.ExpectedCounts` is a ratchet: a regeneration may only add packets, and a short archive throws.
- **A gate comparing shipped data to the corpus belongs in a `--report`, not a test.** The slice is frozen while
  `faolan.db` is built from whatever dumps exist, so the two diverge as captures are added and such a test fails on
  data that is more correct than the evidence it demands. The `*-check` reports are those gates, run against the real
  dumps where there is no second population: `npc-pose-check`, `item-level-check`, `buff-duration-check`,
  `acg-token-check`. A test is the right home only for a property of the code (a round-trip, a field sweep), never of
  the data. A check prints its populations rather than pinning them, and asserts only the direction that can only be a
  defect (`buff-duration-check` fails on an apply that outlasts a row stating a real duration, and merely reports rows
  still at the empty placeholder). `acg-token-check` shows why: its condition is a birthday problem in the item
  population (~1.5 expected collisions per `AcgHash.Salt`, so any salt is clean ~22% of the time) that gets likelier
  with every capture harvested, and its only fix - re-rolling the salt - is a coin flip; as a report it prints the
  count, the expectation to compare against, and the next clean salt.

**The archive carries no capture file names.** `ProtocolCitationTests.V2Citations_AreWellFormed` checks a `[V2:]`
citation's form rather than resolving it, and `Captures/...` paths in docs are not linted, so no listing of the private
corpus ships with a checkout; do not add one. Each corpus entry carries a failure-provenance `s` as an opaque capture id
(the first 8 hex digits of the SHA-256 of the dump's file name), because the dumps are named after the characters
played. `--report fixture-source --report-arg <id> --captures <data-repo>/Captures` maps an id back to its dump, and
`FixtureArchiveTests` keeps a re-harvest from putting names back.

**Character names in the packet bytes** are replaced by equal-length pseudonyms that stay readable against the display
names the source and docs use: `Assas23`, `Demo80`, `Necr80` (for `AssassinL23`/`DemoL80`/`NecroL80`, squeezed to the
byte length the wire fixes). `--report fixture-names --report-arg <name>[,<name>]` reports which fixtures and IIRs still
contain a name and the framing around each hit - feed it the list the commit hook blocks on
(`.git/hooks/personal-names.json`); the residue is protobuf length-delimited fields, so an equal-length substitution
ripples nowhere. `--report fixture-substitute --report-arg "<old>=<new>[,...]" [--dry-run]` performs the substitution
(equal length enforced, framed occurrences only, verifies the old names are gone). Tests that carry the names in their
own inline retail hex (`UnsentMethodCodecTests`, `ItemCreatorNameTests`, `MailMessageCodecTests`,
`TradepostMailBodyTests`, `KnubotCodecTestData`) use the same pseudonyms, and `GuildPacketTests`' SHA-256 pin is derived
from the substituted bytes (its remark explains why that is still a retail pin). If the substitution is ever re-run:
the archive is Brotli-compressed, so an equal-length substitution still changes the file size while every body keeps
its length; and the writer rewrites the whole file, so an interrupted run leaves a truncated archive - `git checkout`
it and re-run. Capture file names in comments and docs are out of scope for this: `.githooks/personal-names.py --tree`
accepts them, and they are how a dump is found in the data repo.
