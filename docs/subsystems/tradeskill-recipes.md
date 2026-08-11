# Crafting recipe book — TradeskillRequestIIR / TradeskillReplyIIR (decoded 2026-07-06)

Opening the in-client **Crafting → Recipes** window is a request/response on the GameServer connection,
decoded from `Captures/NecroL80_mage-necromancer_shops_open-crrafting-receipies.tshark.json` (frame 2916 →
2926) + Ghidra (`AgeOfConan.exe`, project `tools/ghidra/output`).

## The exchange

1. **C→S `TradeskillRequestIIR_c`** (IIR sub-opcode **`0x06B9CD94`**, on the GameInterface→GameAgent 0x2000
   carrier) — the client opened the book and asks for its recipe list. Body (after `[subop][0xC350][handle]`):
   `[u8 env=0][u32 version=1]`. No payload — just "send my recipes".
2. **S→C `TradeskillReplyIIR_c`** (sub-opcode **`0x9062D559`**, GameAgent 0x58 → GameInterface `<instance>`)
   — the recipe-book list. In the capture: **603 recipes, 209 309 bytes**.

If the server never answers, the book opens empty / stalls. Our server received the request (0x2000
sub-opcode) but only logged it → now it replies from the `Recipes` table.

## Reply grammar (client readers)

`GameCharInterfaceDispatch_t::Dispatch...` → `TradeskillReplyIIR_c::ReadSubClass` (`FUN_007aab30`):
read `u32 version (==1)`, then a **type byte** (`FUN_004e0390`): `1` = single `RecipeLearnt`,
`3` = full list (`FUN_007abb30`). The 4-byte stream reader is `FUN_0048f900` (big-endian).

Reply IIR body (past `[subop][0xC350][handle]`):
```
[u8 env=0][u32 version=1][u8 type=3][u32 c]          # c is 0x3F1-framed: c = (recipes+1)*0x3F1
recipes x record
```
The `0x3F1` (1009) framing is the same list invariant as the quest journal / GameActionList.

### Per record (`FUN_0077be70`)
```
[u32 recipeId]                       # the list-key the loop maps by (== RequestRecipeInfo levelId)
[u32 x3]                             # header (@c = 0xf4241 = 1000001, the tradeskill RDB type)
KEY1 = 0x3F1-list of 18-u32 elements # ingredients: item id + item DecryptionKeys (FUN_0077c4f0 / FUN_005d5150)
[u32 x4]                             # mid header; word 1 (0x1F4) is the CRAFT DURATION in centiseconds (below)
KEY2 = 0x3F1-list of u32             # id list (FUN_004d4c70)
listC = 0x3F1-list of 17-u32 elements # results (FUN_005d4e50)
[u32]                                # @50
[u8]                                 # trailing flag (@0x5c, read by FUN_00cba1b0(...,1) in the loop)
```
Validated byte-exact: the 603 records consume the payload with **0 leftover bytes**
(`TradeskillReplyCodecTests`). Each 16-byte block inside the ingredient/result elements is an item's TEA
`DecryptionKey` — the keys `RequestRecipeInfo` (0x2090) later references to fetch a recipe's item details.

## Implementation

- **`TradeskillReplyCodec`** (`Faolan.Core.Network`): `Parse` (→ a `TradeskillReplyIIR` carrying typed
  `RecipeRecord`s) and `Build` (the whole record: carrier prefix + env/version/type/count + records), with
  `RecipeRecords.FromRow` mapping a stored `Recipe` row to a wire record.
  Fully decoded — no verbatim blob. `Parse` → `Build` round-trips the capture byte-for-byte,
  and so does the DB path (`Recipe` row → `RecipeRecords.FromRow` → `Build`); both are golden-gated in
  `TradeskillReplyCodecTests`. Parse validates every folded constant (header word 0 == list-key, mid header,
  element id-dup / key-dup / `[13..16]` const) so a capture that breaks the layout throws rather than mis-building.
- **`Recipe` table** — **fully columnized** (#193): `Id` (= recipeId = header word 0), `Header1`, `ProfessionId`
  (`0xf4241`/`0xf4242` — see below), `MidFlag` (0/1), `Tail`, `TrailingFlag` (0/1), and the opaque ref-id list as
  `RefId0` (always present) + `RefId1` (nullable; every captured record has 1 or 2). No `Data` blob.
- **Importer** `--only recipes` (`RecipesModule` / `CaptureRecipeImporter`) — scans captures for the reply
  sub-opcode, parses records into `Recipe` rows (columns) + `RecipeItem` children, upserts them. Seeded 603
  from the NecroL80 capture.
- **Handler** `HandleIirCarrier` → `SendCraftingRecipeBook`: on `TradeskillRequestIIR`, loads `Database.GetRecipes()`
  (ordered by id = capture order, `Items` + their `Items` rows included), maps each via `ToRecord`, and sends the
  reply on the world framing.

## Element layout

`RecipeItem` (Role ingredient/result, Position, ItemId, Flag, Trailing) is the wire element; the referenced
`Items` row supplies the rest. The fixed 18-u32 (ingredient) / 17-u32 (result) element:
`[0]=[1]=itemId, [2]=0, [3]=Flag, [4]=Hash, [5..8]=[9..12]=key, [13..16]=const, [17]=Trailing (ingredient only)`.
`ItemId` is a hard FK to `Items` (`Restrict`): the item's per-template TEA key (the key `RequestRecipeInfo`
0x2090 uses) + hash live **once** in `Items` (harvested there from the recipe element by the importer;
verified: no itemId ever carries two different non-zero hashes or keys across the 603 records), so they're
**not** duplicated on `RecipeItem`. `ToRecord` reads them back through the `RecipeItem` → `Items` nav.

## Crafting the recipe

The craft itself is a **behavior**, not its own IIR: `BehaviorId.Tradeskill` (`0x27`) over
`BehaviorControlCenterIIR`, and its trailer is `[u8 flag][u32 recipeId][u32 count]` — **not** the `[from][target]`
pair the rest of the behavior switch reads. The whole transaction, from
`NecroL80_mage-necromancer_login_gameplay_annotated_5`:

```
18:45:25 c2s BehaviorControlCenter  Tradeskill(0x27), trailer 01 0000000F 00000001   -> recipe 15, one of them
18:45:26 s2c   the behavior ack
18:45:30 s2c   DispatchSetItemStat x3      destination-cell bookkeeping
               DispatchAddItem             the product, carrying creator_name "NecroL80"
               DispatchRemoveItem          bag 0x68 pos 19       - Plain Hilt Piece, consumed whole
               DispatchRemoveItem          resource 0x70 pos 13  - Copper, consumed whole
               DispatchSetItemStat         resource 0x70 pos 10 StackSize 2 - Silver, decremented
               DispatchTradeskillComplete  0x0000000F
```

`DispatchTradeskillComplete`'s body is the **recipe id** and nothing else, and recipe 15 in our own catalog is
exactly `Plain Hilt Piece ×1 + Copper ×4 + Silver ×4 → Bloodsilver Khopesh`. The removals corroborate the
quantities to the unit — his copper stack ran 4 → 0 and his silver stack 6 → 2 — so the shipped rows *are*
retail's. [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5.tshark.json fr17374/17376/17401/17402]

`TradeskillService` runs it: check every material first (a player one silver short must keep their copper), grant
the product before consuming (a full bag refuses the craft with the materials still in hand), then take the
materials and send the completion. The product carries the crafter through
`ItemInstance.CreatorName` — see [inventory.md](inventory.md).

Refusals are **silent**, because retail has no failure form here and a completion over a craft that did not happen
would close the client's window on an item it never received.

### The 5-second bar is DATA, and it is server-side

A tradeskill is a **command-queue command** — `CommandQueue\tradeskillcommand.cpp`, `TradeskillCommand_c` — and
the client's duration getter for it (`FUN_0114bf50`) answers per operation:

| operation | duration | where it comes from |
|---|---:|---|
| craft | the recipe's own | `ClientTradeskillRecipe_c` `+0x24`, i.e. the record's mid-header word 1 — `0x1F4` on all 603 shipped recipes |
| put a gem in a socket | 500 | hardcoded in the getter |
| take a gem out | 0 | hardcoded — instant |
| anything else | 100 | hardcoded |

The unit is **centiseconds**, the same as the BCC's `activation_time`/`recharge_time`, so both real values are
**5.00 s**; it is what sizes the client's `TradeskillProgressBarDesc` bar. ⚠️ Do not read the c2s BCC's
`activation_time` as the bar: it is **400** on both the craft and the socket record, i.e. the tradeskill
*animation's* own 4.00 s length, and it is the same on two operations whose durations differ.

The hold is **retail's server**, not a client-side animation — measured on the wire in two captures, corrected for
half the ping RTT observed in each:

| | request | grant | RTT/2 | server-side |
|---|---|---|---:|---:|
| craft | `..._annotated_5` fr17374 18:45:25.441 | fr17402 18:45:30.801 | ~140 ms | **5.07 s** |
| socket | `..._annotated_8` fr8215 06:52:55.276 | fr8248 06:53:00.384 | ~65 ms | **4.98 s** |

[V2: NecroL80_mage-necromancer_login_gameplay_annotated_5.tshark.json fr17374/17402;
NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json fr8215/8248]

So the inbound handler only acks and **stages** the transaction (`TradeskillService.Stage`); the world tick runs it
when the deadline passes (`CompleteDue`, reached through `IWorldOutbound.CompleteDueTradeskills`). Nothing is
checked, taken or granted until then — which is retail's order too: the *completion* decides, so a player who
empties their own bag mid-bar is refused at the end of it rather than at its start. A connection that drops
mid-bar has its staged work thrown away (`Forget`), because the alternative is granting an item to a session that
has gone.

⚠️ `CraftDurationCentiseconds` is a folded codec constant rather than a `Recipe` column *because every captured
recipe carries the one value*. `Parse` throws on anything else, so the day a capture disagrees cannot pass
unnoticed — and that is the day to columnize it.

## Open / follow-ups

- ⭐ **The profession model, and the gate it drives (#735).** A profession is a **linear chain of feats in
  `FeatPrereqs`**, seven ranks deep — each rank names the one below it as its single prerequisite — and eleven of
  them ship, which is exactly AoC's five crafting plus six gathering:

  | | roots (wire feat id of the Novice rung) |
  |---|---|
  | crafting | Weaponsmith `321000` · Armorsmith `321300` · Alchemy `321600` · Architect `321900` · Gemcutter `322200` |
  | gathering | Woodcutting `323200` · Mining `323300` · Prospecting `323400` · Stonecutting `323500` · Weaver `327200` · Skinner `327500` |

  Every one of the 603 recipes states the rung it needs in `RequiredFeatId`, and all of them resolve onto the
  Weaponsmith and Armorsmith ladders — which is the finding the book itself carries: **our "catalog" is ONE
  character's book**, harvested from that character's `TradeskillReplyIIR`, so it holds their two professions and
  no others.

  `TradeskillLadders` walks the chains; `TradeskillService.BookFor` filters the book to the rank the character has
  reached and `HasProfessionFor` gates the craft. A character who has trained nothing gets an **empty book**, which
  is retail — the book is what a profession buys. `.tradeskill <profession> [rank]` is the GM bootstrap standing in
  for the (uncaptured) trainer conversation.

  ⚠️ **The RANK is the chain DEPTH, never the feat's NAME.** All six of Gemcutter's trained ranks are named
  `Gemcutter Novice` in the shipped feat table — a fault in the client's own data — so a name-derived rank gives
  that profession seven Novices and no ladder at all.

  ⚠️ **A character holds SEVERAL rungs of one ladder, not one.** `AddCharacterFeats` keeps the highest rank per
  `id / 100` BASE and a crafting ladder spans three bases (Weaponsmith 3210 / 3211 / 3212), so a Grandmaster holds
  `321001`, `321101`, `321202` — and holds **neither** `321000` (the rung 9 recipes name) **nor** `321201`.
  Membership tests against the recipe's own `RequiredFeatId` hide most of a character's book from them; compare
  depths.

  ⚠️ **The WORKSHOP requirement is not enforced.** 366 of the 603 recipes also carry a `RequiredWorkshopFeatId`
  (artisan rank and up, one workshop tier per rank), and a workshop is a guild-city BUILDING and where the
  character is standing — not a feat they train. Blocked on a guild-city model; see Gitea #761.
  [V3: TradeskillProfessionTests.TheElevenProfessionLadders_WalkOutOfTheShippedPrereqEdges]

- ⭐ **Retail's own record of a learned tradeskill is `CharacterSkills`, and it carries the RANK.** The professions
  ride the **spawn scaffold's feat/ability list** — the `[u8 rank][u32 id]` groups after the entry table —
  imported per character from that character's own capture. **`SkillId` IS a wire feat id**, so it resolves
  straight onto a ladder, tier included. `TradeskillService.ProfessionsOf` reads this list and `CharacterFeats`
  together and takes the deepest rung either reaches.

  | | resolved from the scaffold |
  |---|---|
  | NecroL80 | Armorsmith Grandmaster, Woodcutting Master, Stonecutting Master, Skinner Master, Mining Master, Prospecting Master |
  | DemoL80 | Gemcutter Grandmaster, Woodcutting Apprentice, Stonecutting Apprentice, Skinner Novice, Mining Novice, Prospecting Adept |

  ⚠️ **The earlier reading of this table — "slot 65–75 IS the profession id, and the TIER is not in here" — was a
  POSITIONAL mis-read and is falsified.** Two of the slots carry ids that are not professions at all and are
  IDENTICAL on both characters (`328908` → `Expert Bandaging`, `326000` → `Destiny Quest`); filing those under
  whichever profession their slot was believed to be is what produced "two professions carry a byte-identical
  SkillId at different tiers", and hence "the tier is recorded somewhere else". Resolve the ID, never the slot:
  the slot is not stable across characters either (Woodcutting sits at 66 on one and 67 on the other).
  [V3: TradeskillProfessionTests.AnImportedCharactersScaffold_CarriesTheirProfessionsAndRanks]

- ⚠️ **The LEARN path itself is still uncaptured.** No capture teaches a profession — both annotated crafting
  sessions are L80s who already had one — so the trainer conversation and the rank-up rule are not built, and
  `.tradeskill` stands in for them.

- `Header1` / `MidFlag` / `Tail` / `TrailingFlag` are stored faithfully but their gameplay meaning is not yet
  decoded (they reproduce the wire exactly regardless). The ref-id list is decoded — it is the feat requirement.

### ⭐ `ProfessionId` — the column that used to be called `RdbType` (#148)

It takes two values, and they are the two shipped crafting professions. Resolving each recipe's
`RequiredFeatId` through `FeatIdMaps.WireFeatId` names all twelve ladder ranks, six per value, with **zero
crossover** — and the `RequiredWorkshopFeatId` families split the same way:

| value | recipes | feats | workshop feats |
|---|---:|---|---|
| `0xF4241` 1000001 | 99 | `321000`/`321001`/`321100`/`321101`/`321200`/`321201` — **Weaponsmith** Novice…Master | `3282xx` |
| `0xF4242` 1000002 | 504 | `321300`/`321301`/`321400`/`321401`/`321500`/`321501` — **Armorsmith** Novice…Master | `3283xx` |

⚠️ **The old name was a misreading, and it is positively refuted rather than merely unsupported.** RDB type
`1000001` is the **playfield** type — its entries are `Davids Test Playplace`, `Conarch Village`,
`Spider_Caverns` — and type `1000002` does not exist in the client's RDB index at all. The mis-naming had also
escaped into the issue history as *"recipes imported from RDB 1000001/1000002"*; recipes are **capture-derived**
(`CaptureRecipeImporter` reads `TradeskillReplyIIR`) and the importer never opens the RDB.

⚠️ Two values is a cheap partition on its own. What makes this a decode is that the six-rank ladder **and** the
disjoint workshop-feat family fall on the same split, and that the alternative reading is disproved.

⭐ It also sharpens what the missing professions look like: the shipped set is Weaponsmith + Armorsmith because
that is what the one captured recipe book held, so an Alchemist's book would arrive carrying a **third**
`ProfessionId`. That is the concrete prediction the alchemy-quest gap (#148, quests 13141/13143/13145) waits on.
- `type=1` single-`RecipeLearnt` form + the `LearnRecipe` GameAction (`FUN_0112d750`) are not built yet.
