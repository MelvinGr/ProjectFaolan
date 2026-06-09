# Plan: recover real opcode / RPC method names from the client

**Short answer: yes, this is very feasible** — more so than the .proto recovery was. The client's entire RPC
layer is statically linked into `AgeOfConan.exe` with its C++ symbol names intact (RTTI + log strings), so the
*names* are essentially free; the only real work is mapping each recovered name to the numeric id we see on the
wire. This plan stages that work, cheapest-and-highest-value first.

Same exe as the .proto / login / DH work:
`/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe` (use this one — it's the
client the logs come from; `AgeOfConanDX10.exe` is the DX10 twin). Reuse the existing RE venv `/tmp/re_env`
(`pefile`, `protobuf`) and the `/tmp/proto_dump.py` precedent.

## What the binary already gives us (feasibility evidence)

Probed 2026-06-09 with `strings`/`python` over `AgeOfConan.exe`:

1. **Method names + signatures are present** — 1194 `<Class>::Method(...)` log strings, of two kinds:
   - **client→server**: `GameCharAgentProxy_t::AddFavoriteItem(uint32 saleID) streamed payload of size ...` —
     the proxy classes log every outbound call with its **full C++ signature** (param names + types).
   - **server→client**: `GameCharInterfaceDispatch_t::DispatchLearnSpell() ... stream bad after 'i_nSpellID'` —
     the dispatch classes log each inbound method and **each field as it is read** (so we get param names *in
     order*, which is the wire layout). Examples already spotted that we care about:
     `DispatchLearnSpell(i_nSpellID)`, `DispatchForgetSpell`, `DispatchAddSpecialAbility`,
     `DispatchSetDialogueCamera(i_cTargetID,i_cOther,nShotSize,nShotType,nShotEmphasis,nShotFov,nShotTilt,
     bAutoface,i_vHeadPaddingY)`, `DispatchSetQuestMarker(i_cNPC,i_eMarker)`,
     `DispatchChangeItemStackSize(i_cInventoryID,i_nItemPos,i_nValue)`, `DispatchUpdateCash`, `DispatchAddTag`.
2. **MSVC RTTI class names** enumerate the interfaces: `.?AVGameCharInterfaceDispatch_t@GameCodeEndpoints_n@@`,
   `.?AVGameCharAgentProxy_t@GameCodeEndpoints_n@@`, `TradepostInterfaceDispatch_t`, … The pattern is
   `<Iface>Agent[Project]Proxy_t` (client→server send side) vs `<Iface>[Project]Dispatch_t` / `<Iface>_i`
   (server→client receive side). Source-path strings confirm it (`Endpoints\GameCharAgentProxy.cpp`).
3. **The numeric ids are NOT stored as literals** — every known id (the 6 `ProtoEndpoint` type ids like
   `GamePlayerType 0x6C67C147`, and the 32-bit sub-opcodes like `ApplySpell 0x6B87E316`, `Move 0x4517AA81`,
   `0x96B8DC59`, `0x737A6DF9`) occurs **0 times** as a LE or BE 4-byte literal in the exe. Conclusion: they are
   **computed at runtime as string hashes of the class/method names** (consistent with how `ProtoEndpoint.type` =
   "object-class id" already behaves). So id↔name = *recover the hash function and apply it to the names*.
4. **It's all in one binary** — 764 of the 768 `Dispatch_t::`/`Endpoints_n` strings are in `AgeOfConan.exe` (the
   rest in libeay32, irrelevant). No separate engine DLL to chase; the hash routine is in the exe too.

### Two distinct id systems (don't conflate them)

The names in our `Faolan.Core/Network/Packets/Opcodes.cs` (e.g. `ApplySpell`, `MapChange`) are **our** invented
labels, not the client's — they won't be found as strings. On the wire there are three numeric namespaces:

| Layer | Example | Likely client meaning |
|---|---|---|
| 16-bit RpcHeader opcode | `0x2052`, `0x2008`, `0x200B` | per-interface **method index** in that interface's `DispatchMethod` switch |
| 32-bit sub-opcode inside `0x2000` | `0x6B87E316`, `0x96B8DC59`, `0x737A6DF9` | **hash of an IIR/message type name** (the dynel-update / IIR channel) |
| `ProtoEndpoint.type` (fixed32) | `0x6C67C147`, `0xB171CE13` | **hash of the endpoint/object-class name** |

The 32-bit and endpoint ids are the hash track (Stage 2/3a). The 16-bit opcodes are the dispatch-table track
(Stage 3b).

## Stages

### Stage 1 — Name & signature catalog ✅ DONE (2026-06-09)
Extracted by `Captures/extract_rpc_catalog.py <AgeOfConan.exe>` → **`docs/aoc-client-rpc-methods.md`** (+ `.json`).
**14 interfaces, 376 client→server (proxy) + 401 server→client (dispatch) methods.** The interfaces map 1:1 onto
our services: `UniverseAgent/Interface`, `PlayerAgent/Interface`, `CSPlayerAgent/Interface`, `GameCharAgent/
Interface` (the main game protocol, 169 c2s / 157 s2c), `GameAgent/Interface`, **`TradepostAgent/Interface`**
(the mail/auction/trade-post TODO). Client→server params are fully typed (from `(...) streamed payload`);
server→client params are the field NAMES in wire/read order (from the per-field `stream bad after '<field>'`
strings; types not in the string). Highlights already located in `GameCharInterface` (server→client = what we
emit): `DispatchLearnSpell(i_nSpellID)`, `DispatchForgetSpell`, `DispatchAddSpecialAbility(i_nAbilityID,i_cItem,
i_nType,i_nResourceID,i_bDoFeedback)`, `DispatchSetDialogueCamera(i_cTargetID,i_cOther,nShotSize,nShotType,
nShotEmphasis,nShotFov,nShotTilt,bAutoface,i_vHeadPaddingY)`, `DispatchSetQuestMarker(i_cNPC,i_eMarker)`,
`DispatchNextQuestAvailable`, `DispatchSetItemStat`, `DispatchPassiveAbilityEquip`.
This already answers "what does this Dispatch do" for the whole protocol, independent of the numeric ids below.

### Stage 2 — Recover the string-hash function ✅ DONE (2026-06-09)
**The hash is BKDR-31: `h = 0; for c in name: h = (h*31 + c) & 0xFFFFFFFF` over the ASCII class name** (the
classic BKDR / Java `String.hashCode`). The cheap brute (`/tmp/hash_brute2.py`: hash every exe token with a
battery of functions, count anchor hits) nailed it — **18/18 anchors**, and the recovered names are semantically
exact. No disassembly needed. Examples:
- endpoint type ids = hash(interface/agent name): `hash("GameCharInterface")=0x8B87F791`,
  `hash("GameCharAgent")=0xA9ECB95D`, `hash("GameInterface")=0x6C67C147`, `hash("GameAgent")=0xB171CE13`.
  → our `ConanEndpoints` labels were guesses; the real classes are GameInterface/GameAgent (player/world),
  GameCharInterface/GameCharAgent (spawn handshake), GameChar*Project (post-ready / server-id).
- 32-bit sub-opcodes = hash(IIR message-type name): `ApplySpell`→`MagicBuffIIR_c`, vitals→`StatUpdateIIR_c`,
  inv-snapshot→`FullPlayerCharacterIIR`, spawn→`SimpleCharFullUpdateIIR_t`, map-change→`PlayfieldFIIR_t`,
  Interact→`BehaviorControlCenterIIR_c`, Move→`CharDCMoveIIR_t`.

### Stage 3 — Build the id↔name tables
- **3a (32-bit + endpoint ids):** ✅ DONE — `Captures/decode_opcode_ids.py <exe> <Captures>` →
  **`docs/aoc-opcode-ids.md`**. Scans every capture across **all servers**, resolves all 14 endpoint type ids and
  ~72 IIR sub-opcodes by hashing every exe identifier. Cross-validated against independently-known opcodes
  (`SetMorphValueIIR_c`=morph ack, `SetNameIIR_t`=name commit, `n3TeleportIIR_t`=teleport, the `Knubot*IIR`
  dialogue family). Leftover `[?]` are client instance ids / handles in `0x2000` bodies (e.g. `0x02165EFF`=Strie),
  not sub-opcodes. Original 3a wording follows:
- **3a (32-bit + endpoint ids):** apply the recovered hash to every name from Stage 1 → full id table; verify
  against the sub-opcodes actually seen in captures (`0x6B87E316`, `0x96B8DC59`, `0x737A6DF9`, `0x4517AA81`,
  `0x642CD3D6`, `0xFA015807`, …). Each verified pair replaces an invented `Opcodes.cs` name.
- **3b (16-bit interface opcodes):** ⏸ ATTEMPTED, PARKED (2026-06-09). The per-interface method id (wire opcode,
  in `0x2000..0x20FF`) is **framework-assigned via the RPC registration order — NOT a static immediate, jump
  table, or hash** — so none of the cheap angles recovered it. Four tries (scripts in `/tmp`): (1) proxy-method
  immediate scan (`op16.py`) — the id is not loaded as a literal near the `*Proxy_t::Method` log string;
  (2) dispatcher string-ref (`op16_jt.py`) — the `DispatchMethod(` string sits in a logging branch, not at the
  switch; (3) structural `.rdata` jump-table scan (`op16_scan.py`) — no static pointer table (dispatch is
  registration-based); (4) hash-derivation test — BKDR-31 low bits don't match the known anchors
  (`CreateCharacter`=0x20DE, `EnterWorld`=0x20B6: near-misses are coincidence). To finish this needs tracing the
  RPC method-registration / vtable order (Ghidra full-analysis, or find the generated IDL/method-id header in the
  binary). **Deprioritized**: the dominant `0x2000` channel (the whole IIR protocol) is fully decoded in 3a, and
  the remaining 16-bit *control* opcodes are a small set already hand-identified for the ones we use
  (`SpawnCheck` 0x2008, `ReadyForPlayScreen` 0x2024, `Pong` 0x2009, the `0x201C` handshake, …). The per-server
  16-bit opcode inventory (counts, unnamed) is in `docs/aoc-opcode-ids.md` for when this is picked back up.

### Stage 4 — Integrate & validate ✅ DONE (2026-06-09)
- Real client names integrated: `GameServer0X2000RespondsOpcodes` is now the full IIR catalog (71 real names +
  5 descriptive aliases), `GameServer0X2000Opcodes` placeholders renamed, `ConanEndpoints` comments corrected.
  Build + 90 tests green. Kept the rule from [[client_protobuf_schema]]: **where names and the retail bytes
  disagree, the retail bytes win** — every mapping cross-checked against `Captures/*.json`.
- Immediate payoffs now unblocked by the names (carried into Stage 5 below):
  - `DispatchLearnSpell(i_nSpellID)` / `DispatchAddSpecialAbility` → the real **spell-grant** packet (TODO #3
    follow-up; the C++ `addSpell` 0x09 is the dead old scheme).
  - `DispatchSetDialogueCamera(...)` → the **cutscene camera** path (TODO #7, the create-world Kalanthes intro).
  - `DispatchSetQuestMarker` / quest dispatches → the quest-log/objective work.

## Stage 5 — Further client-extraction opportunities (backlog)

The same three tools — `strings`/RTTI, the **BKDR-31** name hash, and the `stream bad after '<field>'` parser
traces — yield much more than opcodes. Each item below is grounded in a probe of `AgeOfConan.exe` (2026-06-09)
and tied to an open TODO. Ranked by value.

1. **Exact wire layout of every raw dispatch method (highest value).** ✅ DONE (2026-06-09). `stream bad after
   '<field>'` is the client's own parser emitting each field *in read order*; the catalog
   (`docs/aoc-client-rpc-methods.md`, via `Captures/extract_rpc_catalog.py`) already lists every method's field
   names in order + the typed client→server signatures, and now a **Hungarian-prefix → wire-type legend** (`i_c`=
   ID32 handle, `i_n`=int, `b`=bool, `i_v`=vector, plural/`...List`/`...Data`=collection), verified against the
   proxy sigs. Confirmed examples: `DispatchSetItemStat(i_cInventoryID, i_nItemPos, i_nStat, i_nValue)`,
   `DispatchLearnSpell(i_nSpellID)`. Remaining: collection fields still need their nested element encoding (item 2).
2. **Reverse-hash decoder for name-hashed ids inside packet BODIES** — ✅ CLOSED: NO PRODUCTIVE TARGET REMAINS
   (probed 2026-06-09). `Captures/reverse_hash_ids.py` BKDR-31-matches 32-bit body words against every exe
   identifier, but a **blind** scan is too noisy: the hash over ~60k tokens collides with random 4-byte windows
   (lengths/floats/strings), so the non-opcode hits are mostly junk (`wTrL`, `PjES`, `ShadowMapType`). It only pays
   off **targeted at a field already located by packet structure** — and when we targeted BOTH named leads, **both
   turned out to be plaintext, not name-hashes**, so there was nothing to reverse-hash:
   - **TradePost/RemoteFormat tokens — NOT hashed (disproven 2026-06-09).** The trade-post/mail UI is plaintext
     `remoteformat` XML: `<remoteformat token="TradePostGUI_MailItemBought" category="629" key="..."><parameter
     value="..."/>...` carried in a `0x2002` GameServer packet (`[framing][u32 xmlLen][XML][tail handles]`). The
     `token`, `category`, and `<parameter>` values are all human-readable; the only binary 32-bit words that
     reverse-hash to an exe name are the **already-known** ProtoEndpoint framing ids (`TradepostInterface`
     0xF2C2D5B5, `TradepostAgent` 0x3D744981, the Game* ids) + the `BehaviorControlCenterIIR_c` Interact opcode —
     i.e. things already in `docs/aoc-opcode-ids.md`. No new hashed token ids exist in the bodies. (Also: only
     ONE category-629 token — `TradePostGUI_MailItemBought` — appears across ALL captures, so the mail/auction
     *implementation* TODO is itself blocked on fresh captures of the listing/buy/sell flow; it's a feature, not RE.)
   - **`OverrideGameTweaks` disconnect — ✅ DONE (2026-06-09), and it turned out NOT to be a reverse-hash case.**
     `DispatchOverrideGameTweaks(overrides)` (Player + GameChar interfaces); `overrides` is a collection of
     tweaks, but the tweak names are **plaintext length-prefixed strings on the wire**, NOT name-hashes — so no
     reverse-hashing was needed. Wire format decoded: `[u32 header 0x00004ED4][ per tweak: u16 nameLen, ASCII
     name, f64 BE value ] *`. The fix (40d4bc0) replayed retail's 630-byte body verbatim; now de-magicked into a
     named `(name, value)` tweak list in `PlayerAgentListener` (Ox20EC handler) emitted via `WriteString` +
     `WriteDoubleBigEndian`, guarded byte-for-byte by `GameTweaksTests`. Client applies all 19 tweaks; no disconnect.
3. **Pair the protobuf IIR descriptors to the Stage-3a ids.** ✅ INVESTIGATED (2026-06-09) — premise corrected.
   Re-ran the descriptor dump: the exe embeds exactly **16** `FileDescriptorProto`s (Util, CoreCharacterIIR,
   FullCharacterIIR, ContainerAddItemIIR, GameCodeDynelIIR, SimpleDynelIIR, LootBagIIR, ScryScriptIIR,
   QuestGiverList, CommandMessage, LooksPacket, LuaScriptList, LoreTreeBuf, GameTriggerBuf, CharacterTagTypeBuf,
   CollisionMaterial) — **already fully in the committed schema; there are no more to grab.** Crucially, **most
   IIRs are NOT protobuf** — the high-value combat/quest/morph/move ones (`StatUpdateIIR`, `MagicBuffIIR`,
   `DamageResultIIR`, `Knubot*IIR`, `SetMorphValueIIR`, `CharDCMoveIIR`, `FullPlayerCharacterIIR`) use **raw-stream**
   serialization, so the proto schema does NOT decode them (they need manual field-layout RE; some already done,
   e.g. `StatUpdateIIR` = the count-prefixed `[u32 statId][u32 value]` records in [[protocol_stats_and_combat]]).
   Only **3** descriptors map to a top-level wire sub-opcode (via BKDR-31), and these the analyzer CAN decode today
   with the existing `AocProtocol.g.cs` types:
   - `SimpleDynelIIR` → **0x66AEDD50** (`SimpleDynelFullUpdateIIR_c`, world-object spawn)
   - `LootBagIIR` → **0xF096251E** (`LootBagFullUpdateIIR_c`)
   - `ScryScriptIIR` → **0xA36D3B74** (`ScryScriptIIR_c`) — **directly useful for TODO #7** (the cutscene scry
     messages: `PlayOrSkip` / `ThisIsKalanthes` ride this; `ProtoScryScriptIIR` is already a typed C# class).
   The other 13 descriptors are sub-structures nested inside other messages (e.g. CoreCharacter/FullCharacter inside
   the char snapshot), not standalone wire ids.
   - **Applied to TODO #7 (2026-06-09):** `Captures/decode_scryscript.py` decodes the full create-world intro scry
     sequence (PreloadAssets+manifest → PlayOrSkip → StartScriptGroup → ThisIsKalanthes → WelcomeTutorial).
     Cross-checking our server showed we ALREADY replay the Slave Galley `PreloadAssets` + manifest verbatim, and
     the client's failing resource ids are playfield *child* lookups (local RDB), not server-sent — which
     reinforces the client-side-RDB diagnosis. See the TODO #7 pin for the one remaining server-side check (the
     later Tortage Beach scry chunk).
4. **Stat / attribute id table** (was: wrong stat ids in `CapturedCharacterStats.cs`) — ✅ VITALS DONE FROM
   CAPTURES (2026-06-09), no disasm needed; attributes still open. The name↔**id** pairing isn't in the string
   table (ids are small enums), BUT the vitals current/max ids fell out of the `0x96B8DC59` packet directly: the
   6-record updates carry a (current, max) pair per Health/Stamina/Mana, and the `current <= max` invariant holds
   100% across the captures, so the larger-valued id of each pair is the max. Result (overturns the old swapped
   labels): Health cur=`0x0001`/max=`0x001B`, Stamina cur=`0x01F9`/max=`0x01F8`, Mana cur=`0x01FB`/max=`0x01FA`.
   Fixed in `LegacyCharacterStatId` + `EntityVitals.ToVitalsBlock` (now emits both ids per vital — the old code
   sent the current value under the max id, so damaged entities reported `max == currentHP`). See
   `docs/player-stats.md`. **Still open:** the **attributes** (`Strength`/`Constitution`/`Dexterity`/`Intelligence`/
   `Wisdom`) — probed earlier as NOT plain stat records (likely client-computed base+gear+buffs); recovering those
   would need the RDB stat DataTable or disasm of the enum registration.
5. **Appearance morphs** — ⚠ BINARY ANGLE DEAD, partial fix landed (2026-06-09). The premise (recover the morph
   names/slot enum from the binary) is **wrong**: the morph names are **not in `AgeOfConan.exe` at all**
   (`BrowLow`/`CheekForward`/`NoseWide`/… = 0 hits) — they're RDB-defined, like the gametweak/tradepost tokens.
   BUT the editor's `SetMorphValue` callbacks (client→server) already name each morph in plaintext AND **embed the
   slot id in the name** (`02_BrowLow`, `33_NoseCrookedRight` → slot 2, 33), so captures + the `NN_` convention
   give us what we need without the binary. Captures show 15 morphs: 7 body sliders + `ears` + 7 slot-prefixed
   face sliders. **Fixed in `ApplyMorph`**: now maps `ears` + the 7 captured face morphs to their `CharacterBody`
   columns (was dropping them). **Still blocked for *rendering*:** `CharacterBody` is persisted but never read back
   into any packet — there is no looks/`SetMorphValueIIR` broadcast yet, and that IIR is raw-stream (not strings/
   protobuf, per #3), so building it needs packet-structure RE from captures, and the full slot→column map needs
   the RDB face-morph table (we only see the 7 morphs the test users happened to change).
6. **Cutscene / ScryScript decode** (TODO #7). `ScryScript` is Lua-bound (`ResourceScryscriptID`,
   `ScryScriptGUIView_c`). The scry message layout + valid camera-path / playfield-resource names should pinpoint
   the `invalid camera path` failures — possibly a missing field or resource id we can supply.
7. **The Lua `GameInterface` API as a Rosetta stone.** 101 `com.GameInterface.*` namespaces (FactionInterface,
   FeatWindows, ProgressBars, Targeting, …) + the UI Lua scripts in the RDB map "server packet → what the UI does",
   useful for verifying any of the above (quest tracker, feat windows, mail, target frames).
8. **Lower-but-cheap:** login/version constants (`territoryVersion` / `communityVersion` fields seen in the
   `Authenticate` proxy sigs); and using the validation strings themselves as a **build safety net** — emitting
   fields in the client's stated order guarantees packets it accepts (avoids `Dispatcher call ... failed`
   disconnects), which de-risks every new packet we craft.

Progress (2026-06-09): the genuinely **easy** parts are done — #1 (full field-order catalog + type legend), the
`reverse_hash_ids.py` tool for #2, and the **`OverrideGameTweaks` fix** (#2's named-tweak lead — done + de-magicked,
turned out to be plaintext not hashed; see above). #2 is now CLOSED with no productive target left (both its leads —
gametweaks and TradePost — proved to be plaintext-token protocols, not name-hashes). #4 (stat ids) and #5/#6 still
need more than `strings` (packet-structure decoding, RDB, or disasm). **Best next spike: #3** — re-run the
proto descriptor dump and pair each IIR message to its Stage-3a id; one pass makes the whole `0x2000` channel
(combat / quests / morphs / loot) typed-decodable, and is the prerequisite that makes #2's "targeted" body-id
naming actually usable.

## Risks / unknowns
- **Hash input ambiguity** (bare vs namespaced vs decorated) — mitigated by the multi-anchor brute in Stage 2.
- **16-bit opcodes may need real disasm** if they aren't declaration-ordered — Ghidra headless covers it.
- **Some 32-bit ids might hash a struct/layout signature, not a name** — captures stay the tiebreaker.
- This is read-only RE of a client we run locally for interop with our own emulator (same basis as the prior
  .proto / DH-login work); no client redistribution.

## Tooling
- `/tmp/re_env` venv (pefile, protobuf) + `/tmp/proto_dump.py` precedent; add `capstone`/`r2pipe` or Ghidra
  headless for Stages 2–3.
- Cross-check scripts live next to the captures (`Captures/*.py`); outputs land in `docs/`.
