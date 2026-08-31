# Capture event → packet survey

> For a chronological, id-resolved account of a SINGLE capture (login handoff, quest chain, dialogue,
> combat, loot, chat, plus what our catalogs cannot name), run
> `dotnet run --project Faolan.Importer -- --report timeline --report-arg <capture>.tshark.json`
> (or `./run.sh --timeline <capture>.tshark.json`) — see the `capture-timeline` skill. This page is the
> cross-corpus event→sub-opcode map instead.

The `Captures/*.json` filenames describe the events performed while recording. This maps each named event to the
packets that carry it, found by: per-capture sub-opcode profiling vs a plain login baseline, and extracting the
human-readable `remoteformat`/`localized` XML + object/NPC name strings (with the carrying sub-opcode). Generated
2026-06-08; offsets/ids are observations to verify, not all confirmed in-client.

## Sub-opcode legend (GameServer 0x2000 sub-opcodes seen)

| sub-op | meaning (observed) |
|--------|--------------------|
| `5D85BFC7` | MapChange (map id @ body+34, spawn pos @ body+13 — raw floats BIG-endian, e.g. 995.3/115.2/624.2) |
| `FA015807` | entity Spawn (NPC/player); name string, e.g. "Trader", "Inexperienced Nemedian" |
| `96C46740` | per-entity registration (paired with Spawn) |
| `66AEDD50` | SpawnObject / world object - names like "Totem Destruction", "loot_chest_04 [QRON]", "Bag of loot" |
| `F096251E` | container/object data (chests, loot bags) - "Bag of loot" |
| `737A6DF9` | inventory/character-state snapshot (huge; tails hold stats/quests) |
| `4A3D899A` | conversation node (category 50002) - quest/NPC dialogue |
| `F164A4A5` | `remoteformat` display XML (category 50001) - dialogue/UI text by id |
| `642CD3D6` | Interact / action (TalkToNpc 0x16, MoveItem 42010/42011, "lootbag_open", ApplySpell-adjacent) |
| `6B87E316` | ApplySpell (cast broadcast: caster + spellId + target + magnitude; spell id @ inner+13) |
| `96B8DC59` | entity vitals update: `0000C350` + entityHandle + `00000000` + count(1) + N×(u32 statId,u32 value) — the combat HP/Stamina/Mana packet (see docs/re/quests-and-combat.md) |
| `4517AA81` | Move (position; floats big-endian) - dominant during gameplay |
| `2008`/`2020`/`2076`/`2052` | spawn-poll handshake (client 2020 polls -> server 2008 SpawnCheck) |
| `201C` | channel handshake (5 registrations) |
| `2002` | overloaded: world-address advertise AND token remoteformats e.g. `TradePostGUI_MailItemBought` (category 629) |
| `2038` | named area / quest title? - "Kill or be Killed", "Loot Goblins", "Six Man Guild...5dead" |
| `203f` | chat / guild broadcast text ("...put in guild bank", "Full Info on ALL items/quests") |
| `20a5` | quest tracker? - repeats the quest title ("Kill or be Killed") |
| `2005`/`2085`/`2082`/`2009` | create-editor + name/region setup family |
| `A36D3B74` | client script callback (create editor) AND "QuestRewards" (quest turn-in rewards) |
| `205a` | gameplay state (frequent during play; unidentified) |
| `04B82B2D`=`n3ToClientQuit` (entity despawn, `protocol_despawn_n3toclientquit`), `D751EBB4`=`StopIIR` (stop moving), `FEC4158A`=`DamageResultIIR` (combat hit); `0802E5D4`/`08006F31`/`0131xxxx` = per-session handle ids (names in `docs/re/aoc-opcode-ids.md`) |

## Per-capture events

### hoxxling — guild_invite_accepted, gameplay, other players visible
`...guild_invite_accepted-random-gameplay-other_playersvisible.json`
- **Guild chat / broadcast**: `0x203f` carries guild text ("Please put in guild bank", "Full Info on ALL items/quests"). (Full chat protocol — ChatServer channels + local say — is in `docs/subsystems/chat.md`.)
- **Loot**: `0x2038` "Loot Goblins"; `0xF096251E` "Bag of loot"; `0x642CD3D6` "lootbag_open" (open a loot bag).
- **Other players**: many `FA015807` spawns + `4517AA81` Move (678) - other player entities streaming in.
- Guild-invite accept packet itself not yet isolated (no distinct "guild invite" sub-op found; likely an Interact/
  remoteformat dialog - look for a `2002`/remoteformat around the accept moment).

### NecroL80 — changemap + accept_quest
`...login-changemap-accept_quest.json`
- **Map change**: two `5D85BFC7` MapChange (see world-burst extraction).
- **Quest accept dialogue**: `4A3D899A` nodes 30031/30033/30039/30040/30041/30045 + `43131/43013/43102/43104`
  with `F164A4A5` remoteformats (18940..18951, 27108/27109) - the quest-giver conversation branches.
- **Quest title**: `0x2038` "Kill or be Killed", "Six Man Guild...5dead"; `0x20a5` repeats "Kill or be Killed".
- Loot chest: `0xF096251E` "loot_chest_04 [QRON]".

### necrol80/assassinl23 — auctionhouse, sell to trader, get mail, totem of origin
`...sell_items_to_trader-intract-with-auctionhouse.json`, `...autionhouse_get_mail-use_totem_of_origin...json`
- **Mail/auction**: `0x2002` `remoteformat token="TradePostGUI_MailItemBought" category="629"` (+ `<parameter value="7;AssassinL23"/>`) - the trade-post/mail UI updates are TOKEN-based remoteformats (not numeric-id), category 629.
- **Trader NPC**: `FA015807` "Trader".
- **Totem of origin** (teleport item → long load): triggers a MapChange + full world burst (the "long loading"); the use itself is an Interact (`642CD3D6`) on the totem item - not yet isolated by sub-op.

### AssassinL23 — resurect
`...randomActions-resurect.json`
- `0x642CD3D6` dominates (3632) - the death/resurrect interactions + the resurrect confirm are Interact actions.
- `FA015807` "Inexperienced Nemedian" (the mob).
- **DECODED 2026-06-15, fully decoded 2026-07-16 (#372):** death → server sends `DispatchValidResurrectionSpots`
  (s2c `0x2024`, GameCharInterface):
  `[refPos 3 floats][u32 (count+1)*0x3F1][ spot(41B): pos 3 floats + spotType + mapId + 0 0 0 + spawnPointId +
  restorationLevel + 0x00 ]*`, ordered nearest-first from refPos (idx616/idx4050, 57 B, both deaths identical bar
  the death pos). ⚠️ The `0x7E2`/`0x0A` this section once called constants are the LIST COUNT and the point's
  `RestorationLevel`, and the `0x00` is per-record — all three indistinguishable from constants in these two
  captures because both carry exactly ONE spot. See docs/subsystems/combat.md. The client shows the release screen, the player
  picks → `RespawnMe` (c2s `0x2024`) = `[u32 spawnPointId]` (the chosen spot echoed back). NOTE: the same `0x2024` is
  sent at LOGIN (the misnamed `SendReadyForPlayScreen`) to set the spawn anchor - it only renders the release UI when
  the client is in the dead state (which needs a real combat killing-blow, not a raw health=0 push). Builder
  (`Faolan.Core.Network/Codecs/Combat/ValidResurrectionSpotsCodec.cs`) + golden test
  (`Faolan.Tests/DispatchValidResurrectionSpotsTests.cs`) shipped; see Gitea #214/#215.

### NecroL80 — the TOMBSTONE and its map pin (#868)

`NecroL80_mage-necromancer_login_gameplay_chat-annotated-4`, plus five more captures that carry the add without the
remove. `--report method-bodies --report-arg WayPoint` and `--report entity-classes --report-arg TombStone` are the
oracles.

**The pin is planted on RESPAWN, not on death.** The whole sequence sits inside 0.7 s, in this order:

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

⚠️ **The one captured `DispatchRemoveWayPoint` is a ZONE CHANGE, not a recovery.** Twenty seconds after the pin goes
up, the player uses a zone-line dynel, answers *"Do you wish to leave? You will be unable to return to this
instance."*, teleports to playfield 1090 and is redirected to another game server — and the pin for playfield 1092
comes down 0.6 s later. That is why `i_nPlayfieldID` is in the remove body at all: **the pin is per-playfield**, and
leaving the playfield drops it.

⚠️ **No capture performs a tombstone RECOVERY**, so what the pickup sends is not known. Open work: Gitea #868.

### ⭐ The pin's ICON is not in the message (#911)

All six captured `DispatchAddWayPoint` frames are tombstones in every field at once — `i_cID`'s class is
`EntityClass.TombStone`, `i_nLabel` is *"Tombstone"*, `i_nToolTip` is the death-penalty line — so the corpus
cannot say which one picks the map icon. It was carried as a `[V0]` with two candidates. **The client says
neither**, which also means the proposed in-client A/B would have come back negative on both arms.

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
  `i_cID` stays what #868 showed it to be — the marker KEY, which is what `DispatchRemoveWayPoint` addresses.

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
  26909→12492→12494 with `F164A4A5` remoteformats 7615/12621/12622/12623/13444/12548/7617. (This is the tree
  `ConversationService` already serves.)
- **Quest rewards / turn-in**: `0xA36D3B74` "QuestRewards".
- **Combat**: `0x6B87E316` ApplySpell casts + `4517AA81` Move; damage/health updates are in entity-state packets
  (candidate `205a`/`04B82B2D`/`D751EBB4` - unconfirmed). Level-up not isolated to a single sub-op yet.

### hoxxling — create (all sliders), long load, chat before exit
`...create-modified_all_sliders-very_long_loading_screen-chat_message_before_exit.json`
- Full create-editor stream: `A36D3B74` script callbacks (77), `2005/2009/2085/2082` editor setup, `96C46740`.
  The editor `0x2000` IIR carrier stream is decoded in `docs/subsystems/create-editor-bursts.md` (dissector
  `CreateEditorRecordTests`): it multiplexes by endpoint-class pair, and the entity full-updates
  reuse the login/world-entry spawn grammar.
- **Chat before exit**: a chat send (`642CD3D6`/text) near the end; chat broadcast is `0x203f` (see guild).
