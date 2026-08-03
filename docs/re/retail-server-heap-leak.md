# The retail server's own memory, leaked through the NPC spawn scaffold

Every captured NPC spawn carries ~200 bytes of the **retail server's own heap**. This is what is in it.

Harvest it with:

```zsh
dotnet run --project Faolan.Importer -- --report scaffold-heap --captures <data-repo>/Captures --RdbDir <rdb-dir>
```

`--RdbDir` is optional (it is only used to decrypt the LDB strings the harvest finds; without it they are
named but not read). `--report-arg runs` lists every distinct string; `--report-arg dump` writes a hex dump
of every distinct block.

## Why there is a leak at all

The FA015807 pre-IIR scaffold is a **stat block**: 6 flag bytes + 53 u32 that
`SimpleCharFullUpdateIIR_t::Apply` pushes into the dynel as `SetStat(statId, value)`, one explicit id per
slot ([`fa015807-spawn-reader.md`](fa015807-spawn-reader.md), `SpawnPreIir.WordStatIds`). What the ids say it
holds is the character's **PvP / minigame career**.

An NPC has no PvP career, and the retail server writes only **3 of the 53 words** on an NPC spawn (w17, w18,
w38). It does not clear the other 50 — it sends whatever was in that memory.
[V3: SpawnPreIirCorpusTests.OnlyThreeScaffoldSlots_AreWrittenOnAnNpcSpawn] So each NPC spawn ships 200 bytes
of unrelated server-side data, and 591 captured Pillagers carry 551 distinct blocks.
[V3: SpawnPreIirCorpusTests.AnNpcsScaffold_IsPerSighting_NotPerType] The leak is NPC-only — a player's block
is that character's real career, not a leak. [V3: SpawnPreIirCorpusTests.OnlyNpcScaffolds_CarryLeakedServerText]

Across the whole corpus: **17,400 NPC spawns → 10,754 distinct blocks → 3.3 MB** of retail server memory.
(Not `[Vn:]`-tagged — the test suite reads the committed fixture slice, not the private corpus, so this
number is re-derived by running the report, not by a gate.)

## What the view costs

The 53 words are scattered across the **non-contiguous** C++ struct range `0x184..0x267`, so reading them as
text means reassembling them in struct-offset order, little-endian (`SpawnPreIir.DecodeScriptBuffer`). Two
limits follow, and both bound every count below:

- **The window is 228 bytes.** Almost every leaked string is cut at one or both ends.
- **4 of the 57 slots in that range are never written by the client reader either** (struct `0x1f8`, `0x20c`,
  `0x22c`, `0x250` = buffer offsets 116, 136, 168, 204). They are not on the wire at all, so they read as
  zero and **split any string that spans one**. A string cut this way is recoverable only from another block
  that happened to catch it at a different alignment.

⚠️ **This is a diagnostic view, never a source of content.** Nothing here describes the NPC it arrived on,
and none of it may be lifted into a table.

## ⚠️ How to count it without inventing data

Extraction runs **per maximal printable run**, because a run boundary is the only thing that can be a string
boundary: the bytes either side of it were never part of one C string. Every pattern is anchored on a
**closing delimiter** (`ScriptGroup '<name>' (id=<n>)` needs both quotes and the paren), so a name the window
truncated is dropped rather than half-reported.

That is not a stylistic choice. The first pass over this data ran its regexes over the **whole 228-byte
buffer**, letting a match span the non-printable gaps between one leaked string and the next, and reported
**133 ScriptGroups, 121 source files, and 26 `key=` values across eight categories**. The honest figures are
**56, 32 and 9 across eight categories** — the rest were manufactured by matches that crossed a gap
(`category=` in one string plus `000` from the binary after it reads as category 50000). Roughly half the
original findings were artifacts.

The one field with no closing delimiter is a **source path**, and it shows the failure mode plainly:
unanchored, `([A-Za-z0-9_/]+\.cpp)` matches from wherever the run begins and yields `and.cpp`, `d.cpp`,
`criptcommands/dbprintfscriptcommand.cpp` — **100 "distinct files" where there are 32**. The anchor is the
message's own `defined at ` prefix, which proves the path start was captured.

## The retail script engine

The leak is dominated by the script engine's error formatter:

```
<file>.cpp(<line>) : Error in "ScriptFormula defined at <file>.cpp(<line>) in ScriptGroup '<name>' (id=<id>)
  Script '<name>' (id=<n>) Command '<name>' (id=<n>) ..."
```

- **56 ScriptGroup name → id pairs** — `DefaultIdleStates` (4723238), `HarvestingEncounterSpawnScript`
  (3985349), `PvPMonthlyFestivalArenaOfDeath_Team` (5000420), `SewerInspection` (3038731), `WereWolfShifter`
  (223451), `Tradepost Building script` (3804874), …
- **32 confirmed C++ source files**, with line numbers — `spawnscriptcommand.cpp(66)`,
  `scriptcommands/dbprintfscriptcommand.cpp(16)`, `Teleport/teleportregionscriptcommand.cpp(50)`,
  `AlternateAdvancement/AddAAPvPXPScriptCommand.cpp(25)`, … plus **15 further paths seen only truncated** and
  matching no confirmed one, i.e. files that exist and the corpus has not yet named in full.
- **55 command names** (`getbrainparameter`, `hatelistquery`, `rungameaction`, `setstat`, `bccemote`,
  `spawn`, `switch`, `waitnseconds`, …) and **90 script names** (`main`, `init`, `combatlogic`,
  `messagelistener`, `playfieldevent`, `idlestatescalled`, `knubot`, …).

⚠️ **`id=` is a per-parent INDEX for `Script` and `Command`**, not a type id — the same command name appears
with a dozen different ids (`Command 'dbprintf' (id=1)`, `(id=12)`, `(id=14)`). Only the ScriptGroup id is an
identity.

A second diagnostic format names the same three levels as a **call stack**:
`(0:bccswitchstate(33)|main(1)|DefaultIdleStates` = `depth:command(index)|script(index)|group`.

### ⚠️ The ScriptGroup ids resolve against nothing we hold

Checked and negative, all three ways:

- **Not RDB entry ids** — none of them is an id in the client RDB (`--report rdb-id`).
- **Not in our data** — zero hits across *every* integer column of `faolan.db`.
- **Not a hash of the name** — BKDR-31, BKDR-131, ELF, djb2 and sdbm of the group name all miss.

They are the retail server's own database keys, and their range (150,068 for old content up to 5,031,350 for
festival/achievement content) reads like a content-DB autoincrement allocated over the game's lifetime. So
the names are a genuine catalogue of retail's NPC brain layer, but the ids buy nothing — there is nothing to
reconcile them against.

## The retail server's deployment

Two things the first pass missed entirely, and the most interesting content after the script engine.

**A configuration tree**, rooted at `Universe`:

```
Universe/Territory/Dimension/GameServer/GMPasswordEnabled
Universe[1]/Territory[1]/Dimension[1]/GameServer[12]/GMPasswordEnabled
Universe[1]/Territory[1]/Dimension[2]/GameServer[5]/GMPasswordEnabled
```

The bracketed indices are the find: a **Dimension ran at least 12 GameServer processes**, and one Territory
held at least 2 Dimensions. A Dimension is retail's name for what
[`multi-realm.md`](../subsystems/multi-realm.md) calls a Realm, so this is direct evidence of the shape that
work targets — one realm is many game hosts, not one.

**SQL**, with Oracle bind syntax:

```
... HASH from QUEST_REWARD where CHAR_ID = :1 and QUEST_TEMPLATE_ID = ...
... _AVAILABLE_TO from LOCATION_BUFF t...
... CHAR_ID, KILL_COUNT) ...
```

Tables `QUEST_REWARD` and `LOCATION_BUFF`; a character is keyed by `CHAR_ID`; quest rewards carry a `HASH`
column. `:1` is an Oracle positional bind, so retail persisted on Oracle.

## LDB strings

The server was also holding `remoteformat` XML — the same layer `RemoteFormatXml` builds outbound.

⚠️ **It appears at two escaping depths, and matching only one costs the entire yield.** A `<remoteformat>`
element held as a standalone string is escaped **once** (`key="a&#92;b"`); one nested inside a `<parameter
value="…">` attribute is escaped **twice** (`&lt;localized … key=&quot;a&amp;#92;b&quot; &gt;`). Matching only
the double form finds categories 100 and 51000 — both plaintext — and concludes there is nothing to decrypt.
That conclusion is wrong: the singly-escaped form is where the encrypted categories are.

**9 complete `id` + `category` + `key` references**, across categories 100, 50000, 50002, 50050, 50200,
51000, 52000 and 55000. Two of them name an **encrypted** category (the eleven with `flags=1`: NPC dialogue
50000–50003, quest text 50300–50306), which need a key per string — and both decrypt:

| reference | plaintext |
|---|---|
| `LDB[50000][5757]` | *"My family is dead in the hills, still unburied, and we wait here day after day doing nothing!"* |
| `LDB[50000][6490]` | *"Have there been any orders from higher up? From Pallantides or the King?"* |

Neither string is reachable any other way: `faolan.db` holds **no** category-50000 rows at all, so these are
two lines of retail NPC dialogue recovered purely from the server's leaked memory. See
[`ldb-string-index.md`](../rdb/ldb-string-index.md) for the key format.

## Is it quotable?

The report ships its own safety sweep (`--report scaffold-heap`, last section) rather than asserting the
answer, so it re-derives on every run. Clean for **IPv4 addresses, URLs, e-mail addresses, GUIDs and Windows
paths**. Two hits, both benign:

- `GMPasswordEnabled` — a configuration **key name** in the tree above, not a value.
- `where CHAR_ID = :1` — the SQL fragment above; a retail column name, not data.

No credentials, no personal data, no host addresses.
