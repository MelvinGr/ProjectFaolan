# Quests & combat — capture notes

Dissection of the quest and combat protocol from
`Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.json` (4878 packets: create →
combat → level-up → quest complete), the NecroL80 `accept_quest` capture and `hoxxling-login-gameplay`. Builds on
`docs/tooling/capture-events.md`. Sub-ops are GameServer `0x2000` body sub-opcodes (`[bodylen4][subop4]...`);
handles are 4-byte entity instances. The class word `0x0000C350` (50000) prefixes most gameplay bodies after the
sub-op.

## Combat

The combat loop is **cast → vitals update**.

### `0x6B87E316` — ApplySpell (cast broadcast)
Server broadcasts a cast. Layout (variable length, 26–80 bytes inner):
```
6B87E316 | 0000C350 | <casterHandle:4> | <spellId> | 0000C350 | <targetHandle:4> | ... | <magnitude:4> | ...
```
- caster = `0x01316D8A` (the player's session entity handle in this capture).
- target may equal the caster (self-buff) or a mob handle (e.g. `0x00002C58`).
- trailing 4-byte magnitude observed as `0x2710` (10000), `0x02BC` (700), `0x03E8` (1000) — the effect amount
  (damage / heal / resource), sign/scale unconfirmed.
- the cast spell id is harvested by `CaptureSpellImporter` (spell id at inner+13 of ApplySpell).
- the builders live in `Faolan.Core.Network/Codecs/Combat/` (`MagicBuffDataCodec`, `MagicBuffCodec`,
  `DeathSequence`).

### `0x96B8DC59` — entity vitals update (the HP/damage packet)
Carries an entity's current vitals as **count-prefixed stat records** — the same `[uint32 statId][uint32 value]`
encoding as the `0x737A6DF9` snapshot (see `docs/subsystems/player-stats.md`):
```
96B8DC59 | 0000C350 | <entityHandle:4> | 00000000 | <count:1> | count × ( <statId:u32> <value:u32> )
```
Example: count=3 → `001B`(Health) `01F8`(Stamina) `01FA`(Mana), all `0x189C`=6300 → 63.00 (×100 vitals scale).
Tracing entity `0x01316D8A` (the player) across the capture: Health 80 → 130 → 195 as they level up — this packet
is the authoritative vitals broadcast; combat damage/heals land here as updated Health values. Stat records are
count-prefixed (a 1-byte count, then N × 8-byte records): count 1/3/6 → inner 25/41/65.

Implementation: `CharacterStatBlock` (`Faolan.Core.Network/Codecs/FullCharSnapshot/CharacterStatBlock.cs`) builds
the count-prefixed block (applying the ×100 vitals / ×10 attribute scale) and `StatUpdateCodec`
(`Faolan.Core.Network/Codecs/World/StatUpdateCodec.cs`) the packet. A vitals push always carries the whole
`EntityVitals.ToVitalsBlock` — current **and** max: the max changes after world entry (level-up, gear), and a push
of the current ids alone leaves the bar pinned to the seed max. A path that changes a max calls
`CombatService.ReseedFromMaxStats` first.

The max vitals are not stored: `Character.MaxVitals` computes them from (Class, Level) and the character's
attributes via `CharacterMaxStats.For`, so an imported character reports the same ceiling as a created one of the
same class and level, and nothing needs re-syncing when Level changes. The curve is the client's own
`HitsManaAndStamina_c` table (RDB 1000511) — its base/growth words as `CharacterMaxStats.ClassBaseHundredths`,
which reproduces all 16 captured level-up pushes exactly, plus its attribute weights as
`CharacterMaxStats.AttributeScaledHundredths`. Together they reproduce every retail level-1 sheet across all twelve
classes and the captured pools of the four level-23-to-80 characters (see `docs/rdb/client-data-tables.md`).
Stat 1 moves the health bar's max; stat 27 (`HealthCurrent`) moves nothing the bar draws. Tests:
`CharacterStatBlockTests`, `CharacterMaxStatsTests`.

### The combat loop (server side)
`Faolan/GameServer/Combat/` — `EntityVitals` (live current+max HP/Mana/Stamina, clamped deltas), `CombatState`
(in-memory per-handle vitals store; world-authoritative, never persisted), `CombatResolver`. The client's
`Magic:Attack` interact (`BehaviorId` 0x4) broadcasts the `0x6B87E316` ApplySpell and, for a cast at another
entity, `ResolveOffensiveCast` applies damage to the target's vitals and pushes a `0x96B8DC59` update so its health
bar moves. An NPC's combat HP comes from its live world entity (seeded from the spawn's `npc.Health`, falling back
to `CombatResolver.NpcMaxHealthForLevel`), so a damaged mob's bar matches the HP the client rendered. Tests:
`CombatTests`.

### Melee auto-attack — a BehaviorControlCenter FSM
`Combat:Melee` = `0x642CD3D6` behaviorId **0xf**, not a one-shot: the client sends one swing per press (args
field2 = swing counter; the attack direction ability 769094/95/99 cycles in the request tail; the target is
`args.SpellTarget.field2`). The server's BccAck (`0x642CD3D6` download) echoes the request args **with field3 (a
monotonic "phase" counter) inserted after field2**, plus a result block (constant `0x06 000000CB`).
`SendNormalAttackResponse` builds this; the field3 splice (`InsertBehaviorPhaseField`) reproduces the captured
swing-1 ack args byte-for-byte (`BehaviorPhaseFieldTests`).

The request's argsLen is a **u32** at offset 22 (args at 26). Read as a u16 its high half is 0, the ack echoes empty
args, and the FSM desyncs after one swing.

### Mounts
From DemoL80's `...very_long_loading_screen_after_field_change` capture (the player rides: 122 mounted-run + 19
mounted-jump moves), the summon sequence right before the player's first mounted move (idx 787):
- **a channeled `0x6B87E316` ApplySpell with spell id `0x3BAD7D`** (3911037), repeated ~11× paired with
  `0xF508F4C1` — packets 741–762;
- then the mount entity registers (`0x96C46740`, idx 780–781) — the mount is a **`0xFA015807` entity** (names seen:
  "Fading Black Horse", "Armored Rabicano Horse", handles `0x0004xxxx`);
- then the player's moves switch to the mounted form.

The "Black Horse" strings in `0x2038` are **guild names** ("Black Horse Company"), not mounts. See
`docs/subsystems/mounts.md` for the implementation.

### Other combat traffic
High-volume during combat: `0x4517AA81` `CharDCMoveIIR` ([chardcmove-grammar.md](chardcmove-grammar.md)),
`0xD751EBB4` `StopIIR`, `0xFEC4158A` `DamageResultIIR`, `0x40749B8A` `EnergyIIR`, `0x04B82B2D` `n3ToClientQuit`
and `0xF98E10B3` `StatIIR` (handle + stat pairs, not an XP counter).

## Loot

From `hoxxling-login-gameplay.json`. Loot bags use **typetag `0xC87A`** (not the `0xC350` the entity family uses).
The UX is a loot **window** + click-to-take; the take requests are top-level opcodes `0x2043`/`0x2044`, not `0x2000`.
Sequence:

- spawn `LootBagFull` (`0xF096251E`); client `LookAt` (`0x0D155695`) selects it; `Interact UseObject` (`0x12`)
  opens it.
- server `NewCurrentLooter` (`0xD06568C8`) + **`SimpleDynelVisualChange` (`0xE7F4F8F8`) "Open"** → the loot window
  opens (the bag stays).
- client clicks item → **`0x2043`** (take) → server `LootRemoveItems` (`0x70010162`). No server add-item packet
  follows in the captures — the client adds the looted item to its bag locally. (`0x79F8C7A1`/`0x63268258` seen
  around a loot are **quest** packets, QuestJournalIIR/QuestFullUpdateIIR, not an inventory grant.)
- client **`0x2044`** (close) → bag `n3ToClientFade` (`0x66180B20`) then `n3ToClientQuit` (`0x04B82B2D`).

Per-packet byte layouts (server→client):

1. **`0xF096251E` `LootBagFullUpdateIIR_c`** (spawn) — a **world object** (`0x2C` transform group + object id +
   name "Chest [N7FU]"/"Bag of loot" + a `remoteformat … category=50026` TreasureChestScript). Built by
   `WorldObjectCodec`. **Contents are not in this body** — they arrive on open.
2. **`0x642CD3D6` Interact** (open, client→server) — `… [playerHandle] [seq] 00000012 [pbLen] <protobuf> [c87a]
   [bagHandle] 00000001`, where `0x12`=18 = `startobjectaction(18)` = use; the bag's typetag+handle ride the tail. A
   larger variant carries the full `lootbag_open` TreasureChestScript remoteformat; the server echoes it.
3. **`0xD06568C8` `NewCurrentLooterIIR_c`** + **`0x1E2E0139` `NewAllowedLooterIIR_c`** (21 B):
   `<subop> [c87a] [bagHandle] 00 0000c350 [playerHandle]` = "bag → current/allowed looter = this player".
4. **`0x70010162` `LootRemoveItemsIIR_c`** (25 B for one slot):
   `<subop> [c87a] [bagHandle] [u8 env] [u32 cash] [ListFrame count] [u32 pos …]`, removing what was taken from the
   bag window. `LootRemoveItemsIIR_c::ReadSubClass` (`FUN_007A5950`) is one `u32` into `this+0x3c` and then the
   generic count-framed list reader (`FUN_004D4EF0`, `word/0x3F1 - 1`).
   **That `u32` is the cash.** On the eight cash loots in
   `xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` it equals the `DispatchUpdateCash` delta exactly
   (21, 1, 30, 1, 3, 1, 3, 36), and across all 148 captured records it is a strict alternative to the position list:
   a cash take sends `[cash=N][empty list]`, an item take `[cash=0][the slots]`, never both. A split one byte out of
   step is invisible while the cash is zero and makes every cash record unreadable.
5. **`0x66180B20` `n3ToClientFadeIIR_t`** (fade, 17 B): `<subop> [c87a] [bagHandle] 00 [float fadeSeconds]` (e.g.
   `40400000`=3.0). The **graceful despawn** variant: marker `0x00` + a float fade-time, vs `n3ToClientQuit`'s marker
   `0x01` + u32 playfield token.

The open is mediated by an RDB Lua `TreasureChestScript`, but is **bypassable** like conversations — answer the
use-Interact directly with NewCurrentLooter → LootRemoveItems → fade without running the real script.

## Quests

### Dialogue (quest giver conversation)
- `0x4A3D899A` `KnubotAppendTextIIR` — the quest-giver dialogue lines (category 50002), imported to
  `ConversationNode`/`ConversationPacket`.
- `0xF164A4A5` `KnubotAnswerListIIR` — the `remoteformat` answer XML (category 50001): the dialogue/UI text by id.
- NecroL80 accept_quest tree: nodes 30031/30033/30039/… ; strickenbla beach tree: 12493→12490→…→12494.

### Accept / interact
`0x642CD3D6` Interact carries the talk-to-NPC / accept actions (`BehaviorId` `0x16` = UseCharacter). In
`hoxxling-login-gameplay` the `QuestIIR(add)` for quest `0x34A7` follows a chain of quest-giver Interacts; the
client's accept Interact (`actionType 0x0F`) carries **no quest id** — `0x34A7` appears nowhere in it. So the server
grants the quest from data it already holds: `ConversationEdge.GrantQuestId` (the data-mined NPC→quest map) is
granted when its branch is reached (`ConversationService`), and objectives/turn-in run off the imported
`QuestDefinition` / `QuestGoalDefinition` tables.

### Rewards / turn-in
- `0xA36D3B74` `ScryScriptIIR` — client-script callback carrying the **`QuestRewards`** token and the quest-giver
  script name (e.g. `coachCasilda`). This is the quest-complete / reward-grant packet (also used for create-editor
  script callbacks — overloaded by token string).
- `0xFA015807` — the quest-giver NPC spawn (e.g. "Casilda").

### Quest state / journal
Quest state is driven by four IIR records (decoded from `hoxxling-login-gameplay`, quest `0x34A7`=13479):
- **`0x97E02314` `QuestIIR`** (add/update/remove): `[ack][playerHandle][0][0x01][u32 state][u32 questId][0]` —
  state `2` = active/added (the only confirmed code). 29 B.
- **`0x0774C8C1` `QuestGoal`**: `[ack][playerHandle][0x01][u32 questId][u32 goalId][u32 w1][u32 w2][u32 w3]` —
  what just happened to the goal. 33 B. The three trailing words are one value, the update kind
  (`QuestGoalUpdate`), and carry no running count — the journal holds that. Only three combinations exist in the
  corpus: `(1,0,0)` **Satisfied** (the goal is met), `(2,0,1)` **Progress** (a multi-count goal moved but is not
  met), `(3,2,0)` **Advanced** (the goal is closed and the quest moves to its next goal). Advanced always sits
  between this goal's Satisfied and the next goal's Satisfied, which is what identifies it as a hand-off marker.
  `[V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json — with Hoxxling_mage-heraldofxotli,
  66 QuestGoalIIR packets over the whole corpus: 52 Satisfied / 4 Progress / 10 Advanced, no other combination]`
- **`0x63268258` `QuestFullUpdate`** / **`0x79F8C7A1` `QuestJournal`**: questId + `remoteformat` text refs
  (categories 50300–50302); the client renders the title/description/journal text from its RDB **by id**, so the
  server only sends ids + state, never the text.

Retail sends no quest packet at all before the first in-session accept; both journal tabs ride the login snapshot.
`QuestCodec` / `QuestGoalCodec` are the byte-exact codecs, and the `CharacterQuest` table holds per-character quest
id + state + goal progress. Kill objectives: `CharacterQuest.GoalTargetName` (+ `GoalProgress`/`GoalRequired`); the
NPC-death path calls `QuestProgressionService.AdvanceKillGoals`, which bumps any active quest whose target matches
the slain NPC, pushes `QuestGoal`, persists, and completes at the required count. See `docs/subsystems/quests.md`.
