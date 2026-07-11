# Crafting recipe book — TradeskillRequestIIR / TradeskillReplyIIR (decoded 2026-07-06)

Opening the in-client **Crafting → Recipes** window is a request/response on the GameServer connection,
decoded from `Captures/NecroL80_mage-necromancer_shops_open-crrafting-receipies.tshark.json` (frame 2916 →
2926) + Ghidra (`AgeOfConan.exe`, project `Captures/ghidra/output`).

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
(`TradeskillRecipeCodecTests`). Each 16-byte block inside the ingredient/result elements is an item's TEA
`DecryptionKey` — the keys `RequestRecipeInfo` (0x2090) later references to fetch a recipe's item details.

## Implementation

- **`TradeskillRecipeCodec`** (`Faolan.Core.Network`): `ParseReplyBody` (→ `RecipeRecord[]`), `RecordLength`
  (the 0x3F1-aware record walker), `BuildReplyBody` (env/version/type/count + records). Byte-exact golden vs
  the capture.
- **`Recipe` table** (`Id` = recipeId, `Data` = verbatim record body after the id). The inner
  ingredient/result fields stay verbatim in `Data` (decoded-framing interim, like the item templates);
  splitting them into child tables is the follow-up.
- **Importer** `--only recipes` (`RecipesModule` / `CaptureRecipeImporter`) — scans captures for the reply
  sub-opcode, parses records, upserts `Recipe` rows. Seeded 603 from the NecroL80 capture.
- **Handler** `Handle0X2000` → `SendCraftingRecipeBook`: on `TradeskillRequestIIR`, builds the reply from
  `Database.GetRecipes()` (ordered by id = capture order) and sends it on the world framing.

## Open / follow-ups

- The catalog is served **whole** to every character (the one capture is an L80 necro's full book). Real
  per-character known-recipe filtering (which recipes a char has learned) needs per-char recipe data — none
  is captured yet. An empty catalog yields a valid empty list (book opens, no recipes).
- ~~Normalise the ingredient/result sub-lists into child tables so recipes are queryable.~~ **DONE:**
  `RecipeItem` (Role ingredient/result, Position, ItemId, Flag, Trailing) via
  `TradeskillRecipeCodec.ParseRecordItems`; the importer seeds it (2476 rows: 1873 ingredients + 603 results).
  `ItemId` is a hard FK to `Items` (`Restrict`): the referenced item's per-template TEA key and hash live once in
  `Items` (the key `RequestRecipeInfo` 0x2090 uses), harvested there from the recipe element by the importer, so
  they're **not** duplicated on `RecipeItem`. `Recipe.Data` stays the byte-exact wire source; the child rows are
  the queryable projection. The element layout is fixed 18 u32 (ingredient) / 17 u32 (result):
  `[0]=[1]=itemId, [2]=0, [3]=Flag, [4]=Hash, [5..8]=[9..12]=key, [13..16]=const, [17]=Trailing`.
- `type=1` single-`RecipeLearnt` form + the `LearnRecipe` GameAction (`FUN_0112d750`) are not built yet.
