# Quests & combat — first pass

Initial dissection (2026-06-08) of the quest and combat protocol, from
`Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.json` (4878
packets: create → combat → level-up → quest complete) and the necrol80 `accept_quest` capture. Builds on
`docs/tooling/capture-events.md`. Sub-ops are GameServer `0x2000` body sub-opcodes (`[bodylen4][subop4]...`); handles
are 4-byte entity instances. Marker `0x0000C350` (50256) prefixes most gameplay bodies after the sub-op.

## Combat

The combat loop is **cast → vitals update**:

### `0x6B87E316` — ApplySpell (cast broadcast)
Server broadcasts a cast. Layout (variable length, 26–80 bytes inner):
```
6B87E316 | 0000C350 | <casterHandle:4> | <spellId> | 0000C350 | <targetHandle:4> | ... | <magnitude:4> | ...
```
- caster = `0x01316D8A` (the player's session entity handle in this capture).
- target may equal the caster (self-buff) or a mob handle (e.g. `0x00002C58`).
- trailing 4-byte magnitude observed as `0x2710` (10000), `0x02BC` (700), `0x03E8` (1000) — the effect amount
  (damage / heal / resource), unconfirmed sign/scale.
- the cast spell id is also harvested by `CaptureSpellImporter` (reads spell id at inner+13 of ApplySpell,
  see [[faolan_importer]]).

### `0x96B8DC59` — entity vitals update (the HP/damage packet)
Carries an entity's current vitals as **count-prefixed stat records** — the same `[uint32 statId][uint32 value]`
encoding as the `0x737A6DF9` snapshot (see [[protocol_float_endianness]] sibling doc `docs/subsystems/player-stats.md`):
```
96B8DC59 | 0000C350 | <entityHandle:4> | 00000000 | <count:1> | count × ( <statId:u32> <value:u32> )
```
Example: count=3 → `001B`(Health) `01F8`(Stamina) `01FA`(Mana), all `0x189C`=6300 → 63.00 (×100 vitals scale).
Tracing entity `0x01316D8A` (the player) across the capture: Health 80 → 130 → 195 as they level up — i.e. this
packet is the authoritative vitals broadcast; combat damage/heals land here as updated Health values.

**This also resolves the stat-block framing open question in `docs/subsystems/player-stats.md`: stat records are
count-prefixed (a 1-byte count, then N × 8-byte records).** Confirmed lengths: count 1/3/6 → inner 25/41/65.

**Implemented** (2026-06-08): `CharacterStatBlock` (`Faolan.Core.Network/Codecs/FullCharSnapshot/CharacterStatBlock.cs`) builds the
count-prefixed record block (applies the ×100 vitals / ×10 attribute scale); `SendStatUpdate` / `SendVitals`
(`GameServerListener.Outbound.cs`, wire codec `Faolan.Core.Network/Codecs/World/StatUpdateCodec.cs`) write the full packet. `SendVitals(client)` sources Health/Stamina/Mana from the
character's persisted **`CharacterMaxStats`** (now an active DbSet/table with a 1-1 cascade to Character,
schema since squashed into the `InitialCreate` migration), seeded on creation via `CharacterMaxStats.CreateDefaults(class, level)` —
rough class/level defaults anchored on assassinl23 (Assassin lvl 23 ≈ 1281/1143/73 vs sheet 1270/1148/74); legacy
characters with no row fall back to computed defaults in-memory. The create-editor commits the chosen class
before naming, so `CommitCharacterName` calls `Character.RegenerateMaxStats()` (updates the seeded row in place)
to replace the level-1 rogue baseline with the chosen class's vitals. (The `.setstat` / `.setstatrange` / `.vitals`
GM commands used for live stat testing have since been removed.) Round-trip tests
(`CharacterStatBlockTests`, `CharacterMaxStatsTests`, `DatabaseRepositoryTests.CreateCharacter_SeedsDefaultMaxStats`)
cover bytes, defaults, and cascade. `ApplySpell` (0x6B87E316) was already implemented (caster + `0000` + spellId +
`0000C350` + target + data); its builders now live in `Faolan.Core.Network/Codecs/Combat/` (`SelfBuffCodec`,
`NpcSpellCastBuilder`, `WorldDeath`).

**Combat loop implemented** (2026-06-08): `Faolan/GameServer/Combat/` — `EntityVitals` (live current+max HP/
Mana/Stamina, clamped deltas), `CombatState` (in-memory per-handle vitals store; world-authoritative, never
persisted), `CombatResolver` (provisional level-scaled damage; spell power/mitigation TBD). The client's
`CastSpell` interact (`ActionType 0x4`) now broadcasts the `0x6B87E316` ApplySpell and, for a cast at another
entity, calls `ResolveOffensiveCast` → applies damage to the target's vitals → pushes a `0x96B8DC59` update so
its health bar moves. Death is detected (HP→0) but despawn + XP are TODO (packet not decoded). GM test commands
`.hurt [n]` / `.heal [n]` / `.spelltest <id>` have since been removed (the `.kill` command now drives the death
path). Tests: `CombatTests`.

**Melee auto-attack — a BehaviorControlCenter FSM** (2026-06-11). NormalAttack = `0x642CD3D6` Interact actionType
**0xf**, not a one-shot: the client sends one swing per press (args field2 = swing counter; the attack direction
ability 769094/95/99 cycles in the request tail; the target is `args.SpellTarget.field2`). The server's BccAck
(`0x642CD3D6` download) echoes the request args **with field3 (a monotonic "phase" counter) inserted after
field2**, plus a result block (constant `0x06 000000CB`). `SendNormalAttackResponse` builds this; the field3
splice (`InsertBehaviorPhaseField`) reproduces the captured swing-1 ack args byte-for-byte (`BehaviorPhaseFieldTests`).
- **Fixed 2026-06-11:** the ack was reading argsLen as a u16 at offset 22 (it's a u32 → high half 0), so it echoed
  EMPTY args and desynced the FSM after one swing ("attack runs once, then random / hits self"). Now reads u32@22,
  args@26.
- **NPC combat HP** now comes from the NPC's live world entity: a burst/interest-spawned NPC carries its real
  vitals in the simulation (`WorldEntity.Vitals`, seeded in `WorldBroadcaster` from the spawn's `npc.Health`,
  falling back to `CombatResolver.NpcMaxHealthForLevel`), so a damaged mob's bar matches the HP the client
  rendered; `GetOrAddNpcVitals` only falls back to `CombatResolver.DefaultNpcMaxHealth` for an NPC with no world
  entity (the built-NPC test).
- **Still TODO (needs in-client + a multi-swing capture):** retail also sends a SECOND ack for the NPC's OWN
  behaviour (actor = target handle) that drives the mob's counter-attack, plus a multi-result block — not yet
  reproduced, so mobs may not visibly fight back. The phase counter's absolute start is also unconfirmed (the
  capture began mid-session at 3; ours starts at 1 — monotonic is what matters). See TODO #3.

### Mounts (summon spell found)
Decoded from demol80's `...very_long_loading_screen_after_field_change` capture (the player rides: 122 MountedRun
+ 19 MountedJump moves). The summon sequence right before the player switches to `MovingType.MountedRun` (0x010A,
first mounted move at idx 787):
- **a channeled `0x6B87E316` ApplySpell with spell id `0x3BAD7D`** (3911037), repeated ~11× paired with
  `0xF508F4C1` (the cast bar) — packets 741–762;
- then the mount entity registers (`0x96C46740`, idx 780–781) — the mount is a **`0xFA015807` entity** (names seen:
  "Fading Black Horse", "Armored Rabicano Horse", handles `0x0004xxxx`);
- then the player's Move switches to MountedRun.

So the mount spell is **`0x3BAD7D`** (present in the `Spells` table; names are client-side). A GM command
`.mount [spellIdHex]` (since removed) broadcast that ApplySpell on the player (default `0x3BAD7D`). NOTE: full mounting also
needs the mount entity spawn (`0xFA015807`) + the MountedRun transition; only the cast broadcast was wired.
`0x96B8DC59` is NOT the mount packet (it's the vitals update). NOTE: the "Black Horse" strings in `0x2038` are
**guild names** ("Black Horse Company"), not mounts - a red herring.

### Other combat traffic (not yet decoded)
`0x4517AA81` Move; entity-update sub-ops `0xFEC4158A`, `0xD751EBB4`, `0x04B82B2D`, `0x40749B8A` (high volume
during combat, carry per-entity handles — animation/aggro/state, unconfirmed); `0xF98E10B3` (handle +
slowly-incrementing 4-byte counter — per-entity sequence/tick, NOT confirmed as exp despite an earlier
false-positive); client action `0x01316D8A` upload (same value as the player handle — the player's action
channel).

## Loot

Decoded from `hoxxling-login-gameplay.json` (see `protocol_loot_interaction`). Loot bags use **typetag `0xC87A`**
(not the `0xC350` the entity family uses). **The UX is a loot WINDOW + click-to-take** (an earlier "auto-loot on use"
read was wrong — the take requests are top-level opcodes `0x2043`/`0x2044`, not `0x2000`). Full sequence:

- spawn `LootBagFull` (`0xF096251E`) carries the bag **and its item contents**; client `LookAt` (`0x0D155695`) selects
  it; `Interact UseObject` (`0x12`) opens it.
- server `NewCurrentLooter` (`0xD06568C8`) + **`SimpleDynelVisualChange` (`0xE7F4F8F8`) "Open"** → the loot window opens
  (the bag stays).
- client clicks item → **`0x2043`** (take) → server `LootRemoveItems` (`0x70010162`). **There is NO server
  add-item packet** — the client adds the looted item to the bag locally from the `LootBagFull` it already has.
  (CORRECTION: `0x79F8C7A1`/`0x63268258` are **quest** packets, QuestJournalIIR/QuestFullUpdateIIR — NOT
  ContainerAddItem. The only server inventory-delivery is the world-entry `0x737A6DF9` snapshot; see
  memory `protocol_containeradditem`.)
- client **`0x2044`** (close) → bag `n3ToClientFade` (`0x66180B20`) then `n3ToClientQuit` (`0x04B82B2D`).

Per-packet byte layouts (server→client):

1. **`0xF096251E` `LootBagFullUpdateIIR_c`** (spawn, server→client) — a **world object** (`0x2C` transform group +
   object id + name "Chest [N7FU]"/"Bag of loot" + a `remoteformat … category=50026` TreasureChestScript). Built by
   `WorldObjectBuilder` already. **Contents are not in this body** — they arrive on open.
2. **`0x642CD3D6` Interact** (open, client→server) — `… [playerHandle] [seq] 00000012 [pbLen] <protobuf> [c87a]
   [bagHandle] 00000001`, where `0x12`=18 = `startobjectaction(18)` = USE; the bag's typetag+handle ride the tail. A
   larger variant carries the full `lootbag_open` TreasureChestScript remoteformat; the server echoes it.
3. **`0xD06568C8` `NewCurrentLooterIIR_c`** + **`0x1E2E0139` `NewAllowedLooterIIR_c`** (server→client, 21 B):
   `<subop> [c87a] [bagHandle] 00 0000c350 [playerHandle]` = "bag → current/allowed looter = this player".
4. **Remove** — **`0x70010162` `LootRemoveItemsIIR_c`** (25 B): `<subop> [c87a] [bagHandle] 00000000 [slot/count]
   [5B tail]` removing the item from the bag. No separate "take" upload → Tortage starter loot is **auto-loot on
   use**. The item lands in the player's bag **client-side** (no server add-item packet; the snapshots
   `0x79F8C7A1`/`0x63268258` seen here are quest updates, not an inventory grant).
5. **`0x66180B20` `n3ToClientFadeIIR_t`** (fade, 17 B): `<subop> [c87a] [bagHandle] 00 [float fadeSeconds]` (e.g.
   `40400000`=3.0). The **graceful despawn** variant: marker `0x00` + a FLOAT fade-time, vs `n3ToClientQuit`'s marker
   `0x01` + u32 playfield token (`protocol_despawn_n3toclientquit`). Good fit for loot expiry (and possibly death).

**Gate / path:** the open is mediated by an RDB Lua `TreasureChestScript` (same VM PINNED for conversations), but is
**bypassable** like conversations — answer the use-Interact directly with NewCurrentLooter → LootRemoveItems → fade
without running the real script. The looted item appears in the bag **client-side**; persisting it server-side means
writing the `CharacterInventory` row on the take and re-sending the post-load `0x737A6DF9` snapshot (the only
inventory-delivery mechanism — see memory `protocol_containeradditem`).

## Quests

### Dialogue (quest giver conversation)
- `0x4A3D899A` — conversation nodes (category 50002): the quest-giver branch tree. Already served by
  `ConversationReplay` and imported to `ConversationNode`/`ConversationPacket` ([[faolan_importer]]).
- `0xF164A4A5` — `remoteformat` display XML (category 50001): the dialogue/UI text by id.
- necrol80 accept_quest tree: nodes 30031/30033/30039/… ; strickenbla beach tree: 12493→12490→…→12494.

### Accept / interact
- `0x642CD3D6` Interact carries the talk-to-NPC / accept actions (ActionType `0x16` = TalkToNpc). The discrete
  "accept" vs "decline" action isn't yet isolated to its own field.

### Rewards / turn-in
- `0xA36D3B74` — client-script callback carrying the **`QuestRewards`** token and the quest-giver script name
  (e.g. `coachCasilda`). This is the quest-complete / reward-grant packet (also used for create-editor script
  callbacks — overloaded by token string).
- `0xFA015807` — the quest-giver NPC spawn (e.g. "Casilda").

### Quest state / journal (DECODED 2026-06-11)
Quest state is driven by four IIR records (decoded from `hoxxling-login-gameplay`, quest `0x34A7`=13479):
- **`0x97E02314` `QuestIIR`** (add/update/remove): `[ack][playerHandle][0][0x01][u32 state][u32 questId][0]` —
  state `2` = active/added (the only confirmed code). 29 B.
- **`0x0774C8C1` `QuestGoal`**: `[ack][playerHandle][0x01][u32 questId][u32 goalId][u32 count][0][0]` — objective
  progress. 33 B.
- **`0x63268258` `QuestFullUpdate`** / **`0x79F8C7A1` `QuestJournal`**: questId + `remoteformat` text refs
  (categories 50300–50302); the client renders the title/description/journal text from its RDB **by id**, so the
  server only sends ids + state, never the text.

**Built:** `WorldQuest` (byte-exact `QuestIIR`/`QuestGoal` builders) + a `CharacterQuest` table (per-character
quest id + state + goal progress; schema since squashed into the `InitialCreate` migration) + repo upsert/remove +
restore-on-login. ⚠️ That live restore path (`RestorePlayerQuests`) was DELETED on 2026-07-20: retail sends no
quest packet at all before the first in-session accept, and both journal tabs ride the login snapshot instead
(#389/#390). This is the quest STATE foundation. **Objective auto-tracking on kill is
wired**: `CharacterQuest.GoalTargetName` (+ `GoalProgress`/`GoalRequired`) holds a kill objective; the NPC-death
path calls `QuestProgressionService.AdvanceKillGoals`, which (via `QuestProgress.Advances`) bumps any active quest
whose target matches the slain NPC, pushes `QuestGoal`, persists, and completes at the required count (state +
system message). The objective target/count now comes from the imported `QuestDefinition`/`QuestGoalDefinition`
tables (the quest engine has since grown loot/talk goals and multi-stage chains — `QuestProgressionService`).
Remaining VM pieces: accept-from-dialogue trigger, rewards/turn-in (`0xA36D3B74 QuestRewards`), branching,
QuestFullUpdate replay ([[protocol_conversation_routing]]).

**Accept-from-dialogue is VM-gated (checked 2026-06-11).** In `hoxxling-login-gameplay` the `QuestIIR(add)` for quest
`0x34A7` follows a chain of quest-giver Interacts; the client's accept Interact (`actionType 0x0F`) carries **no quest
id** — `0x34A7` appears nowhere in it. So the server grants the quest from data it already holds (this NPC's
conversation grants quest X) = the quest-giver→quest definition, which is the pinned RDB Lua quest VM. Wiring accept
without the VM would require a capture-mined NPC→quest map (yields only the 1–2 captured quests) — low value. The quest
ENGINE (state + kill-objective tracking) is done. **Update:** accept-from-dialogue has since been wired *without* the
live Lua VM — a per-node `ConversationNode.GrantQuestId` column (the data-mined NPC→quest map above) is granted when
its branch is reached (`ConversationService`), and objectives/turn-in run off the imported `QuestDefinition` /
`QuestGoalDefinition` tables.

## Next steps
- Confirm ApplySpell magnitude sign/scale and whether damage is in the cast or only in the following vitals
  update.
- Decode the entity-update sub-ops (`FEC4158A`/`D751EBB4`/`04B82B2D`) — likely animation/aggro/buff state.
- Isolate quest-accept vs decline, and the quest-log/objective state packet.
- Find the level-up packet (the capture has a level-up; Level `0x36` rises in the vitals/snapshot but the
  "ding" event packet isn't isolated).
