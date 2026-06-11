# Capture event → packet survey

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
| `96B8DC59` | entity vitals update: `0000C350` + entityHandle + `00000000` + count(1) + N×(u32 statId,u32 value) — the combat HP/Stamina/Mana packet (see docs/quests-and-combat.md) |
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
| `04B82B2D` `D751EBB4` `FEC4158A` `0802E5D4`/`08006F31`/`0131xxxx` | gameplay/entity-update + per-session handle ids (unidentified) |

## Per-capture events

### hoxxling — guild_invite_accepted, gameplay, other players visible
`...guild_invite_accepted-random-gameplay-other_playersvisible.json`
- **Guild chat / broadcast**: `0x203f` carries guild text ("Please put in guild bank", "Full Info on ALL items/quests"). (Full chat protocol — AgentServer channels + local say — is in `docs/chat.md`.)
- **Loot**: `0x2038` "Loot Goblins"; `0xF096251E` "Bag of loot"; `0x642CD3D6` "lootbag_open" (open a loot bag).
- **Other players**: many `FA015807` spawns + `4517AA81` Move (678) - other player entities streaming in.
- Guild-invite accept packet itself not yet isolated (no distinct "guild invite" sub-op found; likely an Interact/
  remoteformat dialog - look for a `2002`/remoteformat around the accept moment).

### necrol80 — changemap + accept_quest
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

### assassinl23 — resurect
`...randomActions-resurect.json`
- `0x642CD3D6` dominates (3632) - the death/resurrect interactions + the resurrect confirm are Interact actions.
- `FA015807` "Inexperienced Nemedian" (the mob). Death→resurrect likely: a health-to-0 state update, a resurrect
  prompt (remoteformat), then a MapChange/teleport to a resurrection point. Specific resurrect opcode not yet
  isolated - candidate: an Interact accept + a 737A6DF9/state refresh after.

### strickenbla — create, combat, levelup, questcomplete
`...create-random-actions-combat-levelup-questcomplete.json`
- **Quest dialogue tree** (the beach Casilda/combat NPC): `4A3D899A` 12493→12490→20018→20019→20020→19919→
  26909→12492→12494 with `F164A4A5` remoteformats 7615/12621/12622/12623/13444/12548/7617. (This is the tree
  `ConversationReplay` already serves.)
- **Quest rewards / turn-in**: `0xA36D3B74` "QuestRewards".
- **Combat**: `0x6B87E316` ApplySpell casts + `4517AA81` Move; damage/health updates are in entity-state packets
  (candidate `205a`/`04B82B2D`/`D751EBB4` - unconfirmed). Level-up not isolated to a single sub-op yet.

### hoxxling — create (all sliders), long load, chat before exit
`...create-modified_all_sliders-very_long_loading_screen-chat_message_before_exit.json`
- Full create-editor stream: `A36D3B74` script callbacks (77), `2005/2009/2085/2082` editor setup, `96C46740`.
- **Chat before exit**: a chat send (`642CD3D6`/text) near the end; chat broadcast is `0x203f` (see guild).

## Open questions (for later dissection)
- Isolate the **guild invite accept**, **resurrect confirm**, **level-up**, and **quest accept/complete state**
  packets to specific sub-opcodes (the remoteformat dialogs are found; the state-change packets are not).
- Decode `0x2038` (quest title/area) and `0x20a5` (quest tracker) bodies.
- Combat damage/health: identify the entity-state update sub-op carrying HP changes (see playerstat work).
- `0x2002 category=629` token remoteformats = the trade-post/mail/auction UI protocol (TradePostGUI_*).
