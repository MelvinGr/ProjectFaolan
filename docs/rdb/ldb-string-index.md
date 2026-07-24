# The LDB localized-string index (RDB type 1030002)

The **LDB** (Language DataBase) is the client's localized-text store: spell/item/area names, descriptions,
dialogue, UI strings. It ships as RDB type `1030002` "TDC2" pages (and, for the login/patcher categories, as
loose `Data/text/<lang>/NNNNNNNN.tdbc` files in the same format). Cracking it backfills the handful of
records whose own template carries no embedded name (#291, #300).

## Page grammar — recovered from the client loader `FUN_0060e5c0`

Each page carries an **explicit per-string id index** *after* the string blob. This corrects the earlier
reading that the pages were flat positional arrays with "no per-string id, unsolvable offline" — that mis-parse
saw the blob as NUL-separated strings and never found the trailing id table, which is why the
`textId = pageBase + ordinal` model only ever worked in scattered coincidental id-blocks.

All fields little-endian:

```
[0..12)   RDB-Id header (u32 type=1030002 | u32 id | u32 type2=1000002)
[12..16)  magic "TDC2" (0x32434454)
[16..20)  category      (50200 item dev-names, 50210 spell names, 50211 descriptions, 50025 area/dialogue, …)
[20..24)  flags
[24..28)  size          (string-blob length, bytes)
[28..32)  count         (number of index entries)
[32..48)  16-byte MD5 of the blob
[48..48+size)               the string blob
[48+size .. +count*16)      count × { u32 id, u32 langOrFlags, u32 offset, u32 length }
```

`string[id]` is recovered directly from the index: entry `i` gives `id`, and `blob[offset .. offset+length)`
(NUL-trimmed, UTF-8) is its value. The parser is `Faolan.Core.Rdb.LdbStringIndex.Parse`.

Verified against captured pins: cat `50210` id `222675` → **"Set's Cold Hatred"**, id `222682` → **"Set's
Ruin"** — and those ids **equal the spell ids** (`0x365D3` / `0x365DA`), so a spell's name is `LDB[50210][spell.Id]`.

## Language is per-PAGE, not per-entry

The same `(category, id)` appears **once per shipped language**, in adjacent page blocks. A single client install
bundles several — the one at hand carries seven for the spell-name category:

| language | cat-50210 page |
|---|---|
| French | 271963 |
| **English** | **271967** |
| German | 271971 |
| Spanish | 295734 |
| Russian | 3874202 |
| Polish | 3874778 |
| Korean | 3987257 |

The per-entry second u32 (`langOrFlags`) is *not* a clean language selector (it is `0` for almost every entry),
so the language must be resolved at the page level. The importer identifies the **English** page by **cribbing**:
score each cat-50210 page's strings against the English names the `Spells` catalog already carries from the
template `ExtractName`. The English page matches ~**97 %**; every other language scores **< 30 %**, so the winner
is unambiguous and self-calibrating (no hard-coded page id).

## What each name category keys

A category's key space is identified by intersecting its string ids with each RDB **type**'s entry ids (873,588
entries, `RdbReader.Entries`). Every name category resolves to exactly one type at ≥93 %, so the key is the
resource id itself — there is no indirection through a separate "name id":

| cat | keys RDB type | ids | hit | content |
|---|---|---|---|---|
| `50200` | `1000020` item template | 76,850 | 76,551 (99 %) | item **name** |
| `50220` | `1000020` item template | 19,359 | 19,348 (99 %) | item **name** (a DISJOINT id set — see below) |
| `50201` | `1000020` item template | 4,264 | 4,006 (93 %) | item **description** ("Deals piercing damage over time.") |
| `50210` | `1040005` spell | 20,356 | 19,929 (97 %) | spell name |
| `50211` | `1040005` spell | 3,754 | — | spell description |

**`50200` and `50220` are two halves of one id space, not name vs. polished-name.** They overlap in exactly
**4** of 96,209 ids, and on all 4 they carry the *same* string (`126183` → "Zealot: HP Regeneration (1)" in
both). Between them they name **95,895 of the 98,048** item templates. `50220`'s half is the
training/proficiency items ("Basic Cloth Armor", "Equip: Cloth Armor", "1HB Damage and Combos 40-60"); all
3,240 of our named catalog items fall in `50200`'s half, and **none** in `50220`'s.

So `LDB[50200][item.Id]` on the English page **is** the localized, player-facing name — it is not shadowed by a
polished name elsewhere. Cross-checking the English page (226593) against the French one (226589): **84 %** of
the 76,841 entries genuinely differ (translated), and the 16 % that are byte-identical are the dev codenames for
NPC-only gear (`NPC_Stygia_splintmail_medium_chest_(A0013/A0118)`) — untranslated because retail displayed them
as-is. Restricted to our 3,240 items the split is 2,176 translated / 1,064 dev codenames.

Where the catalog and the LDB disagree (**9** items) the LDB is the *fuller* string, because the template's
embedded `ExtractName` is an internal short name: template `716360` embeds `Hathor-Ka`, the LDB says
**"Hathor-Ka's Note"** (likewise `Alchemist` → "Alchemist's Offal Creel", `Veteran` → "Veteran's Path"). The
other 6 are capture-derived names for NPC gear (`Scavenger Lackey Boots` vs the LDB's dev codename). The
importer's fill-EMPTIES-only policy leaves all 9 alone deliberately — overwriting a capture-observed name from
an asset file is the one direction this project doesn't take.

## Importer

`RdbLdbNameImporter` (module `rdb-ldb-names`, runs after `rdb-spells`) parses the name-category pages, crib-picks
the English page per category, and backfills **only EMPTY** names — `Spells.Name` from `LDB[50210][spell.Id]` and
`Items.Name` from `LDB[50200][item.Id]`. Filling only empties means a wrong page could never overwrite a good
template name, and if no page cribs clean (`≥ 80 %` over `≥ 50` shared ids) nothing is written. Run:

```zsh
dotnet run --project Faolan.Importer -- --only rdb-ldb-names --rdb <RDB> --captures <data-repo>/Captures
```

Result on the current data: **1 spell** (222675) and **1015 items** backfilled (item empties `1020 → 5`).

Diagnostic: `--report ldb-resolve` dumps the whole id→string map, the per-category / per-language breakdown, and
the spell + item crib scores.

## Still open

- **Item descriptions** — cat `50201` is keyed by item id and holds the tooltip text (4,006 templates, 421 of
  them ours). Nothing reads it yet; `Items` has no description column.
- **Catalog coverage** — the LDB names **76,546** item templates but the catalog holds **3,245** rows, because an
  item only enters it when a capture yields its TEA key quad. The 73,339 named-but-uncataloged templates are a
  name source for whatever fills that gap.
- **Missing catalog rows** — e.g. spell `222682` is absent from `Spells` entirely (an extraction gap, #402), so
  there is no row to name; that is separate from the name *source* this solved.
- **Appearance (hair) names** (#291's original target) — the hair LDB page holds only ~15 internal art codenames,
  and a hair meshid maps to a style-group not present in the registry/XML, so the localized hair names this issue
  hoped for don't exist in a usable form. Face/head/tattoo/beard/piercing already resolve from their own `name=`.
