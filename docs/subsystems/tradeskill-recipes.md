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
`CharacterInventory.CreatorName` — see [inventory.md](inventory.md).

Retail leaves ~5 s between the ack and the completion, a bar the client animates; we complete immediately.
Refusals are **silent**, because retail has no failure form here and a completion over a craft that did not happen
would close the client's window on an item it never received.

## Open / follow-ups

- ⚠️ **Professions and ranks are not modelled at all.** In retail a character must LEARN a tradeskill profession,
  which comes in ranks, and that is what gates the book — a fresh character has none. We serve the catalog
  **whole** to every character that asks, recipes carry no profession/tier attribution, and no character row
  records a learned profession (`CharacterSkills` is the combat scaffold, every row rank 0). So nothing stops a
  level-1 character crafting a master-tier recipe. Tracked in Gitea; deliberately not guessed at.

  What the client settles statically, short of a capture:
  - **Learning a recipe is a GameAction**, not an RPC — `53198` / `e_Type_GC_GameAction_LearnRecipe`, one
    argument at key 39 (`Faolan.Core.Rdb/GameActionTypes.cs`). So it is fired by RDB content (a trainer, a quest,
    an item), which is why the c2s table has a `CraftUntrain` and no `CraftTrain`.
  - **A craft has its own id space**: `CraftUntrain(int nCraftID)` and `DispatchCraftUntrainMessageBox(nCraftID)`,
    distinct from a recipe id.
  - **The group→recipe mapping is server-side data.** `DispatchRemoveRecipesByGroupClient` is named for a group
    but takes `i_cRecipeList` — an explicit list — so the client never sees a group id.
  - ⚠️ `TradeskillFlags` (stat `0xCE`) is **NOT** the character's learned set, despite the name: it rides NPCs
    (a *Dockworker* carries 99), while the player's own instance carries `Craftmanship` / `MaxCraftmanship`.
    Those two do move (0 → 1 and 0 → 2 → 3 in early play) and look like slots-used / slots-allowed, but an L80
    who actually crafts reads max 2, so nothing is built on them. [V0: what records a learned tradeskill]
- `Header1` / `RdbType` / `MidFlag` / `Tail` / `TrailingFlag` / the ref-id list are stored faithfully but their
  gameplay meaning is not yet decoded (they reproduce the wire exactly regardless).
- `type=1` single-`RecipeLearnt` form + the `LearnRecipe` GameAction (`FUN_0112d750`) are not built yet.
