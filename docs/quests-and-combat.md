# Quests & combat — first pass

Initial dissection (2026-06-08) of the quest and combat protocol, from
`account-stricken998-character-strickenbla-create-random-actions-combat-levelup-questcomplete.json` (4878
packets: create → combat → level-up → quest complete) and the necrol80 `accept_quest` capture. Builds on
`docs/capture-events.md`. Sub-ops are GameServer `0x2000` body sub-opcodes (`[bodylen4][subop4]...`); handles
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
encoding as the `0x737A6DF9` snapshot (see [[protocol_float_endianness]] sibling doc `docs/player-stats.md`):
```
96B8DC59 | 0000C350 | <entityHandle:4> | 00000000 | <count:1> | count × ( <statId:u32> <value:u32> )
```
Example: count=3 → `001B`(Health) `01F8`(Stamina) `01FA`(Mana), all `0x189C`=6300 → 63.00 (×100 vitals scale).
Tracing entity `0x01316D8A` (the player) across the capture: Health 80 → 130 → 195 as they level up — i.e. this
packet is the authoritative vitals broadcast; combat damage/heals land here as updated Health values.

**This also resolves the stat-block framing open question in `docs/player-stats.md`: stat records are
count-prefixed (a 1-byte count, then N × 8-byte records).** Confirmed lengths: count 1/3/6 → inner 25/41/65.

**Implemented** (2026-06-08): `CharacterStatBlock` (`Faolan/GameServer/Packets/0x737A6DF9_Test/`) builds the
count-prefixed record block (applies the ×100 vitals / ×10 attribute scale); `SendStatUpdate` / `SendVitals`
(`Send0x2000_0x96B8DC59.cs`) write the full packet. `SendVitals(client)` sources Health/Stamina/Mana from the
character's persisted **`CharacterMaxStats`** (now an active DbSet/table with a 1-1 cascade to Character,
migration `AddCharacterMaxStats`), seeded on creation via `CharacterMaxStats.CreateDefaults(class, level)` —
rough class/level defaults anchored on assassinl23 (Assassin lvl 23 ≈ 1281/1143/73 vs sheet 1270/1148/74); legacy
characters with no row fall back to computed defaults in-memory. The create-editor commits the chosen class
before naming, so `CommitCharacterName` calls `Character.RegenerateMaxStats()` (updates the seeded row in place)
to replace the level-1 rogue baseline with the chosen class's vitals. GM commands: `.vitals` (no args → character's
own stats; or `.vitals <h> [st] [m]`) and `.setstat <statIdHex> <value>` for live testing. Round-trip tests
(`CharacterStatBlockTests`, `CharacterMaxStatsTests`, `DatabaseRepositoryTests.CreateCharacter_SeedsDefaultMaxStats`)
cover bytes, defaults, and cascade. `ApplySpell` (0x6B87E316) was already correctly implemented in `Packets.cs`
(caster + `0000` + spellId + `0000C350` + target + data).

**Combat loop implemented** (2026-06-08): `Faolan/GameServer/Combat/` — `EntityVitals` (live current+max HP/
Mana/Stamina, clamped deltas), `CombatState` (in-memory per-handle vitals store; world-authoritative, never
persisted), `CombatResolver` (provisional level-scaled damage; spell power/mitigation TBD). The client's
`CastSpell` interact (`ActionType 0x4`) now broadcasts the `0x6B87E316` ApplySpell and, for a cast at another
entity, calls `ResolveOffensiveCast` → applies damage to the target's vitals → pushes a `0x96B8DC59` update so
its health bar moves. NPC targets are lazily seeded with a placeholder max (real NPC HP not decoded). Death is
detected (HP→0) but despawn + XP are TODO (packet not decoded). GM test commands: `.hurt [n]` / `.heal [n]`
(adjust own vitals + push update), `.spelltest <id>` (self-cast). Tests: `CombatTests`.

### Mounts (summon spell found)
Decoded from demol80's `...very_long_loading_screen_after_field_change` capture (the player rides: 122 MountedRun
+ 19 MountedJump moves). The summon sequence right before the player switches to `MovingType.MountedRun` (0x010A,
first mounted move at idx 787):
- **a channeled `0x6B87E316` ApplySpell with spell id `0x3BAD7D`** (3911037), repeated ~11× paired with
  `0xF508F4C1` (the cast bar) — packets 741–762;
- then the mount entity registers (`0x96C46740`, idx 780–781) — the mount is a **`0xFA015807` entity** (names seen:
  "Fading Black Horse", "Armored Rabicano Horse", handles `0x0004xxxx`);
- then the player's Move switches to MountedRun.

So the mount spell is **`0x3BAD7D`** (present in the `Spells` table; names are client-side). GM command
**`.mount [spellIdHex]`** broadcasts that ApplySpell on the player (default `0x3BAD7D`). NOTE: full mounting also
needs the mount entity spawn (`0xFA015807`) + the MountedRun transition; only the cast broadcast is wired so far.
`0x96B8DC59` is NOT the mount packet (it's the vitals update). NOTE: the "Black Horse" strings in `0x2038` are
**guild names** ("Black Horse Company"), not mounts - a red herring.

### Other combat traffic (not yet decoded)
`0x4517AA81` Move; entity-update sub-ops `0xFEC4158A`, `0xD751EBB4`, `0x04B82B2D`, `0x40749B8A` (high volume
during combat, carry per-entity handles — animation/aggro/state, unconfirmed); `0xF98E10B3` (handle +
slowly-incrementing 4-byte counter — per-entity sequence/tick, NOT confirmed as exp despite an earlier
false-positive); client action `0x01316D8A` upload (same value as the player handle — the player's action
channel).

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

### Quest state / journal (open)
No obvious ASCII quest-state block found; the journal/objective tracking is likely numeric quest ids in a
state packet (candidate: a `0x2038`/`0x20a5` quest-title/tracker pair, see `docs/capture-events.md`). Not yet
isolated.

## Next steps
- Confirm ApplySpell magnitude sign/scale and whether damage is in the cast or only in the following vitals
  update.
- Decode the entity-update sub-ops (`FEC4158A`/`D751EBB4`/`04B82B2D`) — likely animation/aggro/buff state.
- Isolate quest-accept vs decline, and the quest-log/objective state packet.
- Find the level-up packet (the capture has a level-up; Level `0x36` rises in the vitals/snapshot but the
  "ding" event packet isn't isolated).
