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
[u32 x4]
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
- **`Recipe` table** — **fully columnized** (#193): `Id` (= recipeId = header word 0), `Header1`, `RdbType`
  (`0xf4241`/`0xf4242`), `MidFlag` (0/1), `Tail`, `TrailingFlag` (0/1), and the opaque ref-id list as `RefId0`
  (always present) + `RefId1` (nullable; every captured record has 1 or 2). No `Data` blob.
- **Importer** `--only recipes` (`RecipesModule` / `CaptureRecipeImporter`) — scans captures for the reply
  sub-opcode, parses records into `Recipe` rows (columns) + `RecipeItem` children, upserts them. Seeded 603
  from the NecroL80 capture.
- **Handler** `Handle0X2000` → `SendCraftingRecipeBook`: on `TradeskillRequestIIR`, loads `Database.GetRecipes()`
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

## Open / follow-ups

- The catalog is served **whole** to every character (the one capture is an L80 necro's full book). Real
  per-character known-recipe filtering (which recipes a char has learned) needs per-char recipe data — none
  is captured yet. An empty catalog yields a valid empty list (book opens, no recipes).
- `Header1` / `RdbType` / `MidFlag` / `Tail` / `TrailingFlag` / the ref-id list are stored faithfully but their
  gameplay meaning is not yet decoded (they reproduce the wire exactly regardless).
- `type=1` single-`RecipeLearnt` form + the `LearnRecipe` GameAction (`FUN_0112d750`) are not built yet.
