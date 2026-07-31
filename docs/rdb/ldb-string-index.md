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
and the page header's `flags` word is an ENCRYPTION flag rather than a language one (see below) — so neither
says the language.

### The client declares it: the language record (RDB type `1030001`, `TDL1`)

The install ships **exactly one language record per bundled language** (seven here), each naming itself in plain
ASCII and listing **the 152 LDB pages that belong to it**:

```
[0..12)   RDB-Id header
[12..16)  magic "TDL1"
...       the language name in ASCII, twice (English name, then localized: "French" then "Français"),
          a plural-form rule ("(N == 1) /? 1 /: -1"),
          then a table of { u32 pageId, u32 } pairs — this language's pages
```

⚠️ **The page table is not word-aligned in every record.** The leading strings differ in length per language, so
the table starts at a byte offset that is a multiple of 4 only sometimes (it is in the French and Spanish records;
it is *not* in the English one). Read it byte-granularly, anchored on "this value is a real `1030002` page id".
`LdbLanguageIndex` does exactly that and returns page → language; 1,064 of the 1,103 local pages are claimed.

### Do not sniff the language from the text

An earlier version scored each page's prose for English function words. That is not merely approximate — it is
**wrong on this data**: whole pages ship **untranslated**, so the Polish and Korean pages of the spell-description
category (`50211`) contain English sentences and score as English. Unioning every "English-looking" page then lets
the last write per id win, which is how **1,991 of 3,380** imported spell descriptions came out Spanish
(`Aumenta en 1 la Fuerza`), caught in-client. Take the client's word for it.

The name **crib** (score a page against the names the catalog already holds — English matches ~97 %, every other
language < 30 %) is kept on top of the language index as a *check*, not as the way the language is chosen.

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

`RdbLdbNameImporter` (module `rdb-ldb-names`, runs after `rdb-spells`) reads the **English pages only** — selected
by `LdbLanguageIndex` from the client's language records — and backfills **only EMPTY** values: `Spells.Name` from
`LDB[50210][spell.Id]`, `Items.Name` from `LDB[50200][item.Id]`, and the two description columns from `50211` /
`50201`. Filling only empties means a wrong page could never overwrite a good template name; the name crib
(`≥ 80 %` over `≥ 50` shared ids) still runs on top as a check, and a page that fails it contributes nothing. Run:

```zsh
dotnet run --project Faolan.Importer -- --only rdb-ldb-names --rdb <RDB> --captures <data-repo>/Captures
```

Result on the current data: **1 spell** (222675) and **1015 items** backfilled (item empties `1020 → 5`).

Diagnostic: `--report ldb-resolve` dumps the whole id→string map, the per-category / per-language breakdown, and
the spell + item crib scores.

## The client names every category itself — the descriptor table in `TDL1` (2026-07-30)

Each language record (`1030001`) carries **1,603 trailing bytes after its page table** that the grammar above did
not account for. They are a **category descriptor table**: `u32 count` (47), then per entry `u32 category` plus
two length-prefixed strings — the source TABLE and the source FIELD the strings came from. No NUL terminators
here, unlike the record's leading strings.

So a category's meaning is not something to infer by intersecting id spaces; the client states it:

| cat | table | field | | cat | table | field |
|---|---|---|---|---|---|---|
| `50000` | `nc_stockphrases` | stockphrasetext 🔒 | | `50300` | `main_quest_templates` | name 🔒 |
| `50001` | `nc_questions` | questiontext 🔒 | | `50301` | `quest_templates` | task_name 🔒 |
| `50002` | `nc_text` | text 🔒 | | `50302` | `quest_templates` | description 🔒 |
| `50003` | `nc_trade` | tradename 🔒 | | `50303` | `quest_templates` | solvedtext 🔒 |
| `50013`–`50026` | `showsubtitle`, `sayinchat`, `showfifomessage`, … | the feedback channels | | `50304` | `quest_goal_templates` | goal_name 🔒 |
| `50100` | `pvp_area` | city_name | | `50305` | `quest_cooldown_template` | cooldown_text 🔒 |
| `50200`/`50201` | `item` | name / description | | `50306` | `QUEST_MISSION_TYPES` | name 🔒 |
| `50210`/`50211` | `special` | name / description | | `51000` | `npc_template` | name |
| `50220`/`50221` | `feat` | name / — | | **`51200`** | **`bounded_areas`** | **bounded_area** |
| `53000` | `feat_specialized_trees` | name | | **`52000`** | **`regions`** | **region** |
| `54000` | `breedenum` | breed | | `55000` | `simpledynels` | name |
| `55100`/`55150` | `tradeskill_groups` / `tradeskill` | name / group_name | | `55200`/`55300` | `stackinglines` / `spelllines` | linename |
| `51100` | `categorytree` | description | | `55500` | `doorgroup_door` | name |

🔒 = encrypted, see below. Note this corrects the earlier reading of `50210`/`50211` as *spell* name/description:
the client calls that table `special`, and the id space is the spell id, which is why the intersection test landed
on `1040005`.

### `51200` is the bounded-area name table, and quest goals reference it

`bounded_areas` (**400 entries, plaintext**) is the id → name table for the client's trigger volumes
([1000053](bounded-areas-1000053.md)). **374 of its 381 distinct strings are an exact `<name>` of a region in
that table**, which is what identifies it; the handful left over are long HTML zone blurbs sharing the id space.

This is a real cross-reference, not a coincidence: a quest goal's `TrailingCategory`/`TrailingRefId` pair points
straight into it. Quest 10238's goal 1448 ("Enter the jungle") carries `TrailingCategory 51200`,
`TrailingRefId 180`, and **`LDB[51200][180]` = `"Trappers Haven"`** — a 7-vertex region on Tortage Island (4010)
spanning (402, 642)–(498, 725), which contains that goal's own marker at (416, 701). So a `ReachArea` goal's
trigger volume resolves mechanically: `goal → (51200, refId) → region name → the polygon in that playfield's
1000053 record`.

### A quest goal's two LDB references, decoded

The descriptor table also finishes off the quest-goal reference model. A `QuestGoalDefinition` carries **two**
`(category, refId)` pairs, and they mean different things:

| pair | category | points at | readable? |
|---|---|---|---|
| `ObjectiveRefId` + `ObjectiveCategory` | **50304** `quest_goal_templates.goal_name` on all 168 goals; `ObjectiveRefId == GoalId` | the goal's own **display text** ("Kill slavemaster Saddur") | **yes — encrypted, and we hold the keys** (see below) |
| `TrailingRefId` + `TrailingCategory` | 50200 `item.name`, 51000 `npc_template.name`, 51100 `categorytree.description`, 51200 `bounded_areas` | the goal's **target** | **yes — plaintext** |

There is a **third** reference on the goal, and it is not an LDB one — see
[the reserved reference slots](#the-goals-reserved-slots-are-a-third-typed-reference) below.

`TrailingCategory` is therefore a **type tag on the target reference**, and it resolves completely: all **107**
goals with a non-zero `TrailingRefId` resolve in their declared category, giving `Stolen Goods`, `Mangy Yeti
Pelt`, `Gormley's Buried Treasure`, `Quarry Stonebrick`, `Taskmaster Olaf`, `Vanir Master Polluter`,
`Akil-Atsu's Head`, … where `QuestGoalDefinition.TargetName` is currently filled in for only a couple of
hand-seeded goals. (Use the **English** pages — the language record names them — and decode **UTF-8**; merging
all seven languages silently yields French.)

⚠️ **A 51200 reference does NOT by itself mean the goal is a `ReachArea`.** Only three goals carry one:

| goal | quest | map | count | region | objective text | reading |
|---|---|---|---|---|---|---|
| 1448 | 10238 | 4010 | 1 | `Trappers Haven` | "Enter the jungle" | a true reach objective — `ReachArea`, and the region **contains the goal's own marker** |
| 11712 | 17608 | 6400 | **3** | `NightmareRoomBA` | "Overcome the Emperor's Manifested Nightmares" | the region *scopes* the objective — 3 things to overcome inside it, not a place to walk into |
| 14073 | 19364 | (none) | 1 | `BossRoom` | "Locate the Red-Hand Leader" | the goal carries no MapId, yet the name is unique to playfield 2027, so it still resolves |

**Settled: only 1448 is a reach goal**, and the objective text is what settles it — the column that used to be
blocked on #520. That is also why the kind value `9` is `RegionScoped` and not `ReachRegion`: two of its three
goals are not reaches.

### The goal's reserved slots are a third, typed reference

`QuestGoalDefinition.Extra0..Extra6` — the seven "reserved" words — are a lead word plus **three optional
`(marker, identity)` reference slots** at `[1..2]` / `[3..4]` / `[5..6]`. The slots float (goal 11252 uses `[1]`
and `[3]`), and the marker types the identity:

| marker | what it is | identity beside it |
|---|---|---|
| `70099` | the client's **"unset id" sentinel** — `FUN_006dd970`, the `GameCode/Criteria` evaluator, guards every id compare with `!= 0x111d3` and logs `user criteria failed` on a match | the object's **4-character ACGHash name** (`XE7W`, `8MYE`, `61LV`, …) |
| `50003` | a real LDB category — `nc_trade.tradename`, from the descriptor table above | an ordinary **LDB refId** |

**What they refer to** is the goal's own two objects: `[1..2]` is its **target** (the same object
`TrailingCategory`/`TrailingRefId` names) and `[5..6]` is its **NPC**. Four independent facts pin that, and all
four are gated over every shipped goal by `QuestGoalReferenceSlotTests`:

- the three `50003` slots hold the goal's **own `TrailingRefId` verbatim** — the Rosetta stone, since it is the
  one case where the slot is spelled in a vocabulary we already read;
- the token is a **bijection** with the referenced object — 70 tokens ↔ 70 items, 23 ↔ 23 NPCs — so it identifies
  the object rather than the entry (which is what the earlier "random per-entry key" reading had it as);
- **every** `51000` NPC goal carries `[5..6]`;
- the only **item** goals that carry `[5..6]` are all twelve `Separator` 20 **Deliver** goals — exactly the goals
  that need a recipient.

⚠️ **This does not replace the objective text as the importer's source for a Deliver recipient, and it never
will.** The reference is by ACGHash token, and a global token → object map is CLOSED as not recoverable (#547):
the client never dereferences a token, it is absent from the item template, and it is not plaintext in the RDB.
The only map that exists is the 23 pairs from goals that *also* state the NPC's LDB refId, and none of the twelve
Deliver recipients is in it. So reading those recipients out of the objective text is by design, not a stopgap.

The lead word `[0]` is undecoded (0 on 155 goals, then 1/2/4/6 — **not** a count of occupied slots); open work
tracked in Gitea #550.

### The 74 item goals (`50200`) are inert fetch objectives — and the reference does not type them

The largest slice of the trailing references is `item.name`, and it describes a whole family of quests we do not
run: **72 of the 74 sit at `GoalType.Kill` with no `QuestItemId` and no `TargetName`** — the fresh-harvest
default, i.e. never wired. Only the Quarry Stonebrick pair (`CollectItem` + a `TalkToNpc` hand-in) is live.

They are unmistakably gather objectives once named, and `RequiredCount` is real and varied (1, 3, 4, 7, 8, 10,
20): *Stolen Goods* ×8, *Poisonous Gland* ×20, *Shadeleaf* ×10, *Grey Wolf Pelt* ×7, *Vanir Weapons* ×4,
*Mangy Yeti Pelt*, *Akil-Atsu's Head*, *Vulture Brain*, *Scorpion Heart*, *Hyena Testicles*, *Bowl of Set*.

⚠️ **The category says the ref is an item; it does not say how the item is obtained.** The set mixes at least
three mechanics — mob drops (pelts, glands, heads, organs → `LootFromNpc`), world pickups (*Quarry Stonebrick*,
the one already wired → `CollectItem`), and consumables that read as bought or gathered (*Soothing Remedy*,
*Pink Salt*, *Visionwine*). Telling them apart needs the goal's objective text — which is now readable, so
`--report ldb-quest-text` answers it: the *Visionwine* family are `Make <item>` **crafting** goals (kind `5`),
and the `Deliver` goals (kind `20`) name their **recipient** in the text, which is what wires them.

Two facts bound what could be wired even with the type known:

- **Only 23 of the 74 referenced items are in the `Items` catalog**, and `QuestItemId` is a foreign key to it.
- The other 51 **do ship** — 12 of 12 sampled have an RDB `1000020` template — but the template is `RDBH`-wrapped,
  i.e. TEA-encrypted with a per-item key that only a capture yields. So the catalog gap is a KEY gap, not a data
  gap, and it is the same one that leaves 76,546 LDB-named templates against 3,245 catalog rows.

✅ **Inert, but harmless.** These goals sit in the kill-matching pool (`GoalType.Kill`), and `MatchingKillGoal`
matches `TrailingRefId == templateId` before falling back to a name — so an item ref there could in principle be
advanced by killing an NPC whose template id collided. It cannot: NPC template ids span **38–22,982** and these
item refs span **110,891–4,675,657**, disjoint. Fixing them is a feature gap, not a bug.

### …and the 24 NPC goals (`51000`) already work

Worth recording so nobody "fixes" them: all 24 are `GoalType.Kill` referencing an `npc_template`, and
`MatchingKillGoal` resolves by `TrailingRefId` first. So the kill matches on the template id the goal already
carries, and writing `TargetName` from the LDB would be **cosmetic**, not enabling. (It does validate the decode:
on the two goals where a human did set it — Saddur, Scavenger Tuut — `TargetName` equals `NpcTemplates.Name` for
that exact ref.)

## Eleven categories are ENCRYPTED — SOLVED: the server sends a per-string key

The page header's `flags` word is an **encryption flag** — the earlier note that it "is `0` on every page" was
written from plaintext categories only. It separates the two sets perfectly:

| `flags` | categories | strings | length ≡ 0 (mod 8) | printable ASCII |
|---|---|---|---|---|
| `0` | 147 | 1,414,200 | 13.4 % | 63.4 % |
| `1` | **11** | 750,782 | **100.0 %** | **0.0 %** |

The encrypted eleven are exactly the **NPC conversation text** (`50000`–`50003`) and the **quest text**
(`50300`–`50306`) — quest names, task names, descriptions, solved text and goal names for the whole game.

### The recipe

**Every encrypted string is addressed by a `<remoteformat>` the SERVER sends, and that element carries the key.**

```xml
<remoteformat id="1448" category="50304" key="pARATX1!DX]&#92;So+0*A9B" knubot="0"  ></remoteformat>
```

1. The `key` attribute is **XML-escaped on the wire** (310,070 of the corpus's 442,301 `key=` attributes carry an
   entity). Unescape it: the result is **exactly 20 characters** — the client asserts exactly that,
   `sKey.length() == 20` in `ldbface.cpp:37`.
2. Split the 20 characters into **4 groups of 5**, each decoded as **base-85** into one key word:
   `w = w * 85 + (c - 0x21)`, after aliasing `'v'..'z'` (0x76–0x7a) down by `0x3c`. That is the client's
   `FUN_00622080`.
3. Those four words are a **128-bit TEA key**. Decrypt **TEA-ECB, 32 rounds, little-endian block words** — the
   client's `FUN_00a8f550`, which is byte-for-byte the `Tea.DecryptEcb` we already have for item templates.
4. The plaintext is UTF-8, NUL-padded to the 8-byte block.

Verified end to end on both halves of the encrypted set. All **168** quest objective texts decrypt —
`LDB[50304][1448]` = **"Enter the jungle"**, `[3490]` = **"Kill slavemaster Saddur"**, `[3497]` = **"Deliver 3
stone bricks to Turach"**, `[11712]` = "Overcome the Emperor's Manifested Nightmares" — and so does the NPC
dialogue, inline directives and all:

```
screen 33605  cat 50002  Are you certain? The Border Kingdoms are full of angry bastards…
screen 21146  cat 50001  Sign me up.
screen 18243  cat 50002  {camera size=5 emphasis=1}{turn 0}Eh? What's this now?
screen 16905  cat 50002  Have a pleasant eve.{social vixen_stand_squirming3}
```

### Why this is a *server* secret, not a client one

The key never lives in the client, which is why searching for it there failed: a stride-1 TEA/XTEA scan of both
exes, the patcher, 2,612 decompressed RDB records and 95 MB of decoded wire payloads found nothing, and the
scanner was positive-controlled. It was looking for a 16-byte binary constant; the key is a **20-character
base-85 string, different for every string, delivered per reference**.

⚠️ **We already hold 27,037 of these keys**, harvested from captures across eleven columns — the QUEST half
(`QuestGoalDefinition.ObjectiveKey`/`TrailingKey`, `QuestTextRef.KeyText`, `QuestArea.AreaTitleKey`/`MapKey`/
`RegionKey`) and the DIALOGUE half (`ConversationScreen.Key`, `ConversationScreenParameter.ContentKey`,
`StockPhrase.FormatKey`, `NpcChatter.FormatKey`, `WorldObject.DisplayKey`). They are stored as
the **128-bit TEA key** (`ProtoDecryptionKey`, the same shape an item template's key uses), not as the wire
string: the string is a pure base-85 rendering of those four words and `LdbKey.ToWire` regenerates it exactly,
escaping included. That is also why the retail client renders our quest journal correctly — we replay the
captured keys, so it can decrypt its own LDB.

### Reading it back: `--report ldb-quest-text`

The join — ciphertext from the client's LDB pages, keys from our own tables — is a committed diagnostic rather
than a scratch script:

```zsh
dotnet run --project Faolan.Importer -- --report ldb-quest-text --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report ldb-quest-text --report-arg 3497,5792 --RdbDir <rdb-dir>
```

It prints every goal's **kind tag beside its decrypted objective text**, which is the cross-check that matters:
the tag and the text are two independent statements of what the goal asks for, so a disagreement is a bug or an
unmapped kind value. It reports **168/168 decrypted, 0 failed**, and a `DECRYPT FAILED` row would mean the stored
key and the shipped ciphertext disagree — not a padding artefact. `--report-arg` takes goal and/or quest ids.

Two filters in it are load-bearing: the **English** pages come from the language record (a text sniffer unions
languages and yields French), and the **encrypted** pages come from the page header's own `flags` word. The
ciphertext must be read as raw bytes (`LdbStringIndex.ParseRaw`) — the NUL padding is part of the *plaintext*, so
anything that trims or text-decodes the blob first destroys it. `LdbKey.Decrypt` is the whole step.

A string we have never captured a key for stays unreadable — but that is **not a limit of this decode**, it is
the same content model item templates already use: an item enters the catalog when a capture yields its
`Data1..4`, and an LDB string becomes readable when a capture yields its `key=`. Same axis, and a friendlier one
here, since a captured quest carries the keys for its own text along with it.

## Still open

- **Descriptions are imported, nothing reads them** — cat `50201` (item id → tooltip text) fills
  `Items.Description` and cat `50211` (spell id → tooltip text) fills `Spells.Description`, but no armory,
  tooltip or MCP path queries either (Gitea #414).
- **Catalog coverage** — the LDB names **76,546** item templates but the catalog holds **3,245** rows, because an
  item only enters it when a capture yields its TEA key quad. The 73,339 named-but-uncataloged templates are a
  name source for whatever fills that gap.
- **Missing catalog rows** — e.g. spell `222682` is absent from `Spells` entirely (an extraction gap, #402), so
  there is no row to name; that is separate from the name *source* this solved.
- **Appearance (hair) names** (#291's original target) — the hair LDB page holds only ~15 internal art codenames,
  and a hair meshid maps to a style-group not present in the registry/XML, so the localized hair names this issue
  hoped for don't exist in a usable form. Face/head/tattoo/beard/piercing already resolve from their own `name=`.

## String ids are ELF hashes of the token name (2026-07-29)

A string id in the LDB is not opaque: it is the **28-bit ELF hash of the token name** the client looks the
string up by. The exe is full of `FUN_0061f640(<category>, "<Token>")` calls whose second argument is a literal
token name, so **any of them can be resolved to its actual string with no live client** - hash the token, look
the id up in the category.

```python
def elf(s):                      # the classic UNIX ELF/PJW hash, masked to 28 bits
    h = 0
    for c in s:
        h = (h << 4) + ord(c)
        h = (h ^ ((h & 0xF0000000) >> 24)) & 0x0FFFFFFF
    return h
```

Verified on 21 independent token/value pairs: `SkywalkingCommand_Description` → 97813470
(`"This command enables skywalking."`), `SkywalkingEnabled` → 80504532, `SkywalkingDisabled` → 137024644, and
18 `<Class>Command_Name` tokens each landing on exactly the expected verb in category **10002** -
`LogCombatCommand_Name` → `"logcombat"`, `SitCommand_Name` → `"sit"`, `ReloadHotkeysCommand_Name` →
`"reloadhotkeys"`, `ASEvalCommand_Name` → `"aseval"`, and so on.

**Category 10002 is the chat-command page**: it holds every command's verb, its description, and its parameter
names (514 entries). That makes it the catalog of what the client will accept as a `/command` - including
commands with no default hotkey and no GM gate.

The whole page is committed as [`../re/ldb-chat-command-page.tsv`](../re/ldb-chat-command-page.tsv) (514 rows,
`id` + `value`), so a token can be resolved by hashing it and grepping that file.

This is what identified the fly command: `SkywalkingCommand_Name` hashes to 47110773, whose value is **`luke`**
(as in Luke *Skywalker*) - a verb no amount of grepping for "fly" or "skywalk" would have found. See
[`gm-commands.md`](../subsystems/gm-commands.md).
