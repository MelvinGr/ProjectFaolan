# Client RDB data extraction

How the client's resource database is laid out on disk, how a resource is located, fetched, decrypted and
unwrapped, and which game data it does and does not carry. The extraction pipeline itself is the module-based
importer ([`../tooling/importer.md`](../tooling/importer.md)); the per-type formats are documented in this folder
([`rdb-type-catalog.md`](rdb-type-catalog.md), [`gear-rendering.md`](gear-rendering.md),
[`skeleton-grammar.md`](skeleton-grammar.md), [`item-template-stats.md`](item-template-stats.md), …).

## Files

- `<AoC>/RDB/`: **27 × 1 GB `NN.rdbdata`** blobs (~27 GB) + `le.idx` + `RDBHashIndex.bin` (41 MB) +
  `RDBDiffIndex.bin` (1 MB).
- **`le.idx` (version 5) is the offset index.** `RDBHashIndex.bin` (magic `RDHI`, version 6, `0x000D5474` ≈ 873k
  entries) is only the patch **hash** index — per-resource md5s, no offsets — so there is no "v6 index" to reverse.
  The format is shared with The Secret World (same engine; the community `joakibj/tswrdb` docs and the original
  Project Faolan `rdbreader` tool both read it — `rdbreader` supports index version 5 (AoC) and 7 (TSW)).

## `le.idx` layout (v5, validated)

- Header 28 B: magic `IBDR`(4) + version(4)=**5** + MD5(16) + `NumEntries`(4) = 873,588.
- Index: `NumEntries × 8` = `(RDB Type u32, RDB Id u32)` pairs (195 distinct types).
- Detail: `NumEntries × 44` (v5 = 11×u32; v7 is 28 B). Fields: `unk0, offset, size, unk3..unk10`;
  **`.rdbdata` file number = `unk0 & 0xff`**, `offset`/`size` locate the entry. (Detail starts at `28 + N*8`.)
- Then a bundles section.

`RdbReader` (`Faolan.Core.Rdb/RdbReader.cs`) parses it (v5/v7) and exposes each `Entry` with its `Hash` and
`IsLocal`.

## A resource's bytes

**`.rdbdata` entry:** at `offset-16` there is a 16 B pre-header `(type, id, len, len+2)`; at `offset` the data
begins with wrapper `IOz1`(4) + uncompressedSize(4) + a **zlib** stream (`78 da`). Decompress `raw[8:]`; the result
starts with the 12 B RDB-Id-data header, then the type-specific payload. Some entries have no wrapper (the 12 B
header is the first thing); item templates are `RDBH`-wrapped (see encryption below).

Worked example: TextStrings entry (type 1030002, id 211656, file 8) → read 10402 B → `IOz1`/uncomp=28704 →
zlib-inflate to exactly 28704 B → `TDC2` string collection (*Flags, Health, Volume Mass, Attack Speed, Clan, Team*).

Most records are zlib-compressed, so a grep over the pack files does not find strings the client plainly ships;
search decompressed bodies (`--report rdb-text-scan`).

## `file == 0xFF`: CDN content, fetched by hash

A detail record whose file byte is `0xFF` is **not packed locally** — it is on-demand CDN content. **The le.idx
16-byte content hash IS the CDN path md5**: `<gameupdate host>/upm/rdb/res/<h[0:2]>/<h[2:3]>/<h[3:]>` returns the
same `IOz1`+zlib blob as the local store would (byte sizes match le.idx). See
[`cdn-infrastructure.md`](cdn-infrastructure.md).

Every rdb-* module and every `--report` fetches through the `Faolan.Web.Content` mirror proxy (`--rdb-proxy`, just
the mirror's address; `RdbReader` appends the fixed `/gameupdate/upm/rdb` route), which store-and-forwards so pulls
are archived.

**Never gate a read on `Entry.IsLocal`, and never build a bare `new RdbReader(dir)`.** `IsLocal` reads as "skip the
unreadable ones" and means "skip everything not packed in `NN.rdbdata`"; a reader built without a mirror base makes
`CanRead` collapse into exactly that. Neither fails, warns or moves a row count — the import just quietly covers
less of the game, and for several types that is MOST of it:

| type | local | CDN-only |
|---|---:|---:|
| 1010001 RENDERMESH | 51,183 | 44,106 |
| 1010013 in-game map tiles | 73 | 580 |
| 1010506 NPC spawn layouts | 240 | 577 |
| 1070003 tilemaps | 244 | 276 |
| 1000053 bounded areas | 204 | 274 |
| 1010004 / 1066606 textures | 24,248 / 1,531 | 11,051 / 4,649 |

The correct gate is `rdb.CanRead(entry)` plus an **empty-body guard** — a CDN miss returns empty rather than
throwing. `IsLocal` has one legitimate use: REPORTING where a resource physically lives. Both rules are gated by
`RdbCdnGateTests`.

## Encryption (`RDBH`): TEA, keyed per template by the server

**Only `1000020` (item/feat templates) and `1000620` (recipe templates) carry `RDBH` resources**; no other type
(models, text, playfields, treasure, combos, …) is ever encrypted. TSW, same engine, encrypts nothing — `RDBH` is an
AoC-only protection applied to the item database.

- **Container:** `"RDBH"`(4) + `version u32 = 1` + `flags u32 = 1` + `plainLen u32` (unpadded plaintext length) +
  `reserved u32 = 0`, then the ciphertext = `entrySize - 20` bytes (always a multiple of 8).
- **Cipher = standard TEA, ECB.** Decrypt routine at exe va **`0xa8f550`**: 32 rounds, sum starts `0xc6ef3720`, step
  `-0x9e3779b9`. Key = 4 LE u32 read at `[ctx+0]..[ctx+0xc]`; standard TEA decrypt order
  (`v1 -= ((v0<<4)+k2)^(v0+sum)^((v0>>5)+k3); v0 -= ((v1<<4)+k0)^(v1+sum)^((v1>>5)+k1)`). Same TEA family as login,
  a separate use. No 8-byte ciphertext block repeats across 5,537 items / 129,371 blocks — not because the mode is
  CBC, but because **each template has its own key**.
- **Call chain:** `RDBServerSource::Decrypt` (vtable `0x203655c`, **slot 5** = va `0x1064d40`) → `0x106fcb0` (reads
  the `RDBH` magic `cmp eax,0x48424452`, dispatches on version/flags) → `0x106fef0` → TEA `0xa8f550`. The MD5 (`0x67452301…`
  init consts) nearby is the cache-integrity check, not the key. Gate at va `0x8cb40d`: only types `1000020` and
  `1000620` are decrypted; `Template item %u:%u (type %u) was used without a decryption key` is the missing-key
  warning.
- **The key is server-issued, per template** — a `DecryptionKey_c` delivered over the network as the protobuf
  `ProtoDecryptionKey`. It is the per-item quad the server sends in inventory
  (`Faolan.Core/Protobuf/aoc-client-protos.proto`):

  ```
  ProtoDecryptionKey { fixed32 key1; key2; key3; key4 }          // 4 LE u32 = the 128-bit TEA key
  ProtoAcgItem { ProtoDecryptionKey key1,key2,key3; fixed32 template1,template2,template3; uint32 level; fixed32 hash }
  ProtoInventoryGameEntry { ... ProtoAcgItem item_data = 3; ... } // inventory entries embed the keys
  ```

  stored as `Items.Key1..4`. `AocProtocolDecodeTests` decodes a real captured inventory entry with the generated
  classes (`Faolan.Core/Protobuf/AocProtocol.g.cs`) and asserts the decoded key and template id.

**Decryption recipe:** RDBH blob → ciphertext = `entrySize-20` → **TEA-ECB** with key `(Key1..Key4)` as 4 LE u32 →
truncate to `plainLen` → `IOz1` + `uncompressedSize` + zlib → inflate → template (`12B RDB-Id hdr:
type,id,marker=1000000` + fields). Example: Daggamalt `type 1000020 id 180450`, key
`807024719, 3628954689, 2634994156, 3227556839`, decrypts to a valid 329-byte template. A key only exists for a
template the server has sent someone in the corpus; the key sources are listed under `feat-keys` in
[`../tooling/importer.md`](../tooling/importer.md).

**Implementation:** `Faolan.Core/Crypt/Tea.cs`; `RdbReader.ReadDecrypted(entry, teaKey)` detects `RDBH`,
TEA-ECB-decrypts, truncates to `plainLen` and unwraps the inner `IOz1`/zlib; `RdbItemImporter` (`--only rdb-items`,
optional `--dump-templates <dir>`) decrypts every keyed item and validates the RDB-Id header (a DB id that collides
with a *plain* TESTITEM entry is correctly rejected).

### Template string fields

**Strings** are `tag 0x21 (u32) + len (u16) + flags (u16) + ASCII`, unaligned. The length is a **u16**, not a u32 —
the next u16 is a flags word that is **nonzero whenever the string is backslash-escaped** (e.g. an apostrophe `\'`).
Reading the length as a u32 folds that flags word into the high bytes (e.g. `0x0018000f` ≈ 1.5M), which a sane
length guard then rejects, silently dropping every escaped name. Read len as u16, skip the flags word, unescape
`\'`/`\"`/`\\`. The first underscore-free such string is the display name (internal mesh/asset names contain `_`).
The importer fills `Item.Name` only when empty (captured names stay authoritative); the blank remainder are
NPC/TEST/internal items whose only string is an asset-mesh ref. Guarded by `RdbItemNameTests`.

The template's stat fields are decoded in [`item-template-stats.md`](item-template-stats.md). The client's own
parser is lazy — `RDBItemData_c` (creator `0x4e29f0` → worker `0x4e8600`, vtable `0x1fdd11c`, buffer at `+0x88`)
reads only the 12-byte header, and consumers read fields on demand from the stream (read-u32 primitive `0x48f900`) —
so there is no single deserialize to trace.

## What the RDB does NOT carry

- **NPC combat stats.** MONSTERDATA (1010201, 3,185 entries) is plain XML (12-byte header `type,id,1000002`, then
  `<?xml …>`) describing the monster **model**: `<monsterdata animsys= skeleton= name= movementset= …>` with
  `<mesh>`, `<previewset>`, `<soundscript>`, `<fxscript>`. No HP / level / armor / damage, and no XML type carries a
  creature stat table. NPC HP comes from the captured spawn (`CharacterStats.health`).
- **Loot tables.** `TREASUREDATA` (1000083, 1,018 per-playfield XML records) is treasure-container PLACEMENT only
  (position/level/name/mesh/`ScryScriptID`), with no item contents. Retail loot was server-script-driven, so loot
  pools come from captured bag contents (`map-loot`).
- **Conversation trees.** The RDB has dialogue TEXT (TextStrings 1030002, TDC2, by string id) and client-side
  SCRIPTS (LuaArchive 1010223: Lua with quest-state tag conditions such as
  `if player:HasTag(3104) then return false else return true end ... player:AddTag(2742)`), but no
  conversation/quest/knubot/dialog DEFINITION type: which node runs which script, which option leads where, and
  which quest has which goals were server-authoritative content. The LuaArchive scripts are unlinked to captured
  dialogue (~59% are one-line `player:AddTag(n)`, the rest mostly shop/PvP/event content). Conversations are built
  from captures instead ([`../subsystems/knubot-conversation-packets.md`](../subsystems/knubot-conversation-packets.md));
  their quest-state gates are the structured columns `ConversationNode.ConditionQuestId` + `ConditionQuestState`
  (`HasCompletedQuest(X)` == `(X, Completed)`, `HasQuest(X) and not HasCompletedQuest(X)` == `(X, Active)`). The
  `CharacterTag` store (the `player:HasTag/AddTag` flag mechanism) exists for tag-gated content.

Text is not bulk-stored in `faolan.db`: the server references strings by id + category and the client renders them
from its own RDB, so the DB holds only the structured data the server cannot get from the client plus the ids.

## Types the importer reads

- **Map names + ids (1070003, `PLAYFIELDDESCRIPTIONDATA`)** — XML `<playfielddescriptiondata>` with
  `<playfield Name=".." ID=".."/>` (+ `ShowInGUI`); the **RDB record id == playfield id == our `Map.Id`** (4010
  Tortage Island, 2040 Castle of King Conan, 3000 Khemi, 1000 Conarch Village, ...). `rdb-maps`
  (`RdbMapImporter`) creates reference rows for unknown maps and renames existing ones; it touches only `Maps`.
- **Resurrection points (1000090, `RESURRECTIONPOINTSEXPORT`)** — XML `<resurrectionpoint ...><position x y z>`;
  record id == playfield/map id; `discoverable="false"` ⇒ AlwaysKnown; `RestorationLevel` (a percent) and
  `Orientation` (yaw radians) are imported too. `rdb-respawns` (`RdbRespawnImporter`) upserts `RespawnPoints` keyed
  on the RDB `Guid` (538 points across 248 maps; beach 4010 = 3 real points). Most records are local (240 local /
  784 CDN). The XML's `name` is the world-EDITOR object label, not a display name, and is not unique (95 points are
  "ResurrectionAIPoint0"); no display-name/text-id attribute exists on the element.
- The rest are listed per module in [`../tooling/importer.md`](../tooling/importer.md).

## Notes

- This reads the **user's own legally-installed client** for a private preservation/interop emulator. Client
  assets are not redistributed; only derived game-data rows land in the local DB.
- Ground truth to validate an RDB parser against: captured item identities, the lvl-23 character-sheet screenshots
  (`docs/subsystems/player-stats.md`), and the captured conversation nodes.
