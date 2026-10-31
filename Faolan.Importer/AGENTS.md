# Faolan.Importer: harvesting captures and the RDB into faolan.db

**An import prints the count it managed, never the count available**, so a parser bug arrives looking like a fact
about the captures. A gap is a parser bug until the source proves otherwise: check a module's count against what the
source holds (`rdb.EntriesOfType(t).Count()`, the distinct ids in the dumps). Each of these reports success while
losing data:
- A `continue` in an import loop is unlogged data loss. Suspect the "obviously optional" variant: a self-closing
  element, an absent attribute, the first item in a sequence (nothing precedes it). Carve RDB XML with
  `RdbXml.Carve(text, rootTag)`, never `IndexOf('<')`: an RDB body opens with a binary header that can contain `<`.
- The capture importers upsert through an explicit refresh list, so a new column harvested by the extractor still
  lands only on rows being inserted. Add it to the importer's refresh block, guarded so a harvest without the value
  cannot blank it; do not replace the list with a whole-entity copy, which would clobber server-only fields.
- A field written only on insert (`if (exists) continue; Add(...)`) is frozen: a later capture cannot correct it,
  and `--dry-run` shows 0 rows changed. Fill a missing value, never overwrite a present one with 0.
- A key chosen over a parsed element list (`map[naturalKey] = value`) deletes mechanics, not duplicates: a life
  steal states two health elements, a level-banded effect one per band. Key on the record's own element order,
  `(parent, Sequence)`, and resolve at the point of use (`SpellEffectBands`).
- A bootstrap-only rule (`QuestGoalWiring` skips a goal `IsWired` already set) is a no-op on a populated database,
  and `--only quests` also runs the seed under test, which restores its own values. To see what a rule produces,
  reset the columns it writes in a copy and run without the seed's own step; 0 divergent rows is the target.
- The importer does not create its target database: copy `faolan.db` first, and confirm the run by a line it always
  prints, not only by the value you are measuring. `sqlite3 <db> "SQL" < file` ignores the file.

**Rows missing from a re-harvest are not stale.** `faolan.db` holds content harvested from captures that are no
longer in the corpus, so it is a superset of what the current dumps rebuild. Never delete a row because the current
harvest does not produce it: check whether it is referenced, and whether another module wrote its columns.
`--mode overwrite` on `world-objects` deletes what the harvest missed, so it is safe only over a corpus that is a
superset of the one that built the table.

**`--only npcs` wipes the stamps later modules write.** `NpcSpawn` has a derived id, so a targeted run re-creates
every spawn and leaves the `npc-placements` and `npc-disposition` columns null, with unchanged row counts. After
changing the spawn harvest run the full import (`--skip characters`), then regenerate `faolan.sql` before testing
(the suite reads the seed; see `Faolan.Tests/AGENTS.md`).

**A literal seed table of game content is a smell.** Before writing one, find the packet or RDB record that states
it: a hand transcription loses rows and gets digits wrong, and a seed that runs after a harvest silently beats the
wire. Where a seed is legitimate (a value no capture carries), say so in its comment.

**A captured row is its placement plus the state of that second.** A chest's contents, a door's open state and a
resource node's current health were captured mid-play. Keep the row as captured and have the send path stamp the
live value into `Build`; a round-trip gate cannot see this, a census of the value's shape across rows can.

**Check a refactor with a dry-run diff.** A change to what a comparison compares (an opcode, an enum, a codec id)
fails silently in an extractor, and the suite does not run the extractors over the corpus. Run the whole importer
with `--dry-run` against a copy of `faolan.db` before and after, and diff the per-module log lines; on a populated
copy the row-delta table says "no row-count changes", so the module counts are the signal. Run the unchanged code
into a second copy as well before attributing a difference to your change.

**`CapturedPacket.Record` versus `.Body`.** `.Body` still opens with the carrier's u32 length; `.Record` is the IIR
record a codec's `Parse` takes, bounded by the declared length. Dispatch on `p.SubOpcode` (always `body[4:8]`), hand
`p.Record` to a codec, and read a `SpawnPacketBuilder.*Offset` constant off `p.Body`: off `.Record` every field is
four bytes early and still looks plausible. `CapturedRecords.TryParse` is the mixed-stream shape, and it swallows
only a `CodecParseException` from a capture truncated mid-record.
