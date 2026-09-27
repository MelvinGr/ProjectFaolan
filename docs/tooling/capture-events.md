# Capture event → packet survey

> For a chronological, id-resolved account of a SINGLE capture (login handoff, quest chain, dialogue,
> combat, loot, chat, plus what our catalogs cannot name), run
> `dotnet run --project Faolan.Importer -- --report timeline --report-arg <capture>.tshark.json`
> (or `./run.sh --timeline <capture>.tshark.json`) — see the `capture-timeline` skill. This page is the
> cross-corpus event→sub-opcode map instead.

The capture file names describe the events performed while recording. This maps each named event to the packets
that carry it, found by per-capture sub-opcode profiling against a plain login baseline and by extracting the
human-readable `remoteformat`/`localized` XML + object/NPC name strings (with the carrying sub-opcode). Offsets and
ids here are observations; the codecs and `docs/subsystems/` pages are the verified reference.

## IIR sub-opcode legend (GameServer `0x2000` carrier)

Names are the client's own, BKDR-31-verified in [`gen/iir-types.md`](../protocol/gen/iir-types.md).

| sub-op | IIR | what it carries in these captures |
|--------|-----|-----------------------------------|
| `5D85BFC7` | `PlayfieldFIIR` | the map change (map id, spawn pos as big-endian floats, e.g. 995.3/115.2/624.2) |
| `FA015807` | `SimpleCharFullUpdateIIR` | a character spawn (NPC/player), with its name, e.g. "Trader", "Inexperienced Nemedian" |
| `96C46740` | `MoveTaskIIR` | an entity's movement task (sent alongside its spawn) |
| `66AEDD50` | `SimpleDynelFullUpdateIIR` | a world object — "Totem Destruction", "loot_chest_04 [QRON]" |
| `F096251E` | `LootBagFullUpdateIIR` | a loot bag / chest — "Bag of loot" |
| `737A6DF9` | `FullPlayerCharacterIIR` | the player's full character snapshot |
| `4A3D899A` | `KnubotAppendTextIIR` | an NPC conversation's text (category 50002) |
| `F164A4A5` | `KnubotAnswerListIIR` | an NPC conversation's answer list (`remoteformat`, category 50001) |
| `642CD3D6` | `BehaviorControlCenterIIR` | an action — talk to NPC `0x16`, move item, open loot bag, ... |
| `6B87E316` | `MagicBuffIIR` | a spell/buff apply (caster, spell id, target) |
| `96B8DC59` | `StatUpdateIIR` | an entity stat update: `0000C350` + handle + `00000000` + count + N×(u32 statId, u32 value) — the combat HP/Stamina/Mana packet (see `docs/re/quests-and-combat.md`) |
| `4517AA81` | `CharDCMoveIIR` | a player's movement (big-endian floats); dominant during gameplay |
| `A36D3B74` | `ScryScriptIIR` | a client script call — the create editor's callbacks, and "QuestRewards" at a quest turn-in |
| `04B82B2D` | `N3ToClientQuitIIR` | an entity despawn |
| `D751EBB4` | `StopIIR` | an entity stops moving |
| `FEC4158A` | `DamageResultIIR` | a combat hit |

The `0x2NNN` RPC methods seen beside them (the spawn poll `0x2020` → `0x2008` SpawnCheck, chat, the create editor's
setup family, ...) are named per interface in [`gen/opcodes.md`](../protocol/gen/opcodes.md); a wire rendering alone
does not identify a method without its receiver class, so resolve one through the timeline report rather than by
its hex. `0802E5D4` / `08006F31` / `0131xxxx` are per-session handle ids, not opcodes.

## Per-capture events

### hoxxling — guild_invite_accepted, gameplay, other players visible
`...guild_invite_accepted-random-gameplay-other_playersvisible.json`
- **Guild chat / broadcast**: guild text ("Please put in guild bank", "Full Info on ALL items/quests"). The chat
  protocol — ChatServer channels + local say — is in `docs/subsystems/chat.md`.
- **Loot**: "Loot Goblins"; `LootBagFullUpdateIIR` "Bag of loot"; `BehaviorControlCenterIIR` "lootbag_open" (open a loot bag).
- **Other players**: many `FA015807` spawns + `4517AA81` moves (678) - other player entities streaming in.
- The guild-invite accept is a CSPlayerAgent call (`AcceptGuildInvitation`), not an IIR; see
  `docs/subsystems/guilds.md`.

### NecroL80 — changemap + accept_quest
`...login-changemap-accept_quest.json`
- **Map change**: two `5D85BFC7` map changes.
- **Quest accept dialogue**: `4A3D899A` nodes 30031/30033/30039/30040/30041/30045 + `43131/43013/43102/43104`
  with `F164A4A5` remoteformats (18940..18951, 27108/27109) - the quest-giver conversation branches.
- **Quest titles**: "Kill or be Killed", "Six Man Guild...5dead".
- Loot chest: `0xF096251E` "loot_chest_04 [QRON]".

### necrol80/assassinl23 — auctionhouse, sell to trader, get mail, totem of origin
`...sell_items_to_trader-intract-with-auctionhouse.json`, `...autionhouse_get_mail-use_totem_of_origin...json`
- **Mail/auction**: a `remoteformat token="TradePostGUI_MailItemBought" category="629"` (+ `<parameter value="7;AssassinL23"/>`) - the trade-post/mail UI updates are TOKEN-based remoteformats (not numeric-id), category 629.
- **Trader NPC**: `FA015807` "Trader".
- **Totem of origin** (teleport item → long load): triggers a MapChange + full world burst (the "long loading"); the use itself is a `BehaviorControlCenterIIR` on the totem item.

### AssassinL23 — resurect
`...randomActions-resurect.json`
- `BehaviorControlCenterIIR` dominates (3632) - the death/resurrect interactions + the resurrect confirm are actions.
- `FA015807` "Inexperienced Nemedian" (the mob).
- **Resurrection:** death → server sends `DispatchValidResurrectionSpots`
  (s2c `0x2024`, GameCharInterface):
  `[refPos 3 floats][u32 (count+1)*0x3F1][ spot(41B): pos 3 floats + spotType + mapId + 0 0 0 + spawnPointId +
  restorationLevel + 0x00 ]*`, ordered nearest-first from refPos (idx616/idx4050, 57 B, both deaths identical bar
  the death pos). The `0x7E2` is the list count and the `0x0A` the point's `RestorationLevel`, and the `0x00` is
  per-record — all three look constant in these two captures only because both carry exactly ONE spot. See
  `docs/subsystems/combat.md`. The client shows the release screen, the player
  picks → `RespawnMe` (c2s `0x2024`) = `[u32 spawnPointId]` (the chosen spot echoed back). The same s2c message is
  sent at LOGIN to set the spawn anchor - it only renders the release UI when
  the client is in the dead state (which needs a real combat killing-blow, not a raw health=0 push). Codec
  `Faolan.Core.Network/Codecs/Combat/ValidResurrectionSpotsCodec.cs`, golden test
  `Faolan.Tests/DispatchValidResurrectionSpotsTests.cs`.

### NecroL80 — the tombstone and its map pin

`NecroL80_mage-necromancer_login_gameplay_chat-annotated-4`, plus five more captures that carry the add without the
remove. `--report method-bodies --report-arg WayPoint` and `--report entity-classes --report-arg TombStone` are the
oracles.

**The pin is planted on respawn, not on death.** The whole sequence sits inside 0.7 s, in this order:

| t | | |
|---|---|---|
| 1223.2 | c2s | `RespawnMe` — the player picks a resurrection spot |
| 1223.4 | s2c | `BehaviorControlCenterIIR` — the player, `Revive` |
| 1223.9 | s2c | `TombStoneFullUpdateIIR` — spawns handle `0x58` **at the place they died** (200.2, 53.7, 47.3) |
| 1223.9 | s2c | `DispatchAddWayPoint` — a pin on that same handle, same position, playfield 1092 |
| 1223.9 | s2c | `MagicBuffIIR` — `Shield of the Risen` |
| 1223.9 | s2c | `IntendedTeleportIIR` + `N3TeleportIIR` — away to the resurrection point |
| 1223.9 | s2c | `StatUpdateIIR` — vitals restored |

So the corpse stays where you fell and the pin is how you find it again — which is exactly what the pin's own LDB
tooltip says: *"Locate your Tombstone to reduce death penalty!"*.

**What a tombstone record carries**: `EntityClass.TombStone` (51323), a name of literally `"Tombstone"` — except
one record that carries the dead character's own gear mesh name
(`NPC_Global_halfplate_heavy_bronze_head_tattered_(A1182/A1188)`), so the corpse can render as the body — a
`remoteformat` nameplate in category 50200, and a tail ending `0000C350 <ownerHandle>`: the **owner's** character id
under `EntityClass.Character`.

**The one captured `DispatchRemoveWayPoint` is a zone change, not a recovery.** Twenty seconds after the pin goes
up, the player uses a zone-line dynel, answers *"Do you wish to leave? You will be unable to return to this
instance."*, teleports to playfield 1090 and is redirected to another game server — and the pin for playfield 1092
comes down 0.6 s later. That is why `i_nPlayfieldID` is in the remove body at all: **the pin is per-playfield**, and
leaving the playfield drops it.

**No capture performs a tombstone recovery**, so what the pickup sends is not known.

### The pin's icon is not in the message

All six captured `DispatchAddWayPoint` frames are tombstones in every field at once — `i_cID`'s class is
`EntityClass.TombStone`, `i_nLabel` is *"Tombstone"*, `i_nToolTip` is the death-penalty line — so the corpus
cannot say which one picks the map icon. **The client says neither.**

The receive chain is `GameCharInterfaceDispatch_t::DispatchAddWayPoint` (`FUN_004c9860`, which reads exactly the
nine u32s the codec builds — an independent confirmation of the 36-byte grammar) → vtable `+0x1ec` →
`FUN_0139c1f0` (`GamecodeClient_t`) → `FUN_01398160` → `FUN_013a18f0` (`GUI\MapWaypointHandler.cpp`) →
`FUN_0139f520`, the add. In it:

- **`i_nLabel` is the pin's TITLE TEXT and `i_nToolTip` its TOOLTIP TEXT.** Each goes through
  `FUN_0060f190(100, id, 0)` — the LDB text resolver, category 100, the same call shape that resolves a respawn
  point's name out of category 270 — and the two resulting *strings* are what reach the add. A text lookup cannot
  select a bitmap.
- **`i_cID` never reaches the add.** `FUN_0139f520` is `RET 0x20` = exactly eight stack arguments, and all eight
  are accounted for at the call site: (handler, `i_cPos`, `i_vRadius`, title, tooltip, const, record, const).
  `i_cID` is the marker key, which is what `DispatchRemoveWayPoint` addresses.

The look comes from a waypoint **TYPE** the client keeps in that record struct, and the server does not send one:
`FUN_013a18f0` explicitly zeroes the record's three words, so every server-planted pin gets the same default. The
client's own creators do set it — `FUN_013a2f80` writes **7** for a resurrection point and **0x1C** for a murder
resurrection point, clearing those two types first — and there is a named enum behind it (`ParseWPType`,
`FUN_0139e6f0`: names include `destination`, `dungeon`, `custom`; the inverse `FUN_0139e8d0` returns strings for
8, 9, 0x0D, 0x15, 0x16 and defaults to `custom`), plus per-playfield definitions in
`Gui/Default/Waypoints/PF%u.xml` carrying `tooltip_title`, `tooltip_text`, `min_level`, `max_level`,
`tooltip_image`, `normal_bitmap`, `inactive_bitmap`, `dir_arrow`, `dir_arrow_inactive`.

⇒ `.waypoint` takes optional `<labelId> <tooltipId>` arguments, because the TEXT is the only thing about the pin
this message can vary. A non-tombstone icon needs a different mechanism, not a different field.

### Blablabla — create, combat, levelup, questcomplete
`...create-random-actions-combat-levelup-questcomplete.json`
- **Quest dialogue tree** (the beach Casilda/combat NPC): `4A3D899A` 12493→12490→20018→20019→20020→19919→
  26909→12492→12494 with `F164A4A5` remoteformats 7615/12621/12622/12623/13444/12548/7617 (the tree
  `ConversationService` serves).
- **Quest rewards / turn-in**: `0xA36D3B74` "QuestRewards".
- **Combat**: `MagicBuffIIR` applies + `CharDCMoveIIR` moves; hits are `DamageResultIIR` and health changes
  `StatUpdateIIR`. The level-up is `NewLevelIIR`.

### hoxxling — create (all sliders), long load, chat before exit
`...create-modified_all_sliders-very_long_loading_screen-chat_message_before_exit.json`
- Full create-editor stream: `ScryScriptIIR` script callbacks (77), the editor's RPC setup calls, `MoveTaskIIR`.
  The editor `0x2000` IIR carrier stream is decoded in `docs/subsystems/create-editor-bursts.md` (dissector
  `CreateEditorRecordTests`): it multiplexes by endpoint-class pair, and the entity full-updates
  reuse the login/world-entry spawn grammar.
- **Chat before exit**: a chat send near the end (see `docs/subsystems/chat.md`).
