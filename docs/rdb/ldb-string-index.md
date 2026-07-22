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

## Importer

`RdbLdbNameImporter` (module `rdb-ldb-names`, runs after `rdb-spells`) parses every cat-50210 page, crib-picks the
English page, and backfills **only EMPTY** `Spells.Name` from `LDB[50210][spell.Id]`. Filling only empties means a
wrong page could never overwrite a good template name, and if no page cribs clean (`≥ 80 %` over `≥ 50` shared ids)
nothing is written. Run:

```zsh
dotnet run --project Faolan.Importer -- --only rdb-ldb-names --rdb <RDB> --captures <data-repo>/Captures
```

Diagnostic: `--report ldb-resolve` dumps the whole id→string map, the per-category / per-language breakdown, and
the spell-name crib scores.

## Still open

- **Items** (`1076` empty names) — same mechanism against the item-name category (`50200` dev-names / a
  player-facing category), once the item name-id keying is confirmed. Not yet wired.
- **Missing catalog rows** — e.g. spell `222682` is absent from `Spells` entirely (an extraction gap), so there is
  no row to name; that is separate from the name *source* this solved.
- **Appearance (hair) names** (#291's original target) — the hair LDB page holds only ~15 internal art codenames,
  and a hair meshid maps to a style-group not present in the registry/XML, so the localized hair names this issue
  hoped for don't exist in a usable form. Face/head/tattoo/beard/piercing already resolve from their own `name=`.
